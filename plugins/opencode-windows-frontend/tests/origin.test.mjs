import assert from "node:assert/strict";
import { test } from "node:test";
import { billionOrigin, normalizeOrigin } from "../src/billion-origin.ts";

const ready = (origin) => ({ protocolVersion: 1, status: "ready", origin });
const unavailable = (reason = "not-ready") => ({ protocolVersion: 1, status: "unavailable", reason });

const accepted = [
  ["http://127.0.0.1:18788", "http://127.0.0.1:18788"],
  ["http://127.0.0.1:1/", "http://127.0.0.1:1"],
  ["http://127.0.0.1:80", "http://127.0.0.1:80"],
  ["http://localhost:18788/", "http://localhost:18788"],
  ["http://localhost:65535", "http://localhost:65535"],
  ["http://[::1]:18788", "http://[::1]:18788"],
  ["http://[::1]:1/", "http://[::1]:1"],
];

for (const [input, expected] of accepted) {
  test(`accept raw origin ${input}`, () => assert.equal(normalizeOrigin(input), expected));
}

const rejected = [
  undefined, null, 1, {}, "",
  "http://127.0.0.1:080", "http://127.0.0.1:00", "http://127.0.0.1:0",
  "http://127.0.0.1:65536", "http://127.0.0.1:99999999999999999999",
  "http://127.1:1", "http://0x7f.0.0.1:1", "http://2130706433:1",
  "http://0177.0.0.1:1", "http://127.000.000.001:1", "http://127.0.0.2:1",
  "http://[0:0:0:0:0:0:0:1]:1", "http://[::ffff:127.0.0.1]:1",
  "http://localhost.:1", "http://LOCALHOST:1", "http://localhost.evil:1",
  "http://user@127.0.0.1:1", "http://user:password@127.0.0.1:1", "http://@localhost:1",
  "http://127.0.0.1:1/x", "http://127.0.0.1:1/.", "http://127.0.0.1:1/..",
  "http://127.0.0.1:1//", "http://127.0.0.1:1/?a", "http://127.0.0.1:1?",
  "http://127.0.0.1:1#f", "http://127.0.0.1:1/#", "https://127.0.0.1:1",
  "HTTP://127.0.0.1:1", "//127.0.0.1:1", "http://127.0.0.1", "http://localhost:",
  "http://localhost:+1", "http://localhost:-1", "http://localhost:1.0", "http://localhost:1e2",
  "http://%31%32%37.0.0.1:1", "http://localhost:1/%2f", "http://localhost:%31",
  "http:\\127.0.0.1:1", "http://localhost:1\\", " http://localhost:1", "http://localhost:1 ",
  "http://local host:1", "http://localhost:1\n", "http://localhost:1\r\n",
  "http://localhost:1\t", "http://local\thost:1", "http://localhost:1\u0000",
  "http://localhost:1\u007f", "http://localhost:1\u00a0", "http://localhost:1\u2028",
  "x".repeat(257),
];

for (const [index, input] of rejected.entries()) {
  test(`reject raw origin ${index}: ${JSON.stringify(input)}`, () => assert.equal(normalizeOrigin(input), undefined));
}

test("sample current environment on each call, including transient removal and disabled precedence", (t) => {
  const original = { ...process.env };
  t.after(() => { process.env = original; });
  delete process.env.BILLION_CONTEXT_PLUGIN;
  delete process.env.BILLION_CONTEXT_PROXY;
  assert.deepEqual(billionOrigin(), unavailable());
  process.env.BILLION_CONTEXT_PROXY = "http://127.0.0.1:18788";
  assert.deepEqual(billionOrigin(), ready("http://127.0.0.1:18788"));
  process.env.BILLION_CONTEXT_PROXY = "http://localhost:3333/";
  assert.deepEqual(billionOrigin(), ready("http://localhost:3333"));
  delete process.env.BILLION_CONTEXT_PROXY;
  assert.deepEqual(billionOrigin(), unavailable());
  process.env.BILLION_CONTEXT_PROXY = "http://[::1]:2222";
  assert.deepEqual(billionOrigin(), ready("http://[::1]:2222"));
  process.env.BILLION_CONTEXT_PROXY = "";
  assert.deepEqual(billionOrigin(), unavailable());
  process.env.BILLION_CONTEXT_PROXY = "invalid";
  assert.deepEqual(billionOrigin(), unavailable());
  process.env.BILLION_CONTEXT_PLUGIN = "0";
  for (const value of [undefined, "", "invalid", "http://127.0.0.1:18788"]) {
    if (value === undefined) delete process.env.BILLION_CONTEXT_PROXY;
    else process.env.BILLION_CONTEXT_PROXY = value;
    assert.deepEqual(billionOrigin(), unavailable("disabled"));
  }
  process.env.BILLION_CONTEXT_PLUGIN = "false";
  process.env.BILI_NATIVE_OPENCODE = "0";
  assert.deepEqual(billionOrigin(), ready("http://127.0.0.1:18788"));
  assert.equal(process.env.BILLION_CONTEXT_PROXY, "http://127.0.0.1:18788");
});
