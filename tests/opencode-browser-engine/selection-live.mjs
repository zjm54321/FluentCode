// Owned real-Chromium regression: the failing frontend-node contract and the repaired frame-scoped path.
import http from 'node:http';
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {mkdtemp, mkdir, readFile, rm, writeFile} from 'node:fs/promises';
import path from 'node:path';
const evidence=path.resolve(process.argv[2]??'artifacts/browser-selection-live/chrome');await mkdir(evidence,{recursive:true});
const profile=await mkdtemp('/tmp/opencode/aif-selection-live-');
const code=await readFile('src/AIFrontend/Pages/OpenCode/OpenCodeBrowserSelectionScript.cs','utf8');
const source=code.match(/Source = """\r?\n([\s\S]*?)\r?\n""";/)[1];
const server=http.createServer((req,res)=>{res.setHeader('Content-Type','text/html');res.end('<!doctype html>'+ (req.url==='/frame'
 ? '<style>body{margin:0;height:1500px}button{position:absolute;left:73px;top:141px;width:170px;height:48px}</style><button id="frame-target">Frame target</button>'
 : '<style>body{margin:0;height:2400px}main{width:70%;margin:0 auto;padding-top:620px}button{width:170px;height:48px}iframe{display:block;width:620px;height:430px;margin-top:60px;border:6px solid black}</style><main><button id="root-target">Root target</button><iframe src="/frame"></iframe></main>'));});
