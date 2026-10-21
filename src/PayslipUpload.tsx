import {useEffect,useRef,useState} from 'react';
import {Upload,Camera,FileCheck,ShieldCheck,CheckCircle2,AlertCircle,X} from 'lucide-react';
import {api,upload} from './api';
import PayslipCamera from './PayslipCamera';
import PayslipPreview from './PayslipPreview';
import type {Employee} from './types';
const labels:Record<string,string>={salary:'שכר בסיס חודשי (₪)',commission:'עמלות חודשיות (₪)',bonus:'בונוס חודשי (₪)',pension:'פנסיית עובד (%)',fund:'קרן השתלמות עובד (%)',credits:'נקודות זיכוי',participation:'השתתפות עובד ברכב (₪)'};
const maxima:Record<string,number>={salary:200000,commission:200000,bonus:200000,pension:20,fund:20,credits:30,participation:20000};
export default function PayslipUpload({employeeId,enabled,disabledReason,onApply}:{employeeId:string;enabled:boolean;disabledReason?:string;onApply:(patch:Partial<Employee>)=>void}){
 const [file,setFile]=useState<File|null>(null),[consent,setConsent]=useState(false),[busy,setBusy]=useState(false),[error,setError]=useState(''),[result,setResult]=useState<any>(null),[values,setValues]=useState<Record<string,string>>({}),[selected,setSelected]=useState<Record<string,boolean>>({}),[applied,setApplied]=useState(false);
 const [camera,setCamera]=useState(false),[dragging,setDragging]=useState(false);
 const selection=useRef(0),dragDepth=useRef(0),picker=useRef<HTMLInputElement>(null);
 const [availability,setAvailability]=useState({enabled,message:disabledReason||'חילוץ תלושים אינו זמין כרגע. פנו למנהל המערכת.'});
 useEffect(()=>{
  let active=true;setAvailability({enabled,message:disabledReason||'חילוץ תלושים אינו זמין כרגע. פנו למנהל המערכת.'});
  const refresh=()=>{api('/payslip/status').then(s=>{if(active)setAvailability(s)}).catch(()=>{})};
  refresh();window.addEventListener('focus',refresh);return()=>{active=false;window.removeEventListener('focus',refresh);selection.current++};
 },[employeeId,enabled,disabledReason]);
 function reset(){selection.current++;setFile(null);setConsent(false);setResult(null);setValues({});setSelected({});setApplied(false);setError('')}
 async function choose(files:File[]){
  if(busy)return;
  reset();const id=selection.current;
  if(files.length!==1){setError('בחרו תלוש אחד בכל פעם.');return}
  const original=files[0];
  if(original.size>8*1024*1024){setError('המגבלה היא 8MB.');return}
  try{
   const bytes=new Uint8Array(await original.slice(0,16).arrayBuffer());
   if(id!==selection.current)return;
   const starts=(signature:number[])=>signature.every((value,index)=>bytes[index]===value);
   const type=bytes.length<16?null:starts([37,80,68,70,45])?'application/pdf':starts([137,80,78,71,13,10,26,10])?'image/png':starts([255,216,255])?'image/jpeg':null;
   if(!type){setError('אפשר לבחור PDF, PNG או JPEG תקינים בלבד. תמונות HEIC אינן נתמכות.');return}
   setFile(new File([original],original.name,{type,lastModified:original.lastModified}));
  }catch{if(id===selection.current)setError('לא ניתן לקרוא את הקובץ. בחרו אותו שוב.')}
 }
 async function refreshAvailability(){try{setAvailability(await api('/payslip/status'));setError('')}catch(e){setError((e as Error).message)}}
 async function extract(){if(!file||!consent||busy||!availability.enabled)return;setBusy(true);setError('');setResult(null);setApplied(false);try{const r=await upload('/payslip/extract?employee='+encodeURIComponent(employeeId)+'&consent=true',file);setResult(r);setValues(Object.fromEntries(Object.entries(r.fields).map(([key,f]:[string,any])=>[key,f.value==null?'':String(f.value)])));setSelected(Object.fromEntries(Object.keys(labels).map(key=>[key,false])))}catch(e){setError((e as Error).message)}finally{setBusy(false)}}
 function apply(){const patch:Record<string,number>={};for(const [key,checked] of Object.entries(selected)){if(!checked||!(key in maxima))continue;const value=Number(values[key]);if(values[key]===''||!Number.isFinite(value)||value<0||value>maxima[key]){setError('יש לבדוק את הערך בשדה '+labels[key]);return}patch[key]=value}if(!Object.keys(patch).length){setError('סמנו לפחות שדה אחד להעברה לטופס.');return}onApply(patch);setError('');setApplied(true);setResult(null)}
 return <section className="panel payslip-panel">
  <div className="payslip-heading"><span className="stat-icon lavender"><FileCheck size={22}/></span><div><h2>מהתלוש, ישר לפרטים שלך.</h2><p>מעלים, בודקים ומאשרים. שום נתון לא משנה את החישוב בלי אישורך.</p></div></div>
  <div className={'payslip-dropzone'+(dragging?' is-dragging':'')+(busy?' is-busy':'')} aria-label="אזור גרירת תלוש"
   onDragEnter={event=>{if(!event.dataTransfer.types.includes('Files'))return;event.preventDefault();dragDepth.current++;if(!busy)setDragging(true)}}
   onDragOver={event=>{if(!event.dataTransfer.types.includes('Files'))return;event.preventDefault();event.dataTransfer.dropEffect=busy?'none':'copy'}}
   onDragLeave={event=>{event.preventDefault();dragDepth.current=Math.max(0,dragDepth.current-1);if(!dragDepth.current)setDragging(false)}}
   onDrop={event=>{event.preventDefault();event.stopPropagation();dragDepth.current=0;setDragging(false);if(event.dataTransfer.types.includes('Files'))void choose(Array.from(event.dataTransfer.files))}}>
   <Upload size={30} aria-hidden="true"/><strong>{dragging?'אפשר לשחרר את התלוש כאן':'גררו לכאן תלוש שכר'}</strong><span>או בחרו קובץ / צלמו אותו ישירות באתר</span>
   <div className="payslip-controls"><button type="button" className="outline-button" disabled={busy} onClick={()=>picker.current?.click()}><Upload size={18}/> בחירת תלוש</button><input ref={picker} tabIndex={-1} className="payslip-file-input" aria-label="העלאת תלוש שכר" type="file" accept="application/pdf,image/png,image/jpeg,.pdf,.png,.jpg,.jpeg" disabled={busy} onChange={event=>{const files=Array.from(event.target.files||[]);event.target.value='';if(files.length)void choose(files)}}/><button type="button" className="outline-button" disabled={busy} onClick={()=>setCamera(true)}><Camera size={18}/> צילום תלוש</button></div>
   <small>PDF / PNG / JPEG · עד 8MB · קובץ אחד בכל פעם</small>
  </div>
  {!availability.enabled&&<div className="form-info"><ShieldCheck size={18}/><div>{availability.message}<br/>אפשר לבחור או לצלם תלוש ולהציג אותו כאן. לחילוץ נדרש מנוע זמין; אפשר גם להזין נתונים ידנית.<br/><button type="button" className="text-button" onClick={refreshAvailability}>בדיקת זמינות מחדש</button></div></div>}
  {file&&<><div className="payslip-file-summary"><strong>{file.name}</strong><span>{(file.size/1024/1024).toFixed(2)} MB</span><button type="button" className="outline-button" disabled={busy} onClick={reset}><X size={17}/> הסרת התלוש</button></div><PayslipPreview file={file}/></>}
  <p className="payslip-note">בחירה, גרירה וצילום מציגים את התלוש בדפדפן בלבד. הקובץ נשלח לשרת רק לאחר אישור ולחיצה על חילוץ נתונים. התצוגה מתנקה בהסרה או ביציאה מהמסך.</p>
  <label className="payslip-consent"><input type="checkbox" checked={consent} disabled={busy||!file||!availability.enabled} onChange={event=>setConsent(event.target.checked)}/> אני מאשר/ת לעבד את התלוש במנוע החילוץ המקומי של הארגון. המסמך והטקסט הגולמי אינם נשלחים לספק AI חיצוני ואינם נשמרים כתלוש במערכת. שדות שאאשר יישמרו ב־DB לאחר לחיצה על ״שמירה וחישוב מחדש״.</label>
  <button type="button" className="primary-button payslip-extract" disabled={!file||!consent||busy||!availability.enabled} onClick={extract}>{busy?'מחלצים נתונים…':'חילוץ נתונים מהתלוש'}</button>
  <p className="payslip-note">נחלץ רק שדות לחישוב: בסיס, עמלות, בונוס, אחוזי הפרשות עובד, נקודות זיכוי והשתתפות ברכב. אין להסיק שכר בסיס מסך הברוטו או אחוז הפרשה מסכום הניכוי.</p>
  {error&&<div className="error-banner" role="alert"><AlertCircle size={17}/>{error}</div>}
  {applied&&<div className="admin-success" role="status"><CheckCircle2 size={17}/> השדות הועברו לטופס. בדקו ושמרו באמצעות ״שמירה וחישוב מחדש״ כדי להפעיל אותם.</div>}
  {result&&<div className="payslip-review"><h3>משווים למקור, ואז מאשרים.</h3><p>מולכם התלוש המקורי והתוצאה. סמנו במפורש כל שדה שברצונכם לשמור. שדות שלא זוהו נשארו ריקים ולא יחליפו נתונים קיימים.</p><div className="payslip-fields">{Object.keys(labels).map(key=><div key={key}><label className="payslip-select"><input type="checkbox" aria-label={'אישור '+labels[key]} checked={selected[key]||false} onChange={event=>setSelected(s=>({...s,[key]:event.target.checked}))}/>{labels[key]}</label><input aria-label={'חילוץ '+labels[key]} type="number" min={0} max={maxima[key]} step="any" value={values[key]||''} placeholder="לא זוהה" onChange={event=>setValues(v=>({...v,[key]:event.target.value}))}/><small>{result.fields[key]?.value!=null?'ערך מוצע מחילוץ מקומי — בדקו מול התלוש.':'לא זוהה בוודאות. אפשר להזין ידנית ולסמן לאחר בדיקה בתלוש.'}</small></div>)}</div>{result.warnings.map((w:string,i:number)=><p className="payslip-note" key={i}>{w}</p>)}<button type="button" className="primary-button" onClick={apply}><CheckCircle2 size={16}/> אישור והעברת השדות המסומנים לטופס</button></div>}
  {camera&&<PayslipCamera onCapture={photo=>{void choose([photo])}} onClose={()=>setCamera(false)}/>}
 </section>
}
