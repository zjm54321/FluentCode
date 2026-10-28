/* Real Chromium exercise of the exact vendored renderer, without the native WinUI host. */
(async () => {
  const results = []; let pending;
  window.chrome.webview = { postMessage: result => pending(result) };
  const render = (source, dark = false) => new Promise(resolve => {
    pending = resolve; window.renderDiagram(source, dark, 'test');
  });
  const check = (value, name) => { if (!value) throw new Error(name); results.push(name); };
  try {
    for (const [name, source] of [
      ['flowchart', 'flowchart LR\nA[开始] --> B{检查}\nB -->|是| C[完成]'],
      ['sequence', 'sequenceDiagram\nAlice->>Bob: Hello\nBob-->>Alice: Done'],
      ['class', 'classDiagram\nAnimal <|-- Duck\nAnimal : +int age'],
      ['pie', 'pie title Example\n"A" : 50\n"B" : 30']
    ]) {
      const output = await render(source);
      check(output.ok && output.width > 0 && output.height > 0 && atob(output.png).startsWith('\x89PNG'), name + ' genuine PNG');
    }
    const light = await render('flowchart LR\nA-->B', false), dark = await render('flowchart LR\nA-->B', true);
    check(light.ok && dark.ok && light.png !== dark.png, 'theme changes pixels');
    check(!(await render('flowchart LR\nA --> ]')).ok, 'parse error is reported, not rendered as an error diagram');
    check(!(await render('%%{init:{securityLevel:"loose"}}%%\nflowchart LR\nA-->B')).ok, 'directive cannot override sandbox');
    check(!(await render('---\nconfig:\n securityLevel: loose\n---\nflowchart LR\nA-->B')).ok, 'frontmatter cannot override sandbox');
    check(!(await render('x'.repeat(32769))).ok, 'input limit');
    window.__mermaidPwned = false;
    const hostile = await render('flowchart LR\nA["<img src=https://example.invalid/pixel onerror=window.__mermaidPwned=true>"] --> B\nclick A "javascript:window.__mermaidPwned=true"');
    check(!window.__mermaidPwned && document.querySelectorAll('img,script[src^="https:"]').length === 0, 'hostile HTML and click URL cannot execute or persist');
    check(document.getElementById('diagram').childNodes.length === 0, 'temporary DOM released');
    document.getElementById('results').textContent = JSON.stringify({pass:true, checks:results.length, results}, null, 2);
  } catch (error) {
    document.getElementById('results').textContent = JSON.stringify({pass:false, results, error:String(error)}, null, 2);
  }
})();
