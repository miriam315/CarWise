// Read-only deployment check. No login, payroll writes, OCR, or AI calls.
const value=process.argv[2];
if(!value){console.error('Usage: npm run verify:cloud -- https://YOUR-SITE');process.exit(1)}
const base=new URL(value);
if(base.username||base.password||base.search||base.hash||base.pathname!=='/'||!(base.protocol==='https:'||base.protocol==='http:'&&['localhost','127.0.0.1'].includes(base.hostname)))throw Error('Use an HTTPS site origin, without credentials, query or path. HTTP is allowed for localhost only.');
const started=performance.now(),checks=[];
async function read(path,format='json'){
 const begin=performance.now();const url=new URL(path,base);
 if(url.origin!==base.origin)throw Error('Assets must be served from the site origin.');
 const response=await fetch(url,{signal:AbortSignal.timeout(15000),redirect:'error'});
 if(!response.ok)throw Error(path+' returned HTTP '+response.status);
 if(format==='json'&&!response.headers.get('content-type')?.includes('application/json'))throw Error(path+' did not return JSON.');
 if(format==='js'&&!/(?:text|application)\/(?:javascript|ecmascript)/i.test(response.headers.get('content-type')||''))throw Error(path+' did not return JavaScript.');
 if(format==='css'&&!response.headers.get('content-type')?.includes('text/css'))throw Error(path+' did not return CSS.');
 const body=format==='json'?await response.json():await response.text();
 checks.push({path,milliseconds:Math.round(performance.now()-begin)});return body;
}
try{
 const runtime=await read('/api/runtime');if(runtime.requiresSetup)throw Error('Server is still waiting for database setup.');
 if(typeof runtime.authenticationRequired!=='boolean'||typeof runtime.demoMode!=='boolean')throw Error('Runtime metadata is incomplete.');
 const health=await read('/api/health');if(health.status!=='ok'||!(health.records>0))throw Error('Health check did not confirm loaded official data.');
 const html=await read('/','html');if(!html.includes('id="root"'))throw Error('Application HTML is missing.');
 const scripts=[...html.matchAll(/<script[^>]+src="([^"]+)"/g)].map(m=>m[1]);
 const styles=[...html.matchAll(/<link[^>]+href="([^"]+\.css)"/g)].map(m=>m[1]);
 if(!scripts.length||!styles.length)throw Error('Built JavaScript/CSS assets were not found. Run npm run build before deployment.');
 for(const asset of scripts)await read(asset,'js');
 for(const asset of styles)await read(asset,'css');
 console.log(JSON.stringify({passed:true,checks,totalMilliseconds:Math.round(performance.now()-started),database:health.database,officialRecords:health.records,authenticationRequired:runtime.authenticationRequired,demoMode:runtime.demoMode,aiMode:runtime.aiMode,scope:'Read-only readiness. Login, payroll journey, OCR and real AI are not verified by this command.'},null,2));
}catch(error){console.error(JSON.stringify({passed:false,error:error.message,checks},null,2));process.exitCode=1}
