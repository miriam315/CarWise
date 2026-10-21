import {lazy,Suspense,useEffect,useRef,useState} from 'react';
import type {Box} from './payrollLayout';
import PayslipHighlights from './PayslipHighlights';
import {ZoomIn,ZoomOut} from 'lucide-react';
const PdfPreview=lazy(()=>import('./PayslipPdfPreview'));
export default function PayslipPreview({file,boxes}:{file:File;boxes?:Box[]}){
 const region=useRef<HTMLElement>(null);
 useEffect(()=>{if(file.type!=='application/pdf'&&boxes?.length)region.current?.querySelector('.payslip-highlight.value')?.scrollIntoView({behavior:'smooth',block:'center'})},[boxes,file]);
 const [url,setUrl]=useState(''),[zoom,setZoom]=useState(100),[imageError,setImageError]=useState(false);
 useEffect(()=>{setZoom(100);setImageError(false);if(file.type==='application/pdf'){setUrl('');return}const next=URL.createObjectURL(file);setUrl(next);return()=>URL.revokeObjectURL(next)},[file]);
 return <section ref={region} className="payslip-preview" aria-label="תצוגה מקדימה של התלוש"><div className="payslip-preview-toolbar"><strong>התלוש שבחרת</strong><div><button type="button" className="outline-button" aria-label="הקטנת תצוגת התלוש" disabled={zoom<=100} onClick={()=>setZoom(z=>Math.max(100,z-25))}><ZoomOut size={18}/></button><span aria-live="polite">{zoom}%</span><button type="button" className="outline-button" aria-label="הגדלת תצוגת התלוש" disabled={zoom>=250} onClick={()=>setZoom(z=>Math.min(250,z+25))}><ZoomIn size={18}/></button></div></div>{file.type==='application/pdf'?<Suspense fallback={<p role="status">טוענים תצוגת PDF…</p>}><PdfPreview file={file} zoom={zoom} boxes={boxes}/></Suspense>:imageError?<p role="alert">לא ניתן להציג את התמונה. בחרו תמונה תקינה עד 20 מיליון פיקסלים.</p>:<div className="payslip-preview-body" dir="ltr">{url&&<div className="payslip-image-layer" style={{width:zoom+'%'}}><img key={url} src={url} alt="תצוגה מקדימה של התלוש שנבחר" style={{width:'100%'}} onError={()=>setImageError(true)} onLoad={event=>{const image=event.currentTarget;if(image.naturalWidth*image.naturalHeight>20_000_000)setImageError(true)}}/><PayslipHighlights boxes={boxes} page={0}/></div>}</div>}</section>
}
