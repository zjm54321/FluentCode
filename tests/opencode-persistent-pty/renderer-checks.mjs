// Real Edge/Chromium + the vendored production renderer. Uses the already prepared
// workspace browser runtime's puppeteer-core; installs/downloads nothing.
import { createRequire } from 'node:module';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
const repo = process.argv[2];
const evidence = process.argv[3];
if (!repo || !evidence) throw Error('Usage: node renderer-checks.mjs <repo> <evidence>');
const require = createRequire(path.join(repo, 'artifacts/browser-runtime/sidecar.mjs'));
const puppeteer = require('puppeteer-core');
const assets = path.join(repo, 'src/AIFrontend/Assets/OpenCode/PersistentPty');
await mkdir(evidence, { recursive: true });
const checks = [], remote = [], errors = [];
const check = (condition, label) => { if (!condition) throw Error(label); checks.push(label); console.log('PASS ' + label); };
const browser = await puppeteer.launch({ executablePath: process.env.PTY_TEST_BROWSER || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  headless: true, userDataDir: path.join(evidence, 'edge-profile'), args: ['--no-first-run', '--disable-background-networking', '--disable-component-update'] });
try {
  const page = await browser.newPage();
  await page.setViewport({ width: 1000, height: 600, deviceScaleFactor: 1 });
  await page.setRequestInterception(true);
  page.on('request', async request => {
    const url = new URL(request.url());
    const file = url.pathname.slice(1);
    if (url.origin !== 'https://persistent-pty.aif.invalid' || !['index.html','terminal.js','terminal.css','xterm.js','xterm.css','addon-fit.js'].includes(file)) {
      remote.push(request.url()); await request.abort(); return;
    }
    await request.respond({ status: 200, contentType: file.endsWith('.html') ? 'text/html' : file.endsWith('.css') ? 'text/css' : 'text/javascript', body: await readFile(path.join(assets, file)) });
  });
  page.on('pageerror', error => errors.push(error.message));
  await page.evaluateOnNewDocument(() => {
    window.__events = [];
    window.chrome ||= {};
    window.chrome.webview = {
      postMessage: message => window.__events.push(message),
      addEventListener: (_, callback) => { window.__receive = callback; },
    };
  });
  await page.goto('https://persistent-pty.aif.invalid/index.html');
  await page.waitForFunction(() => typeof window.__receive === 'function' && typeof window.Terminal === 'function');
  await page.evaluate(() => {
    const Original = window.Terminal;
    window.Terminal = class extends Original { constructor(options) { super(options); window.__term = this; } };
  });
  const binding = { nonce: 'a'.repeat(32), viewId: 'view-one', generation: '1' };
  let sequence = 0;
  const send = async (type, payload = {}, overrides = {}) => {
    const id = String(++sequence);
    if (type === 'mode') payload = { inputGeneration: id, ...payload };
    await page.evaluate(message => window.__receive({ data: message }), { ...binding, type, sequence: id, ...payload, ...overrides });
    await page.waitForFunction(seq => window.__events.some(e => e.sequence === seq && ['rendered','failed'].includes(e.type)), {}, id);
    const result = await page.evaluate(seq => window.__events.find(e => e.sequence === seq), id);
    check(result.type === 'rendered', type + ' acknowledgement follows VT parsing #' + id);
  };
  const output = bytes => send('output', { data: Buffer.from(bytes).toString('base64') });
  const line = y => page.evaluate(index => window.__term.buffer.active.getLine(index)?.translateToString(true), y);
  const eventCount = type => page.evaluate(t => window.__events.filter(e => e.type === t).length, type);
  const allInput = () => page.evaluate(() => window.__events.filter(e => e.type === 'input').map(e => e.data))
    .then(items => Buffer.concat(items.map(item => Buffer.from(item, 'base64'))).toString());
  await send('begin', { cols: 80, rows: 24 });
  await output('\x1b[2J\x1b[H\x1b[31mRED\x1b[0m\r\nplain');
  check(await line(0) === 'RED' && await line(1) === 'plain', 'ANSI escapes render screen cells, not a text log');
  check(await page.evaluate(() => window.__term.buffer.active.getLine(0).getCell(0).getFgColor()) === 1, 'SGR color survives actual xterm parsing');
  await output('\r\x1b[2Kreplaced');
  check(await line(1) === 'replaced', 'carriage return and erase-line update the existing row');
  await output('\x1b[?1049h\x1b[Hfull-screen app');
  check(await page.evaluate(() => window.__term.buffer.active.type) === 'alternate' && await line(0) === 'full-screen app', 'alternate-screen interactive programs use a real VT screen');
  await output('\x1b[?1049l');
  check(await line(0) === 'RED' && await line(1) === 'replaced', 'leaving alternate screen restores normal buffer');
  const unicode = Buffer.from('中文🙂');
  await output('\r\n'); await output(unicode.subarray(0, 2)); await output(unicode.subarray(2));
  check(await line(2) === '中文🙂', 'UTF-8 multibyte characters survive fragmented binary output');
  await output('\x1b[6n');
  check(await eventCount('input') === 0, 'bootstrap/replay suppresses emulator-generated device responses');
  await page.evaluate(() => window.__term.focus()); await page.keyboard.type('blocked');
  check(await eventCount('input') === 0 && await eventCount('resize') === 0, 'observer cannot send keyboard input or silently resize the PTY');
  await send('mode', { writable: true });
  const firstConsentGeneration = String(sequence);
  check(await eventCount('resize') === 0, 'arming controller input does not implicitly fit or resize the PTY');
  check(await page.evaluate(() => window.__term.cols) === 80, 'fit request waits for host resize acknowledgement');
  await page.keyboard.type('echo ok'); await page.keyboard.press('Enter');
  check((await allInput()).includes('echo ok\r'), 'real browser keyboard produces PTY input bytes');
  const firstConsent = await page.evaluate(() => window.__events.filter(e => e.type === 'input').at(-1).inputGeneration);
  check(firstConsent === firstConsentGeneration, 'input carries the exact local consent generation supplied by the native mode command');
  await page.keyboard.press('Escape');
  check((await allInput()).endsWith('\x1b'), 'Escape is PTY input, never a pane-close shortcut');
  const cdp = await page.createCDPSession();
  await cdp.send('Input.imeSetComposition', { text: '中文', selectionStart: 2, selectionEnd: 2 });
  await cdp.send('Input.insertText', { text: '中文' });
  await page.waitForFunction(() => window.__events.filter(e => e.type === 'input').map(e => atob(e.data)).join('').includes('\xe4\xb8\xad'));
  check((await allInput()).includes('中文'), 'browser IME composition commits UTF-8 terminal input');
  const resizeBefore = await eventCount('resize');
  await page.setViewport({ width: 700, height: 440, deviceScaleFactor: 1 });
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  check(await eventCount('resize') === resizeBefore, 'controller viewport layout changes do not implicitly resize or take control');
  await send('measure');
  await page.waitForFunction(n => window.__events.filter(e => e.type === 'resize').length > n, {}, resizeBefore);
  const requested = await page.evaluate(() => window.__events.filter(e => e.type === 'resize').at(-1));
  await send('reset', { cols: requested.cols, rows: requested.rows, checkpoint: Buffer.from('\x1b[2J\x1b[Hresized checkpoint').toString('base64') });
  check(await page.evaluate(cols => window.__term.cols === cols && window.__term.buffer.active.getLine(0).translateToString(true) === 'resized checkpoint', requested.cols),
    'host-confirmed resize consumes authoritative checkpoint with canonical geometry, not geometry-only reflow');
  await send('mode', { writable: true });
  await send('mode', { writable: false });
  const inputBefore = await eventCount('input'), readOnlyResize = await eventCount('resize');
  await page.keyboard.type('blocked again'); await page.setViewport({ width: 850, height: 500, deviceScaleFactor: 1 });
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  check(await eventCount('input') === inputBefore && await eventCount('resize') === readOnlyResize, 'role downgrade disables both input and resize');
  await send('mode', { writable: true });
  await page.keyboard.press('ArrowUp');
  const nextConsent = await page.evaluate(() => window.__events.filter(e => e.type === 'input').at(-1).inputGeneration);
  check(nextConsent !== firstConsent && nextConsent === String(sequence), 'explicit re-arming rotates input consent generation; queued old messages remain distinguishable');
  await send('mode', { writable: false });
  const disarmedInputCount = await eventCount('input');
  await send('reset', { cols: 80, rows: 24, checkpoint: Buffer.from('\x1b[2J\x1b[Hcheckpoint\x1b[3;5H\x1b[6n').toString('base64') });
  check(await eventCount('input') === disarmedInputCount, 'base64 checkpoint restoration remains disarmed even when it requests a terminal response');
  check(await line(0) === 'checkpoint' && await page.evaluate(() => window.__term.buffer.active.cursorY === 2 && window.__term.buffer.active.cursorX === 4), 'VT checkpoint restores exact cursor state without treating plain text as a checkpoint');
  await page.evaluate(() => window.__term.select(0, 0, 10));
  check(await page.evaluate(() => window.__events.some(e => e.type === 'selection' && e.text === 'checkpoint')), 'read-only terminal selection uses the real screen buffer');
  const oldLine = await line(0);
  await page.evaluate(message => window.__receive({ data: message }), { ...binding, nonce: 'b'.repeat(32), type: 'output', sequence: 'stale', data: Buffer.from('FOREIGN').toString('base64') });
  await page.evaluate(() => new Promise(resolve => setTimeout(resolve, 20)));
  check(await line(0) === oldLine && !await page.evaluate(() => window.__events.some(e => e.sequence === 'stale')), 'stale view nonce cannot change the renderer or receive acknowledgement');
  await output('\x1b]52;c;U0VDUkVU\x07\r\n<img src="https://evil.invalid/a">');
  check(await page.evaluate(() => document.querySelectorAll('img,iframe').length) === 0 && remote.length === 0, 'terminal output cannot create HTML, remote resources or frames; OSC52 is consumed');
  await output('\r\n\x1b[32m$ \x1b[0mnpm run dev\r\n\x1b[36mready\x1b[0m  http://localhost:3000\r\n');
  await page.screenshot({ path: path.join(evidence, 'terminal-renderer.png') });
  check(errors.length === 0, 'renderer has no uncaught browser errors');
  await writeFile(path.join(evidence, 'browser-results.json'), JSON.stringify({ browser: await browser.version(), checks, remoteRequests: remote, errors,
    boundary: 'Real Chromium VT/keyboard/IME/layout; native host/WebSocket/replay-cursor ownership not exercised.' }, null, 2));
  console.log(`PASS ${checks.length} real-browser checks; evidence ${evidence}`);
} finally { await browser.close(); }
