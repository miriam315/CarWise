import {useEffect,useRef,useState} from 'react';
import {createPortal} from 'react-dom';
import {Expand,X,ChevronLeft,ChevronRight,RotateCcw} from 'lucide-react';
import CarArt from './CarArt';
import type {Car,CarPhoto} from './types';
import './car-gallery.css';

export const carPhotoUrl=(photo:CarPhoto)=>'/media/cars/'+photo.id+'.jpg';
const labels={exterior:'חוץ הרכב',interior:'פנים הרכב',spin:'360°'};
export default function CarGallery({car,color,management=false}:{car:Car;color?:string;management?:boolean}){
 const photos=car.photos||[];
 const cover=photos.find(p=>p.view==='exterior')||photos[0];
 const [open,setOpen]=useState(false),[failed,setFailed]=useState('');
 return <div className="car-gallery-cover">{cover&&failed!==cover.id?<img className="car-art car-photo" src={carPhotoUrl(cover)} alt={`${car.brand} ${car.model} — תמונה להמחשה`} loading="lazy" width={1200} height={800} onError={()=>setFailed(cover.id)}/>:<CarArt color={color}/>}{photos.length>0&&<><span className="car-photo-caption">תמונה להמחשה</span><button type="button" className="car-gallery-open" aria-label={`הגדלת תמונות ${car.model}`} onClick={()=>setOpen(true)}><Expand size={17}/> תמונות הרכב</button></>}{open&&photos.length>0&&createPortal(<GalleryViewer key={car.id+'-'+photos.map(p=>p.id+p.view).join('-')} car={car} management={management} onClose={()=>setOpen(false)}/>,document.body)}</div>;
}

