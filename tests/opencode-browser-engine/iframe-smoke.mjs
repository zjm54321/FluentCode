import http from 'node:http';
import {spawn} from 'node:child_process';
import {mkdtemp, rm, writeFile} from 'node:fs/promises';
import {networkInterfaces} from 'node:os';
const profile=await mkdtemp('/tmp/opencode/browser-frame-probe-');
let rootPort,childPort;
const server=http.createServer((req,res)=>{res.setHeader('Content-Type','text/html');res.end(req.url==='/leaf'?'<style>body{margin:0;height:1000px}button{position:absolute;left:15px;top:30px;width:80px;height:30px}</style><button id="leafbtn">Leaf</button>':`<style>body{margin:0;height:2000px}iframe{position:absolute;left:80px;top:220px;width:600px;height:500px;border:10px solid black;transform-origin:0 0;transform:scale(.8) rotate(8deg)}</style><button id="rootbtn">Root</button><iframe src="http://localhost:${childPort}/child"></iframe><script>scrollTo(0,100)</script>`);});
const child=http.createServer((req,res)=>{res.setHeader('Content-Type','text/html');res.end(req.url==='/same'?'<style>body{margin:0}button{position:absolute;left:20px;top:30px;width:70px;height:25px}</style><button id="samebtn">Same</button>':`<style>body{margin:0;height:1400px}button{position:absolute;left:25px;top:40px;width:100px;height:30px}iframe{position:absolute;left:40px;top:120px;width:280px;height:200px;border:5px solid red;transform-origin:0 0;transform:scale(.85)}</style><button id="childbtn">Child</button><iframe src="http://localhost:${childPort}/same"></iframe><iframe style="top:380px" src="http://127.0.0.1:${rootPort}/leaf"></iframe><script>scrollTo(0,20)</script>`);});
await new Promise(r=>server.listen(0,'0.0.0.0',r));rootPort=server.address().port;
await new Promise(r=>child.listen(0,'0.0.0.0',r));childPort=child.address().port;
const chrome=spawn('google-chrome',['--headless=new','--no-sandbox','--no-proxy-server','--site-per-process','--disable-gpu','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'],{stdio:['ignore','ignore','pipe']});
let stderr='';const endpoint=await new Promise((resolve,reject)=>{chrome.stderr.on('data',d=>{stderr+=d;let m=stderr.match(/DevTools listening on (ws:\/\/[^\s]+)/);if(m)resolve(m[1]);});chrome.on('exit',c=>reject(Error('Chrome exit '+c+' '+stderr)));});
const ws=new WebSocket(endpoint);await new Promise(r=>ws.addEventListener('open',r,{once:true}));
let next=0,rootSession;const pending=new Map(),events=[],frames=new Map(),contexts=new Map(),attached=[];
const send=(method,params={},sessionId)=>new Promise((resolve,reject)=>{const id=++next;pending.set(id,{resolve,reject});ws.send(JSON.stringify({id,method,params,...sessionId?{sessionId}:{}}));});
async function enable(session){await send('Page.enable',{},session);await send('DOM.enable',{},session);await send('Runtime.enable',{},session);await send('Target.setAutoAttach',{autoAttach:true,waitForDebuggerOnStart:false,flatten:true},session);}
ws.addEventListener('message',({data})=>{const m=JSON.parse(data);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(Error(JSON.stringify(m.error))):p.resolve(m.result);return;}events.push(m);if(m.method==='Target.attachedToTarget'&&m.params.targetInfo.type==='iframe'){attached.push(enable(m.params.sessionId));}if(m.method==='Page.frameNavigated')frames.set(m.params.frame.id,{...m.params.frame,session:m.sessionId});if(m.method==='Runtime.executionContextCreated'&&m.params.context.auxData?.isDefault)contexts.set(m.params.context.auxData.frameId,{session:m.sessionId,id:m.params.context.id});});
let bridge;
try{
 const target=await send('Target.createTarget',{url:'about:blank'});rootSession=(await send('Target.attachToTarget',{targetId:target.targetId,flatten:true})).sessionId;
 await enable(rootSession);await send('Page.navigate',{url:`http://127.0.0.1:${rootPort}/`},rootSession);
 await new Promise((resolve,reject)=>{const timeout=setTimeout(()=>reject(Error('frames deadline '+JSON.stringify({frames:[...frames],contexts:[...contexts],attaches:events.filter(e=>e.method==='Target.attachedToTarget')}))),12000);const tick=()=>{if(contexts.size>=4){clearTimeout(timeout);resolve();}else setTimeout(tick,20)};tick()});await Promise.all(attached);
 for(const session of new Set([...contexts.values()].map(c=>c.session))){let t=(await send('Page.getFrameTree',{},session)).frameTree;const visit=n=>{frames.set(n.frame.id,{...n.frame,session});for(const c of n.childFrames??[])visit(c)};visit(t)}
 const records=[];
 for(const [frameId,ctx] of contexts){const q=await send('Runtime.evaluate',{expression:`(()=>{let e=document.querySelector('button');return e?{id:e.id,rect:e.getBoundingClientRect().toJSON(),w:innerWidth,h:innerHeight}:null})()`,contextId:ctx.id,returnByValue:true},ctx.session);if(!q.result.value)continue;const o=await send('Runtime.evaluate',{expression:'document.querySelector("button")',contextId:ctx.id},ctx.session);const n=await send('DOM.describeNode',{objectId:o.result.objectId},ctx.session);const b=await send('DOM.getBoxModel',{backendNodeId:n.node.backendNodeId},ctx.session);records.push({frameId,session:ctx.session===rootSession?null:ctx.session,contextId:ctx.id,backend:n.node.backendNodeId,...q.result.value,quad:b.model.border,frame:frames.get(frameId)});}
 console.log(JSON.stringify(records,null,2));
 await writeFile('/tmp/opencode/browser-frame-probe.json',JSON.stringify({records,events,rootSession},null,2));
 if(process.argv.includes('--bridge') || process.argv[2]?.endsWith('.dll')){
  bridge=http.createServer(async(req,res)=>{try{if(req.method==='GET'){res.end(JSON.stringify({records,events,rootSession}));return;}let body='';for await(const b of req)body+=b;const {method,parameters,session}=JSON.parse(body);res.setHeader('Content-Type','application/json');res.end(JSON.stringify(await send(method,parameters??{},session??rootSession)));}catch(e){res.statusCode=500;res.end(String(e));}});
  await new Promise(r=>bridge.listen(0,'0.0.0.0',r));
  if(process.argv[2]?.endsWith('.dll')){
   const host=Object.values(networkInterfaces()).flat().find(i=>!i.internal&&i.family==='IPv4').address;
   const test=spawn('dotnet.exe',[process.argv[2],process.argv[3],`http://${host}:${bridge.address().port}`],{stdio:'inherit'});
   const code=await new Promise(r=>test.on('exit',r));if(code!==0)throw Error('Live C# frame assertions failed: '+code);
  }else{console.log('BRIDGE_PORT='+bridge.address().port);await new Promise(r=>process.on('SIGTERM',r));}
 }
}finally{bridge?.close();ws.close();chrome.kill();server.close();child.close();await rm(profile,{recursive:true,force:true,maxRetries:5,retryDelay:100});}
