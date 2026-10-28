import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {mkdir, mkdtemp, readFile, rm, writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import test from 'node:test';

const helper = fileURLToPath(new URL('./copy-runtime.mjs', import.meta.url));
function copy(source, destination) {
  return spawnSync(process.execPath, [helper, source, destination], {encoding: 'utf8'});
}
async function fixture(t) {
  const root = await mkdtemp(path.join(tmpdir(), 'aif-runtime-copy-'));
  t.after(() => rm(root, {recursive: true, force: true}));
  const source = path.join(root, 'prepared');
  const output = path.join(root, 'output');
  await mkdir(path.join(source, 'node_modules/lighthouse'), {recursive: true});
  await mkdir(output);
  for (const relative of ['node.exe', 'sidecar.mjs', 'runtime-manifest.json', 'node_modules/lighthouse/package.json']) {
    await writeFile(path.join(source, relative), relative);
  }
  return {source, output, destination: path.join(output, 'BrowserRuntime')};
}

test('copies long paths and source maps unchanged, replacing stale generated files only', async t => {
  const {source, output, destination} = await fixture(t);
  const relative = path.join('node_modules', 'deep-package-'.repeat(6), 'vendored', 'nested-package-'.repeat(6), 'entry.js.map');
  await mkdir(path.dirname(path.join(source, relative)), {recursive: true});
  await writeFile(path.join(source, relative), 'complete source map');
  await mkdir(destination);
  await writeFile(path.join(destination, 'stale-dependency.js'), 'stale');
  await writeFile(path.join(output, 'application.dll'), 'unrelated output');
  assert.ok(path.join(destination, relative).length > 260);
  const result = copy(source, destination);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(await readFile(path.join(destination, relative), 'utf8'), 'complete source map');
  await assert.rejects(readFile(path.join(destination, 'stale-dependency.js')), {code: 'ENOENT'});
  assert.equal(await readFile(path.join(output, 'application.dll'), 'utf8'), 'unrelated output');
  assert.equal(await readFile(path.join(source, relative), 'utf8'), 'complete source map');
});

test('refuses overlapping source and destination before deletion', async t => {
  const {source} = await fixture(t);
  const result = copy(source, path.join(source, 'BrowserRuntime'));
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /must not overlap/);
  assert.equal(await readFile(path.join(source, 'node.exe'), 'utf8'), 'node.exe');
});

test('refuses incomplete input before replacing an existing destination', async t => {
  const {source, destination} = await fixture(t);
  await rm(path.join(source, 'sidecar.mjs'));
  await mkdir(destination);
  await writeFile(path.join(destination, 'sentinel'), 'preserved');
  const result = copy(source, destination);
  assert.notEqual(result.status, 0);
  assert.equal(await readFile(path.join(destination, 'sentinel'), 'utf8'), 'preserved');
});

test('refuses to replace an output directory other than BrowserRuntime', async t => {
  const {source, output} = await fixture(t);
  const result = copy(source, output);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Only the generated BrowserRuntime destination/);
});
