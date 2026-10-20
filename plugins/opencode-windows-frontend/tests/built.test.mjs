import assert from "node:assert/strict";
import { test } from "node:test";
import { isDeepStrictEqual } from "node:util";

test("built dist/index.js imports as ESM with a default setup plugin and working handlers", async (t) => {
  const before = { ...process.env };
  const fetch = t.mock.method(globalThis, "fetch", () => { throw new Error("Network forbidden"); });
  const { default: plugin } = await import("../dist/index.js");
  assert.deepEqual(Object.keys(plugin), ["id", "setup"]);
  assert.equal(plugin.id, "opencode.windows-frontend");
  assert.equal(typeof plugin.setup, "function");
  let disposed = false;
  const cleanup = await plugin.setup({ rpc: { register: async (definition, handlers) => {
    assert.equal(definition.id, plugin.id);
    assert.deepEqual(definition.events, {});
    assert.deepEqual(await handlers.hello({ protocolVersions: [1] }), {
      protocolVersion: 1, capabilities: ["billion.origin.v1"],
    });
    await assert.rejects(handlers["billion.origin"]({ extra: true }), TypeError);
    return { dispose: async () => { disposed = true; } };
  } } });
  await cleanup();
  assert.equal(disposed, true);
  assert.ok(isDeepStrictEqual({ ...process.env }, before), "Plugin changed process environment");
  assert.equal(fetch.mock.callCount(), 0);
});