function GalleryViewer({car,onClose,management}:{car:Car;onClose:()=>void;management:boolean}){
 const photos=car.photos||[];
 const spinPhotos=photos.filter(p=>p.view==='spin');
 const spinReady=new Set(spinPhotos.map(p=>p.id)).size>=12;
 const exterior=photos.filter(p=>p.view==='exterior');
 const groups={exterior:exterior.length?exterior:!spinReady?spinPhotos:[],interior:photos.filter(p=>p.view==='interior'),spin:spinReady?spinPhotos:[]};
 const [view,setView]=useState<CarPhoto['view']>(groups.exterior.length?'exterior':groups.interior.length?'interior':'spin');
 const [index,setIndex]=useState(0),[zoom,setZoom]=useState(100),[error,setError]=useState(false);
 const filtered=groups[view],photo=filtered[index];
 const dialog=useRef<HTMLElement>(null),drag=useRef<{x:number;index:number}|null>(null);
 function select(next:number){setIndex((next+filtered.length)%filtered.length);setError(false)}
 useEffect(()=>{const previous=document.activeElement as HTMLElement|null;const overflow=document.body.style.overflow;document.body.style.overflow='hidden';dialog.current?.focus();return()=>{document.body.style.overflow=overflow;previous?.focus()}},[]);
 useEffect(()=>{if(view!=='spin')return;const images=[-1,1,2].map(offset=>{const image=new Image();image.src=carPhotoUrl(filtered[(index+offset+filtered.length)%filtered.length]);return image});return()=>{images.forEach(i=>{i.src=''})}},[view,car.id,index]);
 return <div className="car-gallery-backdrop" onClick={e=>{e.stopPropagation();if(e.target===e.currentTarget)onClose()}}><section className="car-gallery-viewer" role="dialog" aria-modal="true" aria-label={`תמונות ${car.model}`} tabIndex={-1} ref={dialog} onKeyDown={e=>{
  e.stopPropagation();if(e.key==='Escape'){e.preventDefault();onClose()}
  if(e.target===dialog.current||e.target instanceof HTMLImageElement){if(e.key==='ArrowRight')select(index+1);if(e.key==='ArrowLeft')select(index-1)}
  if(e.key==='Tab'){const controls=Array.from(dialog.current!.querySelectorAll<HTMLElement>('button:not(:disabled),input:not(:disabled),[tabindex="0"]'));const first=controls[0],last=controls.at(-1);if(e.shiftKey&&(document.activeElement===first||document.activeElement===dialog.current)){e.preventDefault();last?.focus()}else if(!e.shiftKey&&document.activeElement===last){e.preventDefault();first?.focus()}}
 }}><header><div><h2>{car.model}</h2><p>{car.brand} · {car.year} · תמונות להמחשה</p></div><button type="button" className="icon-button" aria-label="סגירת גלריית הרכב" onClick={onClose}><X/></button></header>
 <div className="car-gallery-tabs" role="tablist" aria-label="סוג תמונות הרכב">{(['exterior','interior','spin'] as const).filter(kind=>management||groups[kind].length>0).map(kind=><button type="button" key={kind} role="tab" aria-selected={kind===view} disabled={!groups[kind].length} onClick={()=>{setView(kind);setIndex(0);setZoom(100);setError(false)}}>{labels[kind]}{kind==='spin'&&<RotateCcw size={17}/>}</button>)}</div>
 {management&&!spinReady&&<p className="car-gallery-hint">תצוגת 360° זמינה רק לאחר הוספת לפחות 12 צילומים מסודרים סביב אותו רכב.</p>}
 <div className={'car-gallery-stage '+(view==='spin'?'spin-stage':'')} onPointerDown={e=>{if(view==='spin'){e.preventDefault();e.currentTarget.setPointerCapture(e.pointerId);drag.current={x:e.clientX,index}}}} onPointerMove={e=>{if(drag.current&&view==='spin')select(drag.current.index+Math.round((e.clientX-drag.current.x)/9))}} onPointerUp={()=>{drag.current=null}} onPointerCancel={()=>{drag.current=null}}>
 {photo&&!error?<img src={carPhotoUrl(photo)} alt={`${car.model} — ${labels[view]}, תמונה ${index+1}`} draggable={false} style={{width:`${zoom}%`,height:`${zoom}%`,maxWidth:'none'}} onError={()=>setError(true)}/>:<p role="status">התמונה אינה זמינה. אפשר לבחור תמונה אחרת.</p>}
 </div><div className="car-gallery-controls"><button type="button" className="outline-button" disabled={filtered.length<2} aria-label="תמונת הרכב הקודמת" onClick={()=>select(index-1)}><ChevronRight/></button><span aria-live="polite">{index+1} / {filtered.length}{view==='spin'&&` · ${Math.round(index*360/filtered.length)}°`}</span><button type="button" className="outline-button" disabled={filtered.length<2} aria-label="תמונת הרכב הבאה" onClick={()=>select(index+1)}><ChevronLeft/></button><label>הגדלה {zoom}%<input type="range" aria-label="הגדלת תמונת הרכב" min={100} max={250} step={10} value={zoom} onChange={e=>setZoom(Number(e.target.value))}/></label></div>
 {view==='spin'?<><p>גררו על התמונה או השתמשו בחצים לסיבוב סביב הרכב.</p><input className="car-spin-range" type="range" aria-label="זווית צפייה ברכב" min={0} max={filtered.length-1} value={index} onChange={e=>select(Number(e.target.value))}/></>:<div className="car-gallery-thumbs">{filtered.map((p,i)=><button type="button" key={p.id+'-'+i} aria-label={`תמונה ${i+1} של ${car.model}`} aria-pressed={index===i} onClick={()=>select(i)}><img src={carPhotoUrl(p)} alt="" loading="lazy"/></button>)}</div>}
 {photo?.credit&&<p className="car-photo-credit">מקור וקרדיט: {photo.credit}</p>}<p className="car-gallery-hint">המראה, האבזור ורמת הגימור עשויים להשתנות לפי שנתון. תמונות אינן מפרט מחייב.</p></section></div>;
}
