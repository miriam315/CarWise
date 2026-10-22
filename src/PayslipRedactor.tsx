import {useEffect,useRef,useState} from 'react';
import {getDocument,GlobalWorkerOptions} from 'pdfjs-dist';
import type {PDFDocumentLoadingTask} from 'pdfjs-dist';
import {rasterPdf} from './redactedPdf';
GlobalWorkerOptions.workerSrc=new URL('pdfjs-dist/build/pdf.worker.min.mjs',import.meta.url).href;
type Rect={x:number;y:number;width:number;height:number};
export default function PayslipRedactor({file,disabled,onPrepared,onInvalidate}:{file:File;disabled:boolean;onPrepared:(file:File)=>void;onInvalidate:()=>void}){
 const [pages,setPages]=useState<HTMLCanvasElement[]>([]),[page,setPage]=useState(0),[masks,setMasks]=useState<Rect[][]>([]),[reviewed,setReviewed]=useState<boolean[]>([]),[error,setError]=useState(''),[loading,setLoading]=useState(true),[saving,setSaving]=useState(false),[ready,setReady]=useState(false),[draft,setDraft]=useState<Rect|null>(null),[zoom,setZoom]=useState(100);
 const canvas=useRef<HTMLCanvasElement>(null),start=useRef<{x:number;y:number}|null>(null),generation=useRef(0);
 useEffect(()=>{
  const version=++generation.current;let task:PDFDocumentLoadingTask|undefined;const built:HTMLCanvasElement[]=[];
  setLoading(true);setSaving(false);setZoom(100);setDraft(null);start.current=null;setError('');setPages([]);setPage(0);setMasks([]);setReviewed([]);setReady(false);
  const active=()=>version===generation.current;
  const make=(width:number,height:number)=>{const c=document.createElement('canvas');c.width=width;c.height=height;c.getContext('2d')!.fillStyle='#fff';c.getContext('2d')!.fillRect(0,0,width,height);built.push(c);return c};
  const timer=window.setTimeout(()=>{if(active()){generation.current++;setError('הכנת התצוגה ארכה מדי. נסו מסמך קטן יותר.');setLoading(false);void task?.destroy().catch(()=>{})}},30000);
  void (async()=>{
   if(file.type==='application/pdf'){
    const data=await file.arrayBuffer();if(!active())return;
    task=getDocument({data:new Uint8Array(data),enableXfa:false,stopAtErrors:true});const pdf=await task.promise;
    if(pdf.numPages>6)throw Error('אפשר לעבד עד 6 עמודים בתלוש.');
    for(let i=1;i<=pdf.numPages;i++){if(!active())return;const p=await pdf.getPage(i),size=p.getViewport({scale:1}),viewport=p.getViewport({scale:Math.min(200/72,2400/Math.max(size.width,size.height))});
     const target=make(Math.ceil(viewport.width),Math.ceil(viewport.height));await p.render({canvas:target,viewport,background:'#ffffff'}).promise;}
   }else{
    const bitmap=await createImageBitmap(file);try{if(!active())return;if(bitmap.width*bitmap.height>20_000_000||Math.max(bitmap.width,bitmap.height)>8000)throw Error('התמונה גדולה מדי. עד 20 מיליון פיקסלים ו־8,000 פיקסלים בצלע.');
     const scale=Math.min(1,2400/Math.max(bitmap.width,bitmap.height));const target=make(Math.max(1,Math.round(bitmap.width*scale)),Math.max(1,Math.round(bitmap.height*scale)));target.getContext('2d')!.drawImage(bitmap,0,0,target.width,target.height);
    }finally{bitmap.close()}
   }
   if(active()){setPages(built);setMasks(built.map(()=>[]));setReviewed(built.map(()=>false));setLoading(false)}
  })().catch(e=>{if(active()){setError(e instanceof Error?e.message:'לא ניתן להציג את המסמך.');setLoading(false)}}).finally(()=>{window.clearTimeout(timer);void task?.destroy().catch(()=>{})});
  return()=>{generation.current++;window.clearTimeout(timer);void task?.destroy().catch(()=>{});for(const c of built){c.width=0;c.height=0}};
 },[file]);
 useEffect(()=>{
  const target=canvas.current,source=pages[page];if(!target||!source)return;target.width=source.width;target.height=source.height;const context=target.getContext('2d')!;context.drawImage(source,0,0);context.fillStyle='#000';
  for(const r of [...(masks[page]||[]),...(draft?[draft]:[])]){const x=Math.max(0,Math.floor(r.x*target.width)-2),y=Math.max(0,Math.floor(r.y*target.height)-2);context.fillRect(x,y,Math.ceil(r.width*target.width)+4,Math.ceil(r.height*target.height)+4)}
 },[pages,page,masks,draft]);
 const invalidate=()=>{setReady(false);onInvalidate()};
 function position(e:React.PointerEvent<HTMLCanvasElement>){const box=e.currentTarget.getBoundingClientRect();return{x:Math.max(0,Math.min(1,(e.clientX-box.left)/box.width)),y:Math.max(0,Math.min(1,(e.clientY-box.top)/box.height))}}
 function rectangle(point:{x:number;y:number}):Rect{const s=start.current!;return{x:Math.min(s.x,point.x),y:Math.min(s.y,point.y),width:Math.abs(s.x-point.x),height:Math.abs(s.y-point.y)}}
 function changeMasks(next:Rect[]){invalidate();setMasks(all=>all.map((m,i)=>i===page?next:m));setReviewed(all=>all.map((v,i)=>i===page?false:v))}
 async function prepare(){if(disabled||saving||!pages.length||!reviewed.every(Boolean))return;const version=generation.current;setSaving(true);setError('');
  try{const images=[];for(let i=0;i<pages.length;i++){
   const target=document.createElement('canvas');target.width=pages[i].width;target.height=pages[i].height;const context=target.getContext('2d')!;context.drawImage(pages[i],0,0);context.fillStyle='#000';
   for(const r of masks[i])context.fillRect(Math.max(0,Math.floor(r.x*target.width)-2),Math.max(0,Math.floor(r.y*target.height)-2),Math.ceil(r.width*target.width)+4,Math.ceil(r.height*target.height)+4);
   try{const blob=await new Promise<Blob>((resolve,reject)=>target.toBlob(b=>b?resolve(b):reject(Error('יצירת העותק נכשלה.')),'image/jpeg',.94));if(version!==generation.current)return;images.push({bytes:new Uint8Array(await blob.arrayBuffer()),width:target.width,height:target.height})}finally{target.width=0;target.height=0}
  }
  const bytes=rasterPdf(images);if(bytes.length>8*1024*1024)throw Error('העותק גדול מ־8MB. בחרו מסמך קצר יותר.');if(version!==generation.current)return;
  onPrepared(new File([bytes],'redacted-payslip.pdf',{type:'application/pdf'}));setReady(true);
  }catch(e){if(version===generation.current)setError((e as Error).message)}finally{if(version===generation.current)setSaving(false)}
 }
 const blocked=disabled||saving;
 return <section className="payslip-redactor" aria-label="השחרת פרטים אישיים"><h3>לפני השליחה — מסירים פרטים אישיים</h3><p>גררו על שם, ת״ז, כתובת, מספר עובד, חשבון בנק, ברקודים וכל פרט מזהה נוסף. בדקו את כל העמודים; אין זיהוי אוטומטי של פרטים אישיים. גם אחרי השחרה נתוני השכר רגישים.</p><p>המסמך נשאר במכשיר בשלב הזה. יישלח רק עותק חדש מתמונות העמודים, בלי שכבת הטקסט והמטא־דאטה המקוריים.</p>
 {loading&&<p role="status">מכינים תצוגה במכשיר…</p>}{error&&<p role="alert">{error}</p>}
 {!!pages.length&&<><div className="payslip-preview-pages"><button type="button" className="outline-button" disabled={blocked||page===0} onClick={()=>{setDraft(null);start.current=null;setPage(p=>p-1)}}>העמוד הקודם</button><span>עמוד {page+1} מתוך {pages.length}</span><button type="button" className="outline-button" disabled={blocked||page===pages.length-1} onClick={()=>{setDraft(null);start.current=null;setPage(p=>p+1)}}>העמוד הבא</button></div>
 <div className="payslip-controls"><button type="button" className="outline-button" aria-label="הקטנת תצוגת ההשחרה" disabled={blocked||zoom===100} onClick={()=>setZoom(z=>Math.max(100,z-25))}>הקטנה</button><span>{zoom}%</span><button type="button" className="outline-button" aria-label="הגדלת תצוגת ההשחרה" disabled={blocked||zoom===300} onClick={()=>setZoom(z=>Math.min(300,z+25))}>הגדלה</button></div><div className="redaction-canvas-wrap"><canvas ref={canvas} aria-label="סימון אזורים להשחרה בתלוש" style={{touchAction:'none',cursor:blocked?'default':'crosshair',width:zoom+'%',maxWidth:'none',display:'block'}}
 onPointerDown={e=>{if(blocked||e.button!==0)return;invalidate();setReviewed(all=>all.map((v,i)=>i===page?false:v));start.current=position(e);e.currentTarget.setPointerCapture(e.pointerId)}} onPointerMove={e=>{if(start.current&&!blocked)setDraft(rectangle(position(e)))}}
 onPointerUp={e=>{if(!start.current)return;const r=rectangle(position(e));start.current=null;setDraft(null);if(!blocked&&r.width>.002&&r.height>.002)changeMasks([...(masks[page]||[]),r])}} onPointerCancel={()=>{start.current=null;setDraft(null)}}/></div>
 <div className="payslip-controls"><button type="button" className="outline-button" disabled={blocked||!masks[page]?.length} onClick={()=>changeMasks(masks[page].slice(0,-1))}>ביטול השחרה אחרונה</button><button type="button" className="outline-button" disabled={blocked||!masks[page]?.length} onClick={()=>changeMasks([])}>ניקוי השחרות בעמוד</button></div>
 <label className="check-row"><input type="checkbox" aria-label={'בדקתי את עמוד '+(page+1)} checked={reviewed[page]||false} disabled={blocked} onChange={e=>{invalidate();setReviewed(all=>all.map((v,i)=>i===page?e.target.checked:v))}}/> בדקתי את עמוד {page+1} והסתרתי את הפרטים שאיני רוצה לשלוח</label>
 <p>{reviewed.filter(Boolean).length} מתוך {pages.length} עמודים נבדקו. אם אין פרטים להסתרה, אפשר לאשר אחרי בדיקה.</p>
 <button type="button" className="outline-button" disabled={blocked||!reviewed.every(Boolean)} onClick={()=>void prepare()}>{saving?'יוצרים עותק…':'יצירת עותק בדוק לשליחה'}</button>{ready&&<p role="status">העותק הבדוק מוכן. בדקו את התצוגה שלו למטה ואשרו שליחה ל־AI.</p>}</>}
 </section>;
}
