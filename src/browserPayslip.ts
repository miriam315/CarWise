import {createWorker,PSM,type Worker} from 'tesseract.js';
import {getDocument,GlobalWorkerOptions,Util,type PDFDocumentProxy} from 'pdfjs-dist';
import {extractLayout,mergeReads,parseTsv,repairHebrewLabels,type PayrollRead,type Word} from './payrollLayout';
GlobalWorkerOptions.workerSrc=new URL('pdfjs-dist/build/pdf.worker.min.mjs',import.meta.url).href;
export type OcrOptions={languages:string;minimumConfidence:number;maxPages:number;timeoutSeconds:number};
export async function extractInBrowser(file:File,options:OcrOptions,signal:AbortSignal,progress:(s:string)=>void):Promise<PayrollRead>{
 let worker:Worker|undefined,pdf:PDFDocumentProxy|undefined,creation:Promise<Worker>|undefined;let expired=false;
 const controller=new AbortController();const abort=()=>{expired=true;controller.abort();void worker?.terminate();void pdf?.loadingTask.destroy()};signal.addEventListener('abort',abort,{once:true});
 const timer=window.setTimeout(abort,Math.min(120,Math.max(10,options.timeoutSeconds))*1000);
 const check=()=>{if(signal.aborted||expired)throw Error(signal.aborted?'החילוץ בוטל.':'החילוץ בדפדפן לא הושלם בזמן. אפשר לנסות קובץ קטן יותר או לבחור במפורש חילוץ מבודד בשרת.')};
 function bounded<T>(task:Promise<T>):Promise<T>{
  check();return new Promise<T>((resolve,reject)=>{const stop=()=>reject(Error(signal.aborted?'החילוץ בוטל.':'החילוץ בדפדפן חרג מהזמן שהוגדר. לא בוצעה העלאה לשרת.'));controller.signal.addEventListener('abort',stop,{once:true});task.then(resolve,reject).finally(()=>controller.signal.removeEventListener('abort',stop))});
 }
 const reads:PayrollRead[]=[];const language=options.languages==='eng'?'eng':'heb+eng';
 async function recognize(canvas:HTMLCanvasElement,page:number){
  check();if(!worker){progress('טוענים מנוע מקומי וקובצי שפה מהאתר…');creation=createWorker(language,1,{workerPath:new URL('/ocr/runtime/worker.min.js',location.origin).href,corePath:new URL('/ocr/runtime',location.origin).href,langPath:new URL('/ocr/models',location.origin).href,workerBlobURL:false,gzip:true,cacheMethod:'none',logger:m=>{if(!expired&&m.status==='recognizing text')progress('קריאה במכשיר בלבד · '+Math.round(m.progress*100)+'%')},errorHandler:()=>{}});worker=await bounded(creation);check()}
  const passes:PayrollRead[]=[],recognized:Word[][]=[];
  for(const [mode,adaptive] of [[PSM.AUTO,false],[PSM.SINGLE_BLOCK,false],[PSM.SPARSE_TEXT,false],[PSM.SPARSE_TEXT,true]] as const){
   check();await bounded(worker.setParameters({tessedit_pageseg_mode:mode,thresholding_method:adaptive?'2':'0'}));check();
   const {data}=await bounded(worker.recognize(canvas,{}, {text:false,tsv:true}));check();const words=parseTsv(data.tsv||'',page).map(w=>({...w,pageWidth:canvas.width,pageHeight:canvas.height}));recognized.push(words);passes.push(extractLayout(words,options.minimumConfidence,'browser-ocr',controller.signal));
   if(Object.values(mergeReads(passes,'browser-ocr').fields).every(f=>f.value!=null))break;
  }
  if(language.includes('heb')&&Object.values(mergeReads(passes,'browser-ocr').fields).some(f=>f.value==null)){
   check();await bounded(worker.reinitialize('heb',1));await bounded(worker.setParameters({tessedit_pageseg_mode:PSM.SINGLE_BLOCK,thresholding_method:'0'}));
   const {data}=await bounded(worker.recognize(canvas,{}, {text:false,tsv:true}));check();const labels=parseTsv(data.tsv||'',page).map(w=>({...w,pageWidth:canvas.width,pageHeight:canvas.height}));
   for(const words of recognized)passes.push(extractLayout(repairHebrewLabels(words,labels,options.minimumConfidence),options.minimumConfidence,'browser-ocr',controller.signal));
   await bounded(worker.reinitialize(language,1));check();
  }
  return mergeReads(passes,'browser-ocr');
 }
 try{
  if(!globalThis.WebAssembly||!globalThis.Worker)throw Error('הדפדפן אינו תומך בחילוץ WebAssembly. אפשר לבחור במפורש חילוץ מבודד בשרת.');
  if(file.size>8*1024*1024)throw Error('המגבלה היא 8MB.');check();
  if(file.type==='application/pdf'){
   progress('קוראים את ה־PDF במכשיר בלבד…');const loading=getDocument({data:new Uint8Array(await file.arrayBuffer()),enableXfa:false,stopAtErrors:true});
   const cancelLoading=()=>void loading.destroy();controller.signal.addEventListener('abort',cancelLoading,{once:true});
   pdf=await loading.promise;controller.signal.removeEventListener('abort',cancelLoading);check();if(pdf.numPages>options.maxPages)throw Error('המסמך חורג ממגבלת העמודים שהוגדרה.');
   const textWords:Word[]=[];
   for(let page=1;page<=pdf.numPages;page++){
    check();const p=await pdf.getPage(page),viewport=p.getViewport({scale:1}),content=await p.getTextContent();
    for(const item of content.items){if(!('str' in item)||!item.str.trim())continue;
     const transform=Util.transform(viewport.transform,item.transform),height=Math.max(1,Math.hypot(transform[2],transform[3])),width=item.width;
     const chunks=item.str.trim().split(/\s+/);const ctx=document.createElement('canvas').getContext('2d')!;ctx.font=height+'px sans-serif';const measured=chunks.map(t=>ctx.measureText(t).width),space=ctx.measureText(' ').width,total=measured.reduce((a,b)=>a+b,0)+space*(chunks.length-1);let x=transform[4];
     const ordered=item.dir==='rtl'?[...chunks].reverse():chunks;
     for(const chunk of ordered){const partWidth=ctx.measureText(chunk).width/Math.max(1,total)*width;textWords.push({text:chunk,x,y:transform[5]-height,width:Math.max(1,partWidth),height,confidence:100,page:page-1,pageWidth:viewport.width,pageHeight:viewport.height});x+=partWidth+space/Math.max(1,total)*width}
    }
   }
   const digital=extractLayout(textWords,options.minimumConfidence,'browser-pdf-text',controller.signal);check();
   if(Object.values(digital.fields).some(f=>f.value!=null))return digital;
   for(let page=1;page<=pdf.numPages;page++){
    check();progress('קוראים עמוד '+page+' מתוך '+pdf.numPages+' במכשיר בלבד…');const p=await pdf.getPage(page),size=p.getViewport({scale:1}),viewport=p.getViewport({scale:Math.min(240/72,3200/Math.max(size.width,size.height))});
    const canvas=document.createElement('canvas');canvas.width=Math.ceil(viewport.width);canvas.height=Math.ceil(viewport.height);await p.render({canvas,viewport}).promise;
    try{reads.push(await recognize(canvas,page-1))}finally{canvas.width=0;canvas.height=0}
   }
  }else{
   const bitmap=await createImageBitmap(file);check();if(bitmap.width>8000||bitmap.height>8000||bitmap.width*bitmap.height>20_000_000){bitmap.close();throw Error('התמונה חורגת ממגבלת 20 מיליון פיקסלים או 8,000 פיקסלים בצלע.')}
   const canvas=document.createElement('canvas');canvas.width=bitmap.width;canvas.height=bitmap.height;canvas.getContext('2d')!.drawImage(bitmap,0,0);bitmap.close();
   try{reads.push(await recognize(canvas,0))}finally{canvas.width=0;canvas.height=0}
  }
  check();return mergeReads(reads,'browser-ocr');
 }catch(error){if(signal.aborted||expired)check();throw Error(error instanceof Error?error.message:'לא ניתן לקרוא את המסמך במכשיר. לא בוצעה העלאה לשרת.')}finally{
  window.clearTimeout(timer);signal.removeEventListener('abort',abort);expired=true;controller.abort();
  if(worker)await worker.terminate().catch(()=>{});else if(creation)void creation.then(w=>w.terminate()).catch(()=>{});
  await pdf?.loadingTask.destroy().catch(()=>{});
 }
}
