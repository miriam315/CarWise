import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {spawn} from 'node:child_process';

async function check(mode){
 const requests=[];
 const server=createServer((request,response)=>{
  requests.push(request.url);
  response.setHeader('Content-Type','application/json');
  if(request.url==='/api/runtime')return response.end(JSON.stringify({requiresSetup:mode==='setup',authenticationRequired:true,demoMode:false}));
  if(request.url==='/api/health'){if(mode==='bad-json'){response.setHeader('Content-Type','text/html');return response.end('<html>error</html>')}return response.end(JSON.stringify({status:'ok',records:77988,database:'test'}))}
  if(request.url==='/'){response.setHeader('Content-Type','text/html');return response.end(`<html><div id="root"></div><script src="${mode==='external'?'https://external.invalid/app.js':'/assets/app.js'}"></script><link href="/assets/app.css" rel="stylesheet"></html>`)}
  if(request.url==='/assets/app.js'){response.setHeader('Content-Type',mode==='spa-fallback'?'text/html':'text/javascript');return response.end(mode==='spa-fallback'?'<html>fallback</html>':'console.log("fixture");')}
  if(request.url==='/assets/app.css'){response.setHeader('Content-Type','text/css');response.statusCode=mode==='missing-css'?404:200;return response.end('body{}')}
  response.statusCode=404;response.end('{}');
 });
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 try{
  const child=spawn(process.execPath,['scripts/check-cloud.mjs',`http://127.0.0.1:${server.address().port}`],{stdio:['ignore','pipe','pipe']});
  let output='';child.stdout.on('data',data=>output+=data);child.stderr.on('data',data=>output+=data);
  const exit=await new Promise((resolve,reject)=>{child.on('error',reject);child.on('close',resolve)});
  return{exit,report:JSON.parse(output),requests};
 }finally{await new Promise(resolve=>server.close(resolve))}
}
test('Readiness confirms health, built assets and read-only scope',async()=>{
 const result=await check('ok');assert.equal(result.exit,0);assert.equal(result.report.passed,true);assert.equal(result.requests.length,5);
 assert.match(result.report.scope,/not verified/);assert.equal(result.report.officialRecords,77988);
});
for(const [mode,message] of [['setup',/database setup/],['bad-json',/did not return JSON/],['spa-fallback',/did not return JavaScript/],['missing-css',/HTTP 404/],['external',/site origin/]]){
 test('Readiness rejects '+mode,async()=>{const result=await check(mode);assert.equal(result.exit,1);assert.equal(result.report.passed,false);assert.match(result.report.error,message)});
}
