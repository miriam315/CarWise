import {spawnSync} from 'node:child_process';
import {checkOcrDocker} from './ocr-docker.mjs';
try{checkOcrDocker({requireImage:true});}catch(error){console.error(error.message);process.exit(1);}
const result=spawnSync(process.execPath,['scripts/dotnet.mjs','test','backend/CarWise.Tests','--nologo','--filter','FullyQualifiedName~OcrSandbox'],{
 cwd:new URL('..',import.meta.url),stdio:'inherit',env:{...process.env,CARWISE_VERIFY_OCR:'true'}
});
process.exit(result.status??1);
