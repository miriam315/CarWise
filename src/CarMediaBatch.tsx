import {useEffect,useRef,useState} from 'react';
import type {Car,CarPhoto} from './types';
import {prepareCarImage} from './carImageUpload';
import {matchCarImagePath} from './carImageBatch';
import {upload} from './api';
type Row={file:File;path:string;carId:string;view:CarPhoto['view']};
export default function CarMediaBatch({cars,onAdd,onBusyChange,disabled=false}:{cars:Car[];onBusyChange:(busy:boolean)=>void;disabled?:boolean;onAdd:(carId:string,photo:CarPhoto)=>void}){
 const [rows,setRows]=useState<Row[]>([]),[credit,setCredit]=useState(''),[rights,setRights]=useState(false),[busy,setBusy]=useState(false),[error,setError]=useState(''),[message,setMessage]=useState('');
 const live=useRef(true),controller=useRef<AbortController|null>(null);
 useEffect(()=>{live.current=true;return()=>{live.current=false;controller.current?.abort();onBusyChange(false)}},[]);
 function choose(files:File[]){
  if(busy||disabled)return;
  setError('');setMessage('');const images=files.filter(f=>/\.(jpe?g|png|webp)$/i.test(f.name));
  if(!images.length){setError('לא נמצאו קובצי JPG, PNG או WebP בבחירה.');return}
  if(images.length>60){setError('אפשר לייבא עד 60 תמונות בכל פעולה.');return}
  const next=images.map(file=>{const path=file.webkitRelativePath||file.name;return{file,path,...matchCarImagePath(path,cars)}}).sort((a,b)=>a.path.localeCompare(b.path,undefined,{numeric:true}));
  setRows(next);if(images.length!==files.length)setMessage(`דולגו ${files.length-images.length} קבצים שאינם תמונות נתמכות.`);
 }
 function update(index:number,patch:Partial<Row>){setRows(all=>all.map((row,i)=>i===index?{...row,...patch}:row))}
 const missing=rows.some(row=>!cars.some(c=>c.id===row.carId));
 async function start(){
  if(busy||disabled||!rows.length||missing||!rights||!credit.trim())return;
  setError('');setMessage('');
  for(const car of cars){const add=rows.filter(r=>r.carId===car.id);if((car.photos?.length||0)+add.length>60){setError(`${car.model}: עד 60 תמונות לרכב.`);return}
   if((car.photos?.filter(p=>p.view==='spin').length||0)+add.filter(r=>r.view==='spin').length>48){setError(`${car.model}: עד 48 צילומים בסדרת 360°.`);return}}
  setBusy(true);onBusyChange(true);controller.current=new AbortController();let completed=0;
  try{
   for(let i=0;i<rows.length;i++){
    const row=rows[i];controller.current.signal.throwIfAborted();setMessage(`מעלים תמונה ${i+1} מתוך ${rows.length} — ${row.carId}`);
    const file=await prepareCarImage(row.file);controller.current.signal.throwIfAborted();
    const result=await upload('/admin/car-images?rightsConfirmed=true',file,controller.current.signal);
    if(!live.current)return;onAdd(row.carId,{id:result.id,view:row.view,credit:credit.trim()});completed++;
   }
   if(live.current)setMessage(`${completed} תמונות עובדו וצורפו לטיוטה. לחצו על שמירת נתונים לפרסום.`);
  }catch(e){if(live.current){setError(controller.current.signal.aborted?'הייבוא נעצר. התמונות שכבר הושלמו צורפו לטיוטה.':(e as Error).message);setMessage(`${completed} מתוך ${rows.length} תמונות הושלמו. אפשר לשמור אותן ולנסות שוב את היתר.`)}}
  finally{if(live.current){setRows(all=>all.slice(completed));setBusy(false);onBusyChange(false)}}
 }
 return <section className="panel admin-section car-media-batch"><h2>ייבוא תמונות למספר רכבים</h2><p>בחרו תמונות או תיקייה שלמה, בדקו את השיוך בטבלה והעלו יחד. עד 60 תמונות בפעולה. אין צורך לפתוח כל רכב בנפרד.</p>
 <p>לשיוך אוטומטי השתמשו במזהה הרכב: <bdi>CAR-01__exterior__01.jpg</bdi>, או בתיקיות <bdi>CAR-01/interior/01.jpg</bdi>. לסיבוב השתמשו ב־<bdi>CAR-01/spin/01.jpg</bdi> עד <bdi>36.jpg</bdi>. אפשר לשנות כל שיוך לפני ההעלאה.</p>
 <div className="payslip-controls"><label className="car-media-upload">בחירת תמונות לייבוא מרוכז<input type="file" multiple accept="image/jpeg,image/png,image/webp" aria-label="תמונות לייבוא מרוכז" disabled={busy||disabled} onChange={e=>{choose(Array.from(e.target.files||[]));e.target.value=''}}/></label><label className="car-media-upload">בחירת תיקיית תמונות<input type="file" multiple {...{webkitdirectory:''}} aria-label="תיקיית תמונות לייבוא" disabled={busy||disabled} onChange={e=>{choose(Array.from(e.target.files||[]));e.target.value=''}}/></label></div>
 {rows.length>0&&<><div className="admin-table-wrap"><table className="admin-table"><thead><tr><th>קובץ</th><th>רכב</th><th>סוג תמונה</th><th/></tr></thead><tbody>{rows.map((row,i)=><tr key={row.path+'-'+i}><td dir="auto">{row.path}</td><td><select aria-label={'שיוך רכב לתמונה '+(i+1)} disabled={busy||disabled} value={row.carId} onChange={e=>update(i,{carId:e.target.value})}><option value="">בחרו רכב</option>{cars.map(car=><option key={car.id} value={car.id}>{car.id} · {car.model} · {car.year}</option>)}</select></td><td><select aria-label={'סוג תמונה בייבוא '+(i+1)} disabled={busy||disabled} value={row.view} onChange={e=>update(i,{view:e.target.value as CarPhoto['view']})}><option value="exterior">חוץ</option><option value="interior">פנים</option><option value="spin">360°</option></select></td><td><button type="button" className="text-button" disabled={busy||disabled} aria-label={'הסרת תמונה מהייבוא '+(i+1)} onClick={()=>setRows(all=>all.filter((_,j)=>i!==j))}>הסרה</button></td></tr>)}</tbody></table></div>
 <label>מקור וקרדיט לתמונות שבבחירה<input aria-label="קרדיט לייבוא תמונות מרוכז" value={credit} maxLength={1000} disabled={busy||disabled} onChange={e=>setCredit(e.target.value)}/></label><label className="check-row"><input type="checkbox" aria-label="הרשאת שימוש בייבוא מרוכז" checked={rights} disabled={busy||disabled} onChange={e=>setRights(e.target.checked)}/> יש לארגון הרשאה לפרסם את כל התמונות בבחירה.</label>
 <p>הקרדיט יחול על כל הבחירה. למקורות שונים בצעו ייבואים נפרדים. 360° מחייב 12–48 צילומים שונים מאותו סיבוב מלא; המערכת אינה יוצרת זוויות חדשות מתמונה אחת. הסדר נקבע לפי שמות הקבצים.</p>
 {missing&&<p role="status">יש לשייך את כל התמונות לרכב לפני הייבוא.</p>}<button type="button" className="primary-button" disabled={busy||disabled||missing||!rights||!credit.trim()} onClick={()=>void start()}>העלאת התמונות המשויכות</button></>}
 {busy&&<button type="button" className="outline-button" onClick={()=>controller.current?.abort()}>עצירת הייבוא</button>}{message&&<p role="status">{message}</p>}{error&&<p className="error-banner" role="alert">{error}</p>}
 </section>;
}
