import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {readFile, readdir, stat, writeFile} from 'node:fs/promises';
import {createRequire} from 'node:module';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const json = async name => JSON.parse(await readFile(path.join(root, name), 'utf8'));
const hash = async name => createHash('sha256').update(await readFile(path.join(root, name))).digest('hex');
const versions = await json('runtime-versions.json');
const manifest = await json('package.json');
const lock = await json('package-lock.json');
assert.equal(process.platform, 'win32');
assert.equal(process.arch, 'x64');
assert.equal(process.version, `v${versions.nodeVersion}`);
assert.equal(path.resolve(process.execPath).toLowerCase(), path.join(root, 'node.exe').toLowerCase());
assert.equal(await hash('node.exe'), versions.nodeExeSha256);
assert.equal(manifest.dependencies.lighthouse, versions.lighthouseVersion);
assert.equal(lock.lockfileVersion, 3);
assert.deepEqual(lock.packages[''].dependencies, manifest.dependencies);
assert.equal(lock.packages['node_modules/lighthouse'].integrity, versions.lighthouseIntegrity);

let packageCount = 0;
for (const [location, entry] of Object.entries(lock.packages)) {
  if (!location) continue;
  assert.ok(!entry.dev, `Development package in production lock: ${location}`);
  assert.match(entry.resolved, /^https:\/\/registry\.npmjs\.org\//, `Unexpected registry: ${location}`);
  assert.match(entry.integrity, /^sha(256|384|512)-/, `Missing package integrity: ${location}`);
  assert.equal((await json(`${location}/package.json`)).version, entry.version, `Version mismatch: ${location}`);
  packageCount++;
}

// Importing Lighthouse must not launch/download a browser or use a network connection.
const lighthouse = await import('lighthouse');
assert.equal(typeof lighthouse.default, 'function');
assert.equal(typeof lighthouse.snapshot, 'function');
assert.equal(typeof lighthouse.startFlow, 'function');
const lighthousePackage = await json('node_modules/lighthouse/package.json');
assert.equal(lighthousePackage.version, versions.lighthouseVersion);

// Preserve the complete npm payload: reports, gatherer scripts, localization and licenses.
const requiredFiles = [
  'node_modules/lighthouse/LICENSE',
  'node_modules/lighthouse/core/config/default-config.js',
  'node_modules/lighthouse/core/gather/gatherers/accessibility.js',
  'node_modules/lighthouse/report/generator/report-generator.js',
  'node_modules/lighthouse/shared/localization/locales/en-US.json',
  'licenses/Node-LICENSE.txt',
  'licenses/Node-README.md',
  'THIRD-PARTY-NOTICES.md',
  'third-party-packages.json',
];
for (const name of requiredFiles) assert.ok((await stat(path.join(root, name))).size > 0, `Missing asset: ${name}`);
assert.ok((await stat(require.resolve('axe-core/axe.min.js'))).size > 0);
assert.ok((await readdir(path.join(root, 'node_modules/lighthouse/report/assets'))).length > 0);

const evidence = {
  schemaVersion: 1,
  platform: 'win-x64',
  nodeVersion: versions.nodeVersion,
  nodeArchive: versions.nodeArchive,
  nodeArchiveSha256: versions.nodeArchiveSha256,
  nodeExeSha256: await hash('node.exe'),
  lighthouseVersion: lighthousePackage.version,
  lighthouseIntegrity: versions.lighthouseIntegrity,
  packageLockSha256: await hash('package-lock.json'),
  productionPackageCount: packageCount,
};
if (process.argv[2] === '--write-manifest') {
  assert.match(process.argv[3] ?? '', /^\d+\.\d+\.\d+$/);
  await writeFile(path.join(root, 'runtime-manifest.json'), `${JSON.stringify({...evidence, npmVersion: process.argv[3]}, null, 2)}\n`);
} else {
  const saved = await json('runtime-manifest.json');
  for (const [name, value] of Object.entries(evidence)) assert.equal(saved[name], value, `Manifest mismatch: ${name}`);
}
console.log(JSON.stringify(evidence, null, 2));
