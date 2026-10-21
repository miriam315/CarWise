// Uses the original synthetic ZIP and actual production sandbox, without provider calls.
import {spawnSync} from 'node:child_process';
import {checkOcrDocker} from './ocr-docker.mjs';
try{checkOcrDocker({requireImage:true})}catch(error){console.error(error.message);process.exit(1)}
const result=spawnSync(process.execPath,['scripts/dotnet.mjs','test','backend/CarWise.Tests','--nologo','--filter','FullyQualifiedName~ComplexPayrollIntegrationTests'],{
 cwd:new URL('..',import.meta.url),stdio:'inherit',env:{...process.env,CARWISE_VERIFY_COMPLEX_OCR:'true'}
});
if(result.status===0)console.log('PASS: all 84 fields in the original 12 dense synthetic documents. Evidence: .local/complex-ocr-verification.json');
process.exit(result.status??1);
