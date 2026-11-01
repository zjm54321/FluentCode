# Offline Mermaid renderer

- Mermaid **11.12.3**, MIT, https://github.com/mermaid-js/mermaid/tree/v11.12.3
- Distribution: https://registry.npmjs.org/mermaid/-/mermaid-11.12.3.tgz
- npm integrity (verified before extracting): `sha512-wN5ZSgJQIC+CHJut9xaKWsknLxaFBwCPwPkGTSUYrTiHORWvpT8RxGk849HPnpUAQ+/9BPRqYb80jTpearrHzQ==`
- `mermaid.min.js` SHA-256: `71dc54e481779b526de5e615c2eb00c99fa5acb75da9c5524d02e333d2fda89a`
- Upstream prebuilt full browser bundle, unchanged; no dynamic imports (no CDN or runtime npm dependency).
- Mermaid's MIT license is in `LICENSE`; embedded third-party license notices remain in the unmodified bundle.
- `index.html` and `renderer.js` are the application-owned restricted rasterization adapter.

The renderer uses an independent WebView2 profile, never a session browser. It accepts at most 32K characters and 500 edges, rejects diagram-level configuration directives/frontmatter, uses strict Mermaid sanitization, blocks navigation/new windows/downloads/permissions and all resource requests except these three local assets. CSP denies external resources, connections, inline scripts, frames, forms and objects. The diagram is rasterized (at most 2000×2000) before crossing back into the native image view. No graph callbacks are bound. Original code remains available through the native code/copy surface on success and on error.

Diagram rendering is serial, starts only for loaded views, cancels on unload, and closes the temporary WebView after completion. Ordinary Markdown stays native. Theme changes rerender the image; parse errors show readable feedback and expand the original source. External icon/image resources, embedded HTML labels and custom renderer configuration are deliberately not supported.
