import {useEffect,useRef,useState} from 'react';
import {getDocument,GlobalWorkerOptions} from 'pdfjs-dist';
import type {PDFDocumentProxy,RenderTask} from 'pdfjs-dist';
GlobalWorkerOptions.workerSrc=new URL('pdfjs-dist/build/pdf.worker.min.mjs',import.meta.url).href;
export default function PayslipPdfPreview({file,zoom}:{file:File;zoom:number}){
 const canvas=useRef<HTMLCanvasElement>(null);
 const [document,setDocument]=useState<PDFDocumentProxy|null>(null),[page,setPage]=useState(1),[error,setError]=useState(''),[loading,setLoading]=useState(true);
 useEffect(()=>{
  let active=true;let task:ReturnType<typeof getDocument>|undefined;
  setDocument(null);setPage(1);setError('');setLoading(true);
  const timeout=window.setTimeout(()=>{if(active){setError('לא ניתן לטעון את ה־PDF בזמן סביר. אפשר לנסות קובץ קטן יותר או תמונה.');setLoading(false);void task?.destroy().catch(()=>{})}},15000);
  void file.arrayBuffer().then(async data=>{
   if(!active)return;
   task=getDocument({data:new Uint8Array(data),enableXfa:false,stopAtErrors:true});
   const pdf=await task.promise;if(active){window.clearTimeout(timeout);setDocument(pdf)}else await task.destroy();
  }).catch(()=>{if(active){window.clearTimeout(timeout);setError('לא ניתן להציג את ה־PDF. בדקו שהוא תקין ואינו מוגן בסיסמה, או בחרו תמונה.');setLoading(false)}});
  return()=>{active=false;window.clearTimeout(timeout);void task?.destroy().catch(()=>{})};
 },[file]);
 useEffect(()=>{
  if(!document)return;
  let active=true,expired=false;let rendering:RenderTask|undefined;setLoading(true);setError('');
  const timeout=window.setTimeout(()=>{if(active){expired=true;rendering?.cancel();void document.loadingTask.destroy().catch(()=>{});setError('הצגת העמוד לא הסתיימה בזמן. נסו קובץ קטן יותר או תמונה.');setLoading(false)}},15000);
  void document.getPage(page).then(async pdfPage=>{
   if(!active||expired||!canvas.current)return;
   const size=pdfPage.getViewport({scale:1});const scale=Math.min(2,2000/Math.max(size.width,size.height));
   const viewport=pdfPage.getViewport({scale});const target=canvas.current;target.width=Math.ceil(viewport.width);target.height=Math.ceil(viewport.height);
   rendering=pdfPage.render({canvas:target,viewport});await rendering.promise;
   window.clearTimeout(timeout);if(active&&!expired)setLoading(false);
  }).catch(()=>{window.clearTimeout(timeout);if(active&&!expired){setError('לא ניתן להציג את העמוד הזה. נסו עמוד אחר או קובץ אחר.');setLoading(false)}});
  return()=>{active=false;window.clearTimeout(timeout);rendering?.cancel()};
 },[document,page]);
 return <><div className="payslip-preview-pages"><button type="button" className="outline-button" disabled={!document||document.loadingTask.destroyed||page<=1||loading} onClick={()=>setPage(p=>p-1)}>העמוד הקודם</button><span aria-live="polite">עמוד {page} מתוך {document?.numPages??'…'}</span><button type="button" className="outline-button" disabled={!document||document.loadingTask.destroyed||page>=document.numPages||loading} onClick={()=>setPage(p=>p+1)}>העמוד הבא</button></div>{loading&&<p role="status">טוענים את התלוש…</p>}{error&&<p role="alert">{error}</p>}<div className="payslip-preview-body" dir="ltr" hidden={!!error}><canvas ref={canvas} style={{width:zoom+'%'}} aria-label={'תצוגת PDF של התלוש — עמוד '+page}/></div></>;
}
