# Offline Mermaid renderer

- Mermaid **12.3.0**, MIT, https://github.com/mermaid-js/mermaid/tree/v12.3.0
- Distribution: https://registry.npmjs.org/mermaid/-/mermaid-12.3.0.tgz
- npm integrity (verified before extracting): `sha512-4113TdXeLAsMruJ79OpFL5O8+LTFXhQqZ260rF3tgUmiNAtRm4Uume04I+65PRN6yZ69J4f8rVYWW8zRk65UYw==`
- `mermaid.min.js` SHA-256: `336df803d87a38b70a01e736e666c8f7601d2ce42f5e47c8fafe5d721074b9a3`
- Upstream prebuilt full browser bundle, unchanged; no dynamic imports (no CDN or runtime npm dependency).
- Mermaid's MIT license is in `LICENSE`; embedded third-party license notices remain in the unmodified bundle.
- The bundle retains its DOMPurify (Apache-2.0/MPL-2.0), regenerator-runtime (MIT), Lodash (MIT), and Cytoscape (MIT) notices verbatim.
- `index.html` and `renderer.js` are the application-owned restricted rasterization adapter.

The renderer uses an independent WebView2 profile, never a session browser. It accepts at most 32K characters and 500 edges, rejects diagram-level configuration directives/frontmatter, uses strict Mermaid sanitization, blocks navigation/new windows/downloads/permissions and all resource requests except these three local assets. CSP denies external resources, connections, inline scripts, frames, forms and objects. The diagram is rasterized (at most 2000×2000) before crossing back into the native image view. No graph callbacks are bound. Original code remains available through the native code/copy surface on success and on error.

Diagram rendering is serial, starts only for loaded views, cancels on unload, and closes the temporary WebView after completion. Ordinary Markdown stays native. Theme changes rerender the image; parse errors show readable feedback and expand the original source. External icon/image resources, embedded HTML labels and custom renderer configuration are deliberately not supported.
