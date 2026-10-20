import {spawnSync} from 'node:child_process';
const result=spawnSync(process.execPath,['scripts/dotnet.mjs','test','backend/CarWise.Tests','--nologo','--filter','FullyQualifiedName~OcrSandbox'],{
 cwd:new URL('..',import.meta.url),stdio:'inherit',env:{...process.env,CARWISE_VERIFY_OCR:'true'}
});
process.exit(result.status??1);
