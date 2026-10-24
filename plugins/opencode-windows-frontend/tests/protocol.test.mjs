import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { isDeepStrictEqual } from "node:util";
import plugin, { handlers } from "../src/index.ts";
import { definition, hello } from "../src/protocol.ts";

function validate(schema, value) {
  return schema["~standard"].validate(value);
}

function fixture(name) {
  return JSON.parse(readFileSync(new URL(`./fixtures/${name}.json`, import.meta.url), "utf8"));
}

test("hello negotiates v1 only, without requiring a common version", async () => {
  for (const name of ["hello-v1", "hello-unsupported"]) {
    const wire = fixture(name);
    assert.deepEqual(await handlers.hello(wire.input), wire.output);
    assert.deepEqual(validate(definition.methods.hello.output, wire.output), { value: wire.output });
  }
  assert.deepEqual(hello({ protocolVersions: [2, 1, 3] }), fixture("hello-v1").output);
  assert.deepEqual(hello({ protocolVersions: [] }), fixture("hello-unsupported").output);
  assert.deepEqual(hello({ protocolVersions: Array(8).fill(1) }), fixture("hello-v1").output);
});

const invalidHello = [
  undefined, null, [], 1, "hello", {}, { protocolVersions: null }, { protocolVersions: 1 },
  { protocolVersions: "1" }, { protocolVersions: ["1"] }, { protocolVersions: [null] },
  { protocolVersions: [0] }, { protocolVersions: [-1] }, { protocolVersions: [1.5] },
  { protocolVersions: [NaN] }, { protocolVersions: [Infinity] }, { protocolVersions: [true] },
  { protocolVersions: Array(9).fill(1) }, { protocolVersions: [1], extra: true },
  { protocolVersions: Array(1) },
];

for (const [index, input] of invalidHello.entries()) {
  test(`reject malformed hello ${index}`, async () => {
    assert.ok("issues" in validate(definition.methods.hello.input, input));
    assert.throws(() => hello(input), TypeError);
    await assert.rejects(handlers.hello(input), TypeError);
  });
}

test("billion.origin accepts exactly an empty object", async () => {
  assert.deepEqual(validate(definition.methods["billion.origin"].input, {}), { value: {} });
  for (const input of [undefined, null, [], "", 0, { extra: 1 }, { protocolVersion: 1 }]) {
    assert.ok("issues" in validate(definition.methods["billion.origin"].input, input));
    await assert.rejects(handlers["billion.origin"](input), TypeError);
  }
});

test("origin fixtures match exact handler outputs; calls perform no fetch or env writes", async (t) => {
  const original = { ...process.env };
  t.after(() => { process.env = original; });
  const fetch = t.mock.method(globalThis, "fetch", () => { throw new Error("Network forbidden"); });
  for (const [name, origin, disabled] of [
    ["origin-ready", "http://127.0.0.1:18788", false],
    ["origin-not-ready", "invalid", false],
    ["origin-disabled", "http://127.0.0.1:18788", true],
  ]) {
    process.env.BILLION_CONTEXT_PROXY = origin;
    if (disabled) process.env.BILLION_CONTEXT_PLUGIN = "0";
    else delete process.env.BILLION_CONTEXT_PLUGIN;
    const before = { ...process.env };
    const wire = fixture(name);
    assert.deepEqual(await handlers["billion.origin"](wire.input), wire.output);
    assert.deepEqual(validate(definition.methods["billion.origin"].output, wire.output), { value: wire.output });
    assert.ok(isDeepStrictEqual({ ...process.env }, before), "Plugin changed process environment");
  }
  await handlers.hello({ protocolVersions: [1] });
  assert.equal(fetch.mock.callCount(), 0);
});

test("output schemas reject additional fields and malformed variants", () => {
  for (const [method, valid] of [
    ["hello", fixture("hello-v1").output],
    ["hello", fixture("hello-unsupported").output],
    ["billion.origin", fixture("origin-ready").output],
    ["billion.origin", fixture("origin-not-ready").output],
    ["billion.origin", fixture("origin-disabled").output],
  ]) {
    const schema = definition.methods[method].output;
    assert.ok("issues" in validate(schema, { ...valid, extra: true }));
    for (const key of Object.keys(valid)) {
      const missing = { ...valid };
      delete missing[key];
      assert.ok("issues" in validate(schema, missing));
    }
    assert.ok("issues" in validate(schema, { ...valid, protocolVersion: 2 }));
  }
  for (const invalid of [
    { protocolVersion: 1, status: "ready", origin: "http://localhost:1/" },
    { protocolVersion: 1, status: "ready", origin: "https://localhost:1" },
    { protocolVersion: 1, status: "unavailable", reason: "other" },
  ]) assert.ok("issues" in validate(definition.methods["billion.origin"].output, invalid));
  assert.ok("issues" in validate(definition.methods.hello.output, { protocolVersion: 1, capabilities: [] }));
});

test("setup only registers the RPC id, four methods, and no events; teardown disposes once", async (t) => {
  const fetch = t.mock.method(globalThis, "fetch", () => { throw new Error("Network forbidden"); });
  let count = 0;
  let disposals = 0;
  const cleanup = await plugin.setup({
    rpc: { register: async (actualDefinition, actualHandlers) => {
      count++;
      assert.equal(actualDefinition, definition);
      assert.equal(actualHandlers, handlers);
      assert.equal(actualDefinition.id, "opencode.windows-frontend");
      assert.deepEqual(Object.keys(actualDefinition.methods), ["hello", "billion.origin", "model.favorites.get", "model.favorites.set"]);
      assert.deepEqual(Object.keys(actualDefinition), ["id", "methods", "events"]);
      assert.deepEqual(actualDefinition.events, {});
      return { dispose: async () => { disposals++; } };
    } },
  });
  assert.equal(count, 1);
  assert.equal(disposals, 0);
  await Promise.all([cleanup(), cleanup()]);
  assert.equal(disposals, 1);
  assert.equal(fetch.mock.callCount(), 0);
});

test("teardown requested during pending setup disposes a late registration", async () => {
  let finish;
  let disposals = 0;
  const setup = plugin.setup({ rpc: { register: () => new Promise((resolve) => { finish = resolve; }) } });
  // Matches host acquireRelease: teardown waits for acquisition to finish.
  const teardown = setup.then((cleanup) => cleanup());
  finish({ dispose: async () => { disposals++; } });
  await teardown;
  assert.equal(disposals, 1);
});

test("registration and disposal failures propagate", async () => {
  const failure = new Error("registration failed");
  await assert.rejects(plugin.setup({ rpc: { register: async () => { throw failure; } } }), failure);
  const cleanup = await plugin.setup({ rpc: { register: async () => ({ dispose: async () => { throw failure; } }) } });
  await assert.rejects(cleanup(), failure);
});
