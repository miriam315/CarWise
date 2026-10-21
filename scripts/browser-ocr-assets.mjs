import {copyFile,mkdir,readFile} from 'node:fs/promises';
import {resolve,join} from 'node:path';
import {createHash} from 'node:crypto';
const root=resolve(import.meta.dirname,'..'),out=join(root,'public/ocr/runtime');
await mkdir(out,{recursive:true});
for(const name of ['worker.min.js','worker.min.js.LICENSE.txt'])await copyFile(join(root,'node_modules/tesseract.js/dist',name),join(out,name));
for(const name of ['tesseract-core-lstm','tesseract-core-simd-lstm'])for(const suffix of ['.wasm.js','.wasm'])
 await copyFile(join(root,'node_modules/tesseract.js-core',name+suffix),join(out,name+suffix));
const manifest=JSON.parse(await readFile(join(root,'public/ocr/models/manifest.json'),'utf8'));
for(const lang of ['heb','eng']){
 const bytes=await readFile(join(root,'public/ocr/models',lang+'.traineddata.gz'));
 if(createHash('sha256').update(bytes).digest('hex')!==manifest[lang].gzipSha256)throw Error('Browser OCR language checksum mismatch: '+lang);
}
console.log('Browser OCR assets ready: same-origin worker, WebAssembly and verified Hebrew/English language models.');
