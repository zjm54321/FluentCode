# Browser runtime third-party notices

The prepared Windows x64 runtime redistributes these components:

- **Node.js 24.22.1**, Copyright Node.js contributors. Node.js is MIT licensed;
  its distribution includes separately licensed components. The complete upstream
  license and bundled-component notices are in `licenses/Node-LICENSE.txt`, and
  the upstream readme is in `licenses/Node-README.md`.
- **Lighthouse 13.4.1**, Copyright Google LLC and Lighthouse contributors,
  licensed under Apache-2.0. Its complete license is retained at
  `node_modules/lighthouse/LICENSE`.
- Lighthouse's locked production dependencies. `third-party-packages.json`
  records each installed package's exact name, version, license declaration,
  repository, npm integrity and top-level license/notice file paths. Their full
  original npm payloads, including nested notices, remain under `node_modules/`.

Do not trim `node_modules` or remove package metadata, licenses, notices,
localization, gatherer scripts, report assets or dependency data files when
publishing. Include this notice, `licenses/`, `third-party-packages.json`, and all
other prepared files alongside `node.exe`.

Lighthouse includes `puppeteer-core` and `chrome-launcher` as upstream production
dependencies. Preparation runs with install scripts disabled and does not fetch
Chromium. Their presence does not select an automation framework for FluentCode;
the sidecar uses the application's existing CDP/tunnel callbacks.

Sources: <https://nodejs.org/dist/v24.22.1/> and
<https://registry.npmjs.org/lighthouse/13.4.1>. Hash pins and the complete resolved
dependency graph are tracked in `runtime-versions.json` and `package-lock.json`.
