// Installs language data only. Tesseract and Poppler run locally and never download on upload.
import {mkdir,readFile,writeFile} from 'node:fs/promises';
import {resolve} from 'node:path';
import {createHash} from 'node:crypto';
const root=resolve(import.meta.dirname,'..','.local','ocr','tessdata');
const revision='87416418657359cb625c412a48b6e1d6d41c29bd';
const hashes={eng:'7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2',heb:'11f9e43ab227f786352a50f75c94c2e9906f1baba86d93276da19da7ce0904db'};
await mkdir(root,{recursive:true,mode:0o700});
for(const [language,expected] of Object.entries(hashes)){
 const path=resolve(root,language+'.traineddata');
 const valid=bytes=>createHash('sha256').update(bytes).digest('hex')===expected;
 try{if(valid(await readFile(path))){console.log(language+': verified language data already installed');continue}}catch{}
 const response=await fetch(`https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/${revision}/${language}.traineddata`,{signal:AbortSignal.timeout(60000)});
 if(!response.ok)throw Error(`Language download failed (${response.status})`);
 const bytes=Buffer.from(await response.arrayBuffer());if(!valid(bytes))throw Error('OCR language SHA-256 mismatch; refusing to install');
 await writeFile(path,bytes,{mode:0o600});console.log(language+': installed SHA-256 verified language data');
}
console.log('Restart CarWise after installing local Tesseract and Poppler. See docs/local-payslip-ocr.md.');