await new Promise(r=>server.listen(0,'127.0.0.1',r));
const chrome=spawn('google-chrome',['--headless=new','--no-sandbox','--no-proxy-server','--disable-gpu','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{stdio:['ignore','ignore','pipe']});
let stderr='';const endpoint=await new Promise((resolve,reject)=>{chrome.stderr.on('data',d=>{stderr+=d;const m=stderr.match(/DevTools listening on (ws:\/\/[^\s]+)/);if(m)resolve(m[1]);});chrome.on('exit',c=>reject(Error('Owned Chrome exit '+c+' '+stderr)));});
const ws=new WebSocket(endpoint);await new Promise(r=>ws.addEventListener('open',r,{once:true}));
let next=0,session;const pending=new Map(),events=[],contexts=new Map(),trace=[],checks=[],expectedErrors=[];
const send=(method,params={},sessionId=session)=>new Promise((resolve,reject)=>{
 const id=++next;const timer=setTimeout(()=>{pending.delete(id);reject(Error('CDP timeout: '+method));},10000);
 pending.set(id,{resolve,reject,method,params,timer});ws.send(JSON.stringify({id,method,params,...sessionId?{sessionId}:{}}));
});
ws.addEventListener('message',({data})=>{
 const m=JSON.parse(data);
 if(m.id){const p=pending.get(m.id);if(!p)return;pending.delete(m.id);clearTimeout(p.timer);trace.push({method:p.method,params:p.params,...m.error?{error:m.error}:{ok:true}});
  m.error?p.reject(Object.assign(Error(p.method+': '+JSON.stringify(m.error)),{cdp:m.error})):p.resolve(m.result);return;}
 events.push(m);if(m.method==='Runtime.executionContextCreated'&&m.params.context.auxData?.isDefault)contexts.set(m.params.context.auxData.frameId,m.params.context.id);
});
const evaluate=async(expression,contextId)=>{const r=await send('Runtime.evaluate',{expression,returnByValue:true,...contextId?{contextId}:{}});assert(!r.exceptionDetails,JSON.stringify(r.exceptionDetails));return r.result.value;};
const wait=async predicate=>{for(let i=0;i<150;i++){if(await predicate())return;await new Promise(r=>setTimeout(r,20));}throw Error('Controlled fixture deadline');};
const geometry=()=>evaluate('({width:innerWidth,height:innerHeight,x:scrollX,y:scrollY,rect:document.querySelector("#root-target").getBoundingClientRect().toJSON()})');
let error=null,version;
try{
 const target=await send('Target.createTarget',{url:'about:blank'});session=(await send('Target.attachToTarget',{targetId:target.targetId,flatten:true})).sessionId;
 for(const method of ['Page.enable','DOM.enable','Runtime.enable','Overlay.enable'])await send(method);
 await send('Emulation.setDeviceMetricsOverride',{width:1200,height:850,deviceScaleFactor:1,mobile:false});
 await send('Page.navigate',{url:`http://127.0.0.1:${server.address().port}/`});
 await wait(async()=>contexts.size>=2&&await evaluate('document.readyState==="complete"'));
 version=await send('Browser.getVersion');await evaluate('scrollTo(0,410)');
 const tree=(await send('Page.getFrameTree')).frameTree;
 for(const [frameId,contextId]of contexts){
  const selector=frameId===tree.frame.id?'#root-target':'#frame-target';
  const document=(await send('Runtime.evaluate',{expression:'document',contextId})).result.objectId;
  const frontend=await send('DOM.requestNode',{objectId:document});
  if(frontend.nodeId===0){
   try{await send('DOM.querySelector',{nodeId:0,selector});throw Error('Legacy zero node unexpectedly accepted');}
   catch(e){assert(e.cdp,'expected actual CDP rejection');expectedErrors.push({method:'DOM.querySelector',params:{nodeId:0,selector},error:e.cdp});}
  }
  // Exact repaired UI argument contract: query on this frame's document object, then backend identity.
  const found=await send('Runtime.callFunctionOn',{objectId:document,functionDeclaration:'function(selector){return this.querySelector(selector)}',arguments:[{value:selector}]});
  assert(found.result.objectId);
  const node=(await send('DOM.describeNode',{objectId:found.result.objectId})).node;
  assert(node.backendNodeId>0);assert(node.attributes.includes(selector.slice(1)));
  const resolved=(await send('DOM.resolveNode',{backendNodeId:node.backendNodeId,executionContextId:contextId})).object.objectId;
  const owns=await send('Runtime.callFunctionOn',{objectId:resolved,returnByValue:true,functionDeclaration:'function(){return this.ownerDocument===document}'});
  assert.equal(owns.result.value,true);
  for(const objectId of [resolved,found.result.objectId,document])await send('Runtime.releaseObject',{objectId});
  checks.push({name:'frame-scoped region query preserves backend/frame identity',frameId,selector,frontendNodeId:frontend.nodeId,backendNodeId:node.backendNodeId});
 }
 assert(expectedErrors.length>0,'legacy invalid-parameter path must be genuinely reproduced');
 await send('Runtime.addBinding',{name:'__aifSelectionSend'});await evaluate(source);
 await evaluate('window.__outlines=[];const nativeStroke=CanvasRenderingContext2D.prototype.strokeRect;CanvasRenderingContext2D.prototype.strokeRect=function(x,y,w,h){window.__outlines.push({x,y,w,h});return nativeStroke.call(this,x,y,w,h)}');
 await evaluate('window.__aifSelection.activate("Region","controlled")');
 const before=await geometry(),r=before.rect;
 for(const [type,x,y]of [['mousePressed',r.x-8,r.y-8],['mouseMoved',r.right+8,r.bottom+8],['mouseReleased',r.right+8,r.bottom+8]])
  await send('Input.dispatchMouseEvent',{type,x,y,button:'left',buttons:type==='mouseReleased'?0:1,clickCount:type==='mouseMoved'?0:1});
 await wait(()=>events.some(e=>e.method==='Runtime.bindingCalled'&&JSON.parse(e.params.payload).type==='region'));
 const message=JSON.parse(events.find(e=>e.method==='Runtime.bindingCalled'&&JSON.parse(e.params.payload).type==='region').params.payload);
 assert(message.selectors.includes('#root-target'));
 const viewport=(await send('Page.getLayoutMetrics')).cssVisualViewport;
 for(const name of ['pageX','pageY','clientWidth','clientHeight'])assert(Math.abs(message.viewport[name]-viewport[name])<.5,name);
 await evaluate('window.__aifSelection.capture(false)');
 const b=message.box,clip={x:b.x+viewport.pageX,y:b.y+viewport.pageY,width:b.width,height:b.height,scale:1};
 const png=await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:false,clip});
 await writeFile(path.join(evidence,'region.png'),Buffer.from(png.data,'base64'));
 await evaluate('window.__aifSelection.restore()');
 assert.deepEqual(await geometry(),before,'ordinary capture must not reflow the live page');
 checks.push({name:'real region input and viewport-only crop preserve exact scrolled layout',before,clip,selected:message.selectors});
 const documentBox={x:b.x+viewport.pageX,y:b.y+viewport.pageY,width:b.width,height:b.height};
 await evaluate('window.__aifSelection.selected('+JSON.stringify([{number:1,color:'#38BDF8',box:b,documentBox}])+')');
 await evaluate('window.__outlines=[];scrollBy(0,73)');
 await wait(async()=>Math.abs((await evaluate('scrollY'))-before.y-73)<.5);
 await wait(async()=>await evaluate('window.__outlines.length>0'));
 const outline=await evaluate('window.__outlines.at(-1)'),current=await geometry();
 assert(Math.abs(outline.y-(documentBox.y-current.y))<.5);assert(Math.abs(outline.x-documentBox.x+current.x)<.5);
 assert(Math.abs(current.rect.y-(before.rect.y-73))<.5);
 const visual=await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});
 await writeFile(path.join(evidence,'region-after-scroll.png'),Buffer.from(visual.data,'base64'));
 checks.push({name:'document anchored outline remains aligned after scroll',documentBox,outline,current});
}catch(e){error=e.stack;process.exitCode=1;}
finally{
 await writeFile(path.join(evidence,'results.json'),JSON.stringify({passed:!error,version,checks,expectedErrors,error,trace},null,2));
 console.log(JSON.stringify({passed:!error,checks:checks.length,expectedErrors,error,evidence},null,2));
 ws.close();chrome.kill();server.close();await rm(profile,{recursive:true,force:true,maxRetries:5,retryDelay:100});
}
