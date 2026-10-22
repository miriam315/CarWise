import {useEffect,useRef,useState} from 'react';
import {upload} from './api';
import type {Car,CarPhoto} from './types';
import CarGallery,{carPhotoUrl} from './CarGallery';
import CommonsCarPicker from './CommonsCarPicker';

async function prepare(file:File):Promise<File>{
 if(!['image/jpeg','image/png','image/webp'].includes(file.type)||file.size>12*1024*1024)throw Error('אפשר להעלות JPG, PNG או WebP, עד 12MB לתמונה.');
 const bitmap=await createImageBitmap(file);
 try{
  if(bitmap.width*bitmap.height>40_000_000)throw Error('התמונה גדולה מדי לפענוח. השתמשו בתמונה עד 40 מיליון פיקסלים.');
  const ratio=Math.min(1,1600/Math.max(bitmap.width,bitmap.height),Math.sqrt(1_920_000/(bitmap.width*bitmap.height)));
  const canvas=document.createElement('canvas');canvas.width=Math.max(1,Math.floor(bitmap.width*ratio));canvas.height=Math.max(1,Math.floor(bitmap.height*ratio));
  const context=canvas.getContext('2d');if(!context)throw Error('לא ניתן להכין את התמונה בדפדפן זה.');
  context.fillStyle='#fff';context.fillRect(0,0,canvas.width,canvas.height);context.drawImage(bitmap,0,0,canvas.width,canvas.height);
  let blob:Blob|null=null;
  for(const quality of [.85,.7,.55,.4]){blob=await new Promise(resolve=>canvas.toBlob(resolve,'image/jpeg',quality));if(blob&&blob.size<=512*1024)break}
  if(!blob||blob.size>512*1024)throw Error('לא ניתן לצמצם את התמונה ל־512KB. נסו תמונה קטנה יותר.');
  return new File([blob],'vehicle.jpg',{type:'image/jpeg'});
 }finally{bitmap.close()}
}

export default function CarMediaEditor({car,onChange}:{car:Car;onChange:(photos:CarPhoto[])=>void}){
 const photos=car.photos||[];
 const [view,setView]=useState<CarPhoto['view']>('exterior'),[credit,setCredit]=useState(''),[rights,setRights]=useState(false),[busy,setBusy]=useState(false),[status,setStatus]=useState(''),[error,setError]=useState('');
 const controller=useRef<AbortController|null>(null),live=useRef(true);
 useEffect(()=>{live.current=true;return()=>{live.current=false;controller.current?.abort()}},[]);
 async function add(files:File[],sourceCredit=credit){
  if(!files.length)return;setError('');setStatus('');
  if(!rights||!sourceCredit.trim()){setError('יש להזין מקור וקרדיט ולאשר הרשאת שימוש לפני העלאה.');return}
  if(files.length+photos.length>60){setError('אפשר לשמור עד 60 תמונות לרכב.');return}
  if(view==='spin'&&photos.filter(p=>p.view==='spin').length+files.length>48){setError('אפשר לשמור עד 48 צילומים בסדרת 360°.');return}
  setBusy(true);controller.current=new AbortController();const added:CarPhoto[]=[];
  try{
   const ordered=view==='spin'?[...files].sort((a,b)=>a.name.localeCompare(b.name,undefined,{numeric:true})):files;
   for(let i=0;i<ordered.length;i++){
    setStatus(`מעלים תמונה ${i+1} מתוך ${ordered.length}…`);
    const image=await prepare(ordered[i]);if(!live.current)return;
    const result=await upload('/admin/car-images?rightsConfirmed=true',image,controller.current.signal);
    if(![...photos,...added].some(p=>p.id===result.id&&p.view===view))added.push({id:result.id,view,credit:sourceCredit.trim()});
   }
   if(live.current)setStatus('התמונות צורפו לטיוטה. שמרו את הגדרות המערכת לפרסום.');
  }catch(e){if(live.current)setError((e as Error).message)}
  finally{if(live.current){if(added.length)onChange([...photos,...added]);setBusy(false)}}
 }
 function move(index:number,direction:number){const updated=[...photos],target=index+direction;if(target<0||target>=updated.length)return;[updated[index],updated[target]]=[updated[target],updated[index]];onChange(updated)}
 return <section className="car-media-editor"><h3>תמונות חוץ, פנים ו־360°</h3><p>התמונות נשמרות בשרת. התמונה הראשונה מסוג חוץ תשמש כתמונת הקטלוג.</p><CarGallery car={car}/><div className="admin-form-grid"><label>סוג התמונות<select aria-label="סוג תמונות להעלאה" value={view} disabled={busy} onChange={e=>setView(e.target.value as CarPhoto['view'])}><option value="exterior">חוץ הרכב</option><option value="interior">פנים הרכב</option><option value="spin">סדרת 360° חיצונית</option></select></label><label>מקור וקרדיט<input aria-label="מקור וקרדיט לתמונות" value={credit} maxLength={1000} disabled={busy} placeholder="הצלם, המקור ורישיון השימוש" onChange={e=>setCredit(e.target.value)}/></label></div><label className="check-row"><input type="checkbox" checked={rights} disabled={busy} onChange={e=>setRights(e.target.checked)}/> אני מאשר/ת שיש לארגון הרשאה לפרסם את התמונות.</label><p className="car-gallery-hint">360°: העלו 12–48 צילומים עוקבים מאותו סיבוב מלא, למשל 01.jpg עד 36.jpg. הסדר נקבע לפי שמות הקבצים וניתן לשינוי. זו תצוגת סיבוב חיצונית, לא סיור פנורמי בתוך הרכב. לפנים הרכב העלו תמונות נפרדות.</p><label className="car-media-upload">{busy?'מעלים תמונות…':'בחירת תמונות לרכב'}<input aria-label="העלאת תמונות לרכב" type="file" accept="image/jpeg,image/png,image/webp" multiple disabled={busy||!rights||!credit.trim()} onChange={e=>{const files=Array.from(e.target.files||[]);e.target.value='';void add(files)}}/></label>{status&&<p role="status">{status}</p>}{error&&<p className="error-banner" role="alert">{error}</p>}{view!=='spin'&&<CommonsCarPicker key={view} model={car.model} view={view} disabled={busy} rights={rights} onPick={async(file,sourceCredit)=>{setCredit(sourceCredit);await add([file],sourceCredit)}}/>}<div className="car-media-items">{photos.map((photo,i)=><div key={photo.id+'-'+i}><img src={carPhotoUrl(photo)} alt={`תמונת ניהול ${i+1}`}/><span>{i+1} · {photo.view==='spin'?'360°':photo.view==='interior'?'פנים':'חוץ'}</span><button type="button" disabled={busy||i===0} aria-label={`הקדמת תמונה ${i+1}`} onClick={()=>move(i,-1)}>↑</button><button type="button" disabled={busy||i===photos.length-1} aria-label={`הזזת תמונה ${i+1} לאחור`} onClick={()=>move(i,1)}>↓</button><button type="button" disabled={busy} aria-label={`הסרת תמונת רכב ${i+1}`} onClick={()=>onChange(photos.filter((_,n)=>n!==i))}>הסרה</button></div>)}</div></section>;
}
