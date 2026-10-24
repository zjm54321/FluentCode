import assert from "node:assert/strict";
import { mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir, homedir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { getModelFavorites, setModelFavorites, modelStatePath, replaceFavorite } from "../src/model-favorites.ts";
import { definition, parseInput } from "../src/protocol.ts";
import { handlers } from "../src/index.ts";

const favorites = { favorites: [{ providerID: "provider", modelID: "vendor/model:latest" }] };
test("host state path honors XDG_STATE_HOME and home fallback", (t) => {
  const original = process.env.XDG_STATE_HOME;
  t.after(() => { if (original === undefined) delete process.env.XDG_STATE_HOME; else process.env.XDG_STATE_HOME = original; });
  process.env.XDG_STATE_HOME = "/synthetic/state";
  assert.equal(modelStatePath(), "/synthetic/state/opencode/model.json");
  delete process.env.XDG_STATE_HOME;
  assert.equal(modelStatePath(), join(homedir(), ".local/state/opencode/model.json"));
});

test("strict envelopes, IDs, cap, and sparse arrays", async () => {
  for (const input of [null, [], { extra: 1 }, { favorites: [] }])
    await assert.rejects(handlers["model.favorites.get"](input), TypeError);
  const invalid = [null, {}, [], { favorites: [], extra: true }, { favorites: Array(201).fill(favorites.favorites[0]) },
    { favorites: Array(1) }, ...["", "a b", "a\n", "a\u0000", "a".repeat(513)].map(modelID => ({ favorites: [{ providerID: "p", modelID }] })),
    { favorites: [{ providerID: "p".repeat(257), modelID: "m" }] }, { favorites: [{ ...favorites.favorites[0], extra: 1 }] }];
  for (const value of invalid) {
    assert.throws(() => parseInput(definition.methods["model.favorites.set"].input, value), TypeError);
    assert.throws(() => parseInput(definition.methods["model.favorites.get"].output, value), TypeError);
  }
  assert.deepEqual(parseInput(definition.methods["model.favorites.set"].input, { favorites: Array(200).fill(favorites.favorites[0]) }).favorites.length, 200);
});

test("missing state, fresh merge, raw preservation, atomic replacement, fixtures", async (t) => {
  const dir = await mkdtemp(join(tmpdir(), "aif-favorites-"));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const path = join(dir, "state/model.json");
  assert.deepEqual(await getModelFavorites(path), { favorites: [] });
  assert.deepEqual(await setModelFavorites(favorites, path), favorites);
  for (const name of ["favorites-get", "favorites-set"]) {
    const fixture = JSON.parse(await readFile(new URL(`./fixtures/${name}.json`, import.meta.url), "utf8"));
    const result = name === "favorites-get" ? await getModelFavorites(path) : await setModelFavorites(fixture.input, path);
    assert.deepEqual(result, fixture.output);
  }
  const original = '{\n "recent": [ {"providerID":"other","modelID":"r"} ], "favorite" : [], "variant": { "x" : "high" }, "future": {"big":9007199254740993123,"nested":["},\\\"",{},null]}\n}';
  await writeFile(path, original);
  await setModelFavorites(favorites, path);
  const actual = await readFile(path, "utf8");
  assert.equal(actual, original.replace('"favorite" : []', '"favorite" : ' + JSON.stringify(favorites.favorites)));
  assert.deepEqual(await getModelFavorites(path), favorites);
  assert.deepEqual(await readdir(join(dir, "state")), ["model.json"]);
  for (const malformed of ["{", "[]", "null", '"text"']) {
    await writeFile(path, malformed);
    await assert.rejects(setModelFavorites(favorites, path));
    await assert.rejects(getModelFavorites(path));
    assert.equal(await readFile(path, "utf8"), malformed);
  }
  assert.equal(JSON.parse(replaceFavorite('{"favorite":[],"favorite":[]}', favorites)).favorite[0].modelID, "vendor/model:latest");
  for (const text of ["{}", "{  }", '{"future":[1,2]}', '{"favo\\u0072ite": []}'])
    assert.deepEqual(JSON.parse(replaceFavorite(text, favorites)).favorite, favorites.favorites);
});
