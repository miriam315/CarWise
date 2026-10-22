import {test} from 'node:test';
import assert from 'node:assert/strict';
import ts from 'typescript';
import {readFile} from 'node:fs/promises';

const source=await readFile(new URL('../src/browserOcrImage.ts',import.meta.url),'utf8');
const {outputText}=ts.transpileModule(source,{compilerOptions:{target:ts.ScriptTarget.ES2022,module:ts.ModuleKind.ES2022}});
const {prepareOcrImage}=await import('data:text/javascript;base64,'+Buffer.from(outputText).toString('base64'));
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const deferred=()=>{let resolve,reject;const promise=new Promise((yes,no)=>{resolve=yes;reject=no});return{promise,resolve,reject}};

test('Already cancelled extraction never starts encoding',async()=>{
 const controller=new AbortController();controller.abort();let calls=0;
 await assert.rejects(prepareOcrImage({toBlob(){calls++}},controller.signal),{name:'AbortError'});
 assert.equal(calls,0);
});
test('Cancel during toBlob ignores the late callback and never reads its document',async()=>{
 const controller=new AbortController();let callback,reads=0;
 const task=prepareOcrImage({toBlob(done){callback=done}},controller.signal);
 const rejected=assert.rejects(task,{name:'AbortError'});controller.abort();await rejected;
 callback({arrayBuffer(){reads++;return Promise.resolve(new ArrayBuffer(8))}});
 await tick();assert.equal(reads,0);
});
for(const rejectLate of [false,true]){
 test('Cancel during byte preparation handles late '+(rejectLate?'failure':'success')+' without worker submission',async()=>{
  const controller=new AbortController(),bytes=deferred();let submitted=0;
  const task=prepareOcrImage({toBlob(done){done({arrayBuffer:()=>bytes.promise})}},controller.signal).then(value=>{submitted++;return value});
  await tick();const rejected=assert.rejects(task,{name:'AbortError'});controller.abort();await rejected;
  if(rejectLate)bytes.reject(Error('late encoder failure'));else bytes.resolve(new ArrayBuffer(8));
  await tick();assert.equal(submitted,0);
 });
}
test('Lossless PNG bytes are fully prepared before recognition can start',async()=>{
 const controller=new AbortController();let mime;
 const value=await prepareOcrImage({toBlob(done,type){mime=type;done(new Blob([new Uint8Array([137,80,78,71])]))}},controller.signal);
 assert.equal(mime,'image/png');assert.deepEqual(value,new Uint8Array([137,80,78,71]));
 controller.abort();
});
test('Encoding failures reject the extraction rather than escaping callbacks',async()=>{
 await assert.rejects(prepareOcrImage({toBlob(done){done(null)}},new AbortController().signal),/לא ניתן להכין/);
 await assert.rejects(prepareOcrImage({toBlob(){throw Error('encoder failed')}},new AbortController().signal),/encoder failed/);
 await assert.rejects(prepareOcrImage({toBlob(done){done({arrayBuffer(){throw Error('read failed')}})}},new AbortController().signal),/read failed/);
});
