/* Local view protocol only. The native host owns transport, credentials and replay cursors. */
(() => {
  "use strict";
  const bridge = window.chrome?.webview;
  if (!bridge) return;
  const screen = document.getElementById("screen");
  let terminal, fit, binding, writable = false, disposed = false, lastResize = "";
  let inputGeneration = ""; // Local UI consent fence, never a server token or ticket.
  let chain = Promise.resolve();
  const post = (type, extra = {}) => {
    if (binding && !disposed) bridge.postMessage({ type, ...binding, ...extra });
  };
  const validBinding = value => value && typeof value.nonce === "string" && value.nonce.length === 32
    && typeof value.viewId === "string" && typeof value.generation === "string";
  const matches = value => binding && value.nonce === binding.nonce && value.viewId === binding.viewId
    && value.generation === binding.generation;
  const geometry = value => Number.isInteger(value.cols) && value.cols >= 2 && value.cols <= 1000
    && Number.isInteger(value.rows) && value.rows >= 1 && value.rows <= 1000;
  const encode = bytes => {
    let text = "";
    for (let i = 0; i < bytes.length; i += 8192) text += String.fromCharCode(...bytes.subarray(i, i + 8192));
    return btoa(text);
  };
  const input = bytes => { if (writable && bytes.length <= 65536) post("input", { data: encode(bytes), inputGeneration }); };
  const requestResize = () => {
    if (!terminal || !writable || disposed) return;
    const size = fit.proposeDimensions();
    if (!size || !geometry(size)) return;
    const key = `${size.cols}:${size.rows}`;
    if (key !== lastResize) { lastResize = key; post("resize", { ...size, inputGeneration }); }
    // Only the host's authoritative resize response changes terminal geometry.
  };
  const write = data => new Promise(resolve => terminal.write(data, resolve));
  const receive = async message => {
    if (!message || typeof message !== "object") return;
    if (message.type === "begin") {
      if (!validBinding(message) || !geometry(message)) return;
      writable = false; inputGeneration = "";
      terminal?.dispose(); screen.replaceChildren(); disposed = false; lastResize = "";
      binding = { nonce: message.nonce, viewId: message.viewId, generation: message.generation };
      terminal = new Terminal({ cols: message.cols, rows: message.rows, scrollback: 5000,
        fontFamily: '"Cascadia Mono", Consolas, monospace', fontSize: 13, lineHeight: 1.15,
        cursorBlink: false, cursorStyle: "block", disableStdin: true, screenReaderMode: true,
        allowProposedApi: false, theme: { background: "#0c0c0c", foreground: "#cccccc", cursor: "#cccccc",
          selectionBackground: "#315175", black: "#0c0c0c", red: "#c50f1f", green: "#13a10e",
          yellow: "#c19c00", blue: "#0037da", magenta: "#881798", cyan: "#3a96dd", white: "#cccccc" },
        linkHandler: { activate() {} } });
      fit = new FitAddon.FitAddon(); terminal.loadAddon(fit); terminal.open(screen);
      terminal.parser.registerOscHandler(52, () => true); // Never read/write the clipboard via terminal output.
      terminal.onData(text => input(new TextEncoder().encode(text)));
      terminal.onBinary(text => input(Uint8Array.from(text, c => c.charCodeAt(0))));
      terminal.onSelectionChange(() => post("selection", { text: terminal.getSelection().slice(0, 262144) }));
      post("rendered", { sequence: message.sequence }); return;
    }
    if (!matches(message) || disposed) return;
    try {
      switch (message.type) {
        case "reset":
          if (!geometry(message) || typeof message.checkpoint !== "string" || message.checkpoint.length > 11184812) throw Error();
          writable = false; inputGeneration = ""; terminal.options.disableStdin = true; terminal.reset();
          terminal.resize(message.cols, message.rows);
          await write(Uint8Array.from(atob(message.checkpoint), c => c.charCodeAt(0))); break;
        case "output":
          if (typeof message.data !== "string" || message.data.length > 2097152) throw Error();
          await write(Uint8Array.from(atob(message.data), c => c.charCodeAt(0))); break;
        case "mode":
          writable = message.writable === true; inputGeneration = typeof message.inputGeneration === "string" ? message.inputGeneration : "";
          terminal.options.disableStdin = !writable;
          terminal.options.cursorBlink = writable; lastResize = "";
          if (writable) terminal.focus(); break;
        case "measure": if (writable) { lastResize = ""; requestResize(); } break;
        case "focus": terminal.focus(); break;
        case "dispose": writable = false; terminal.dispose(); disposed = true; binding = undefined; return;
        default: throw Error();
      }
      post("rendered", { sequence: message.sequence });
    } catch { post("failed", { sequence: message.sequence }); }
  };
  bridge.addEventListener("message", event => {
    // Serialize parsing/reset/output: acknowledgements prove VT consumption, not just JS receipt.
    chain = chain.then(() => receive(event.data)).catch(() => {});
  });
  // Layout/focus/role changes are never implicit PTY control or resize gestures.
  // Only the native view's explicit “适应窗口” action sends a measure request.
})();
