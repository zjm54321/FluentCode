// Owns only the process tree and files created here. No user service, credentials or database reads.
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

const [binary, ptyBinary, windowsDotnet, windowsAssembly, evidenceDirectory] = process.argv.slice(2);
if (!binary || !ptyBinary || !windowsDotnet || !windowsAssembly || !evidenceDirectory)
  throw new Error('Expected server binary, PTY binary, Windows dotnet, assembly and evidence directory.');
const root = await mkdtemp('/tmp/opencode/aif-phase2-pty-real-');
for (const dir of ['home', 'config', 'cache', 'data', 'state', 'runtime', 'work']) await mkdir(join(root, dir));
const env = {
  PATH: process.env.PATH, LANG: 'C.UTF-8', SHELL: '/bin/sh', HOME: join(root, 'home'),
  XDG_CONFIG_HOME: join(root, 'config'), XDG_CACHE_HOME: join(root, 'cache'),
  XDG_DATA_HOME: join(root, 'data'), XDG_STATE_HOME: join(root, 'state'), XDG_RUNTIME_DIR: join(root, 'runtime'),
  OPENCODE_CONFIG_DIR: join(root, 'config'), OPENCODE_CONFIG_CONTENT: '{}', OPENCODE_DB: join(root, 'owned.db'),
  OPENCODE_DISABLE_FILEWATCHER: 'true', OPENCODE_DISABLE_MODELS_FETCH: 'true', OPENCODE_TEST_HOME: join(root, 'home'),
  OPENCODE_PTY_BIN: ptyBinary, OPENCODE_SERVER_PASSWORD: 'synthetic', OPENCODE_CONFIG_PROJECT_DISABLE: 'true',
};
const child = spawn(binary, ['serve', '--hostname', '127.0.0.1', '--port', '0'], {
  cwd: join(root, 'work'), env, detached: true, stdio: ['ignore', 'pipe', 'pipe'],
});
let logs = '';
child.stdout.on('data', data => { logs += data.toString(); });
child.stderr.on('data', data => { logs += data.toString(); });
const exit = new Promise(resolve => child.once('exit', (code, signal) => resolve({ code, signal })));
try {
  let base;
  for (let n = 0; n < 120; n++) {
    base = logs.match(/http:\/\/127\.0\.0\.1:\d+/)?.[0];
    if (base) break;
    if (child.exitCode !== null) throw new Error('Isolated server exited before startup.');
    await delay(100);
  }
  if (!base) throw new Error('Isolated server startup timed out.');
  const result = await new Promise((resolve, reject) => {
    const test = spawn(windowsDotnet, [windowsAssembly, '--real', base, join(root, 'work')], { stdio: ['ignore', 'pipe', 'pipe'] });
    let output = ''; test.stdout.on('data', chunk => { output += chunk; process.stdout.write(chunk); });
    test.stderr.on('data', chunk => { output += chunk; process.stderr.write(chunk); });
    const deadline = setTimeout(() => { test.kill('SIGKILL'); reject(new Error('Owned PTY test timed out.')); }, 60000);
    test.once('exit', async code => { clearTimeout(deadline); await writeFile(join(evidenceDirectory, 'real-results.txt'), output); resolve(code); });
    test.once('error', reject);
  });
  if (result !== 0) throw new Error('Owned PTY backend test failed.');
  await writeFile(join(evidenceDirectory, 'real-fixture.json'), JSON.stringify({ isolatedRoot: root, serverVersion: '2.1.4-qol', passed: true, realNativePty: true, userServiceUsed: false }, null, 2));
} finally {
  // The isolated server may spawn a durable daemon; terminate only this newly created process group.
  try { process.kill(-child.pid, 'SIGTERM'); } catch (e) { if (e.code !== 'ESRCH') throw e; }
  await Promise.race([exit, delay(3000)]);
  try { process.kill(-child.pid, 'SIGKILL'); } catch (e) { if (e.code !== 'ESRCH') throw e; }
  // Never persist raw server logs: a peer could print a ticket or ticketed URL.
  await writeFile(join(evidenceDirectory, 'real-server-summary.txt'), 'Isolated process group terminated; raw server output intentionally not retained.\n');
}
