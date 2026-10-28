import {cp, lstat, realpath, rm, stat} from 'node:fs/promises';
import path from 'node:path';

const [sourceArgument, destinationArgument, ...extraArguments] = process.argv.slice(2);
if (!sourceArgument || !destinationArgument || extraArguments.length) {
  throw new Error('Usage: node copy-runtime.mjs <prepared-runtime> <output-directory/BrowserRuntime>');
}

const source = await realpath(sourceArgument);
const requestedDestination = path.resolve(destinationArgument);
if (path.basename(requestedDestination) !== 'BrowserRuntime') {
  throw new Error('Only the generated BrowserRuntime destination may be replaced.');
}
// Build/Publish creates the parent. Resolve it before checking the destructive target,
// so a junction in the output path cannot turn the destination into the source tree.
const destination = path.join(await realpath(path.dirname(requestedDestination)), 'BrowserRuntime');
function contains(parent, child) {
  const relative = path.relative(parent, child);
  return relative === '' || (relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative));
}
if (contains(source, destination) || contains(destination, source)) {
  throw new Error('Prepared runtime and generated BrowserRuntime must not overlap.');
}
for (const relative of ['node.exe', 'sidecar.mjs', 'runtime-manifest.json', 'node_modules/lighthouse/package.json']) {
  if (!(await stat(path.join(source, relative))).isFile()) {
    throw new Error(`Prepared runtime file is missing: ${relative}`);
  }
}
const existing = await lstat(destination).catch(error => {
  if (error.code === 'ENOENT') return null;
  throw error;
});
if (existing && (!existing.isDirectory() || existing.isSymbolicLink())) {
  throw new Error('Generated BrowserRuntime destination must be a directory, not a link or file.');
}

console.log(`Prepared runtime source: ${source}`);
console.log(`Replacing generated BrowserRuntime only: ${destination}`);
// Replace the whole generated tree, not just changed files: removed dependencies
// must not survive from a previous build. No files, licenses or source maps are filtered.
await rm(destination, {recursive: true, force: true});
await cp(source, destination, {recursive: true, preserveTimestamps: true, verbatimSymlinks: true});
console.log(`Copied complete browser runtime: ${destination}`);
