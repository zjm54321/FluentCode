/* App-owned adapter. Mermaid is vendored separately; never fetch runtime resources. */
window.renderDiagram = async (source, dark, token) => {
  let url;
  try {
    if (source.length > 32768 || source.includes('%%{') || source.trimStart().startsWith('---')) throw new Error('unsupported configuration');
    mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: dark ? 'dark' : 'default',
      htmlLabels: false, maxTextSize: 32768, maxEdges: 500,
      suppressErrorRendering: true, deterministicIds: true,
      secure: ['securityLevel', 'startOnLoad', 'maxTextSize', 'maxEdges', 'suppressErrorRendering'] });
    const result = await mermaid.render('diagramSvg', source);
    const host = document.getElementById('diagram');
    host.innerHTML = result.svg;
    const svg = host.querySelector('svg');
    // No interactive or externally referenced content survives into the raster image.
    svg.querySelectorAll('script,foreignObject,image,use,a').forEach(node => {
      if (node.localName === 'a') node.replaceWith(...node.childNodes); else node.remove();
    });
    for (const node of [svg, ...svg.querySelectorAll('*')]) {
      for (const attr of [...node.attributes]) {
        if (/^on/i.test(attr.name) || /href$/i.test(attr.name) || /url\(\s*['"]?(?!#)/i.test(attr.value)) node.removeAttribute(attr.name);
      }
    }
    const bounds = svg.viewBox.baseVal;
    if (!(bounds.width > 0 && bounds.height > 0)) throw new Error('empty diagram');
    const scale = Math.min(2, 2000 / bounds.width, 2000 / bounds.height, Math.sqrt(4000000 / (bounds.width * bounds.height)));
    const width = Math.max(1, Math.ceil(bounds.width * scale)), height = Math.max(1, Math.ceil(bounds.height * scale));
    svg.setAttribute('width', width); svg.setAttribute('height', height); svg.style.maxWidth = 'none';
    url = URL.createObjectURL(new Blob([new XMLSerializer().serializeToString(svg)], {type:'image/svg+xml'}));
    const image = new Image(); image.src = url; await image.decode();
    const canvas = document.createElement('canvas'); canvas.width = width; canvas.height = height;
    const ctx = canvas.getContext('2d'); ctx.fillStyle = dark ? '#202020' : '#ffffff'; ctx.fillRect(0, 0, width, height); ctx.drawImage(image, 0, 0);
    window.chrome.webview.postMessage({token, ok:true, png:canvas.toDataURL('image/png').split(',')[1], width, height});
  } catch (_) {
    window.chrome.webview.postMessage({token, ok:false});
  } finally {
    if (url) URL.revokeObjectURL(url);
    document.getElementById('diagram').replaceChildren();
  }
};
