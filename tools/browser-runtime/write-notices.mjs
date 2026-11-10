import assert from 'node:assert/strict';
import {readFile, readdir, writeFile} from 'node:fs/promises';
import path from 'node:path';

const root = path.resolve(process.argv[2]);
const lock = JSON.parse(await readFile(path.join(root, 'package-lock.json'), 'utf8'));
const packages = [];
for (const [location, entry] of Object.entries(lock.packages)) {
  if (!location) continue;
  // npm records every native variant, but installs only the optional win-x64 payload.
  if (entry.optional && ((entry.os && !entry.os.includes(process.platform))
      || (entry.cpu && !entry.cpu.includes(process.arch)))) continue;
  const directory = path.join(root, location);
  const metadata = JSON.parse(await readFile(path.join(directory, 'package.json'), 'utf8'));
  const files = (await readdir(directory)).filter(name => /^(licen[cs]e|copying|notice|copyright)([.-]|$)/i.test(name));
  // Original package payloads, including nested license files, are never filtered.
  packages.push({
    name: metadata.name,
    version: metadata.version,
    license: metadata.license ?? entry.license ?? metadata.licenses ?? null,
    repository: metadata.repository ?? null,
    location,
    integrity: entry.integrity,
    licenseFiles: files.map(name => `${location}/${name}`),
  });
}
assert.ok(packages.length > 0);
packages.sort((a, b) => a.location.localeCompare(b.location));
await writeFile(path.join(root, 'third-party-packages.json'), `${JSON.stringify(packages, null, 2)}\n`);
