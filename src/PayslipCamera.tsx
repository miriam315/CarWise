import {useEffect,useRef,useState} from 'react';
import {Camera,RotateCcw,X,Check,AlertCircle} from 'lucide-react';
export default function PayslipCamera({onCapture,onClose}:{onCapture:(file:File)=>void;onClose:()=>void}){
 const video=useRef<HTMLVideoElement>(null),stream=useRef<MediaStream|null>(null),close=useRef<HTMLButtonElement>(null);
 const [ready,setReady]=useState(false),[error,setError]=useState(''),[photo,setPhoto]=useState<Blob|null>(null),[preview,setPreview]=useState('');
 function stop(){stream.current?.getTracks().forEach(track=>track.stop());stream.current=null}
 useEffect(()=>{
  let active=true;const previous=document.activeElement as HTMLElement|null,overflow=document.body.style.overflow;document.body.style.overflow='hidden';close.current?.focus();
  async function start(){
   if(!window.isSecureContext||!navigator.mediaDevices?.getUserMedia){setError('צילום באתר דורש HTTPS או localhost ודפדפן שתומך במצלמה. אפשר לבחור תמונה שכבר צילמתם.');return}
   try{const camera=await navigator.mediaDevices.getUserMedia({audio:false,video:{facingMode:{ideal:'environment'},width:{ideal:2000},height:{ideal:2600}}});
    if(!active){camera.getTracks().forEach(track=>track.stop());return}stream.current=camera;
    if(video.current){video.current.srcObject=camera;await video.current.play();}
   }catch(e){if(active){stop();setError(e instanceof DOMException&&e.name==='NotAllowedError'?'לא ניתנה הרשאה למצלמה. אפשר לאשר בדפדפן או לבחור תמונה מהמכשיר.':'לא ניתן להפעיל את המצלמה. בדקו שאינה בשימוש ביישום אחר, או בחרו תמונה מהמכשיר.')}}
  }
  void start();const hidden=()=>{if(document.hidden){stop();onClose()}};
  const key=(event:KeyboardEvent)=>{if(event.key==='Escape')onClose();if(event.key==='Tab'){const nodes=Array.from(document.querySelectorAll<HTMLElement>('.payslip-camera button:not(:disabled),.payslip-camera input'));const first=nodes[0],last=nodes.at(-1);if(event.shiftKey&&document.activeElement===first){event.preventDefault();last?.focus()}else if(!event.shiftKey&&document.activeElement===last){event.preventDefault();first?.focus()}}};
  document.addEventListener('visibilitychange',hidden);document.addEventListener('keydown',key);
  return()=>{active=false;stop();document.body.style.overflow=overflow;document.removeEventListener('visibilitychange',hidden);document.removeEventListener('keydown',key);previous?.focus()};
 },[]);
 useEffect(()=>{if(!photo){setPreview('');return}const url=URL.createObjectURL(photo);setPreview(url);return()=>URL.revokeObjectURL(url)},[photo]);
 async function capture(){const element=video.current;if(!element||!ready)return;
  const scale=Math.min(1,2600/Math.max(element.videoWidth,element.videoHeight));const canvas=document.createElement('canvas');
  canvas.width=Math.round(element.videoWidth*scale);canvas.height=Math.round(element.videoHeight*scale);const context=canvas.getContext('2d');if(!context)return;
  context.drawImage(element,0,0,canvas.width,canvas.height);const blob=await new Promise<Blob|null>(resolve=>canvas.toBlob(resolve,'image/jpeg',.92));
  if(blob)setPhoto(blob);else setError('לא ניתן לשמור את הצילום. נסו שוב או בחרו תמונה מהמכשיר.');canvas.width=canvas.height=0;
 }
 return <div className="modal-backdrop"><section className="panel payslip-camera" role="dialog" aria-modal="true" aria-labelledby="payslip-camera-title"><div className="payslip-camera-heading"><h2 id="payslip-camera-title">צילום תלוש שכר</h2><button ref={close} className="icon-button" aria-label="סגירת המצלמה" onClick={onClose}><X/></button></div><p>הניחו את התלוש על משטח מואר. ודאו שהשורות והסכומים חדים ושכל העמוד נמצא במסגרת.</p>{error?<div className="error-banner" role="alert"><AlertCircle/>{error}</div>:<><video ref={video} playsInline muted aria-label="תצוגת מצלמה" onLoadedData={()=>setReady(true)} hidden={!!photo}/>{preview&&<img src={preview} alt="תצוגה מקדימה של התלוש שצולם"/>}<div className="payslip-camera-actions">{photo?<><button className="outline-button" onClick={()=>setPhoto(null)}><RotateCcw size={18}/> צילום מחדש</button><button className="primary-button" onClick={()=>{onCapture(new File([photo],'payslip-camera.jpg',{type:'image/jpeg'}));stop();onClose()}}><Check size={18}/> שימוש בצילום</button></>:<button className="primary-button" disabled={!ready} onClick={capture}><Camera size={18}/> צילום</button>}</div></>}<p className="payslip-note">אין הקלטה ואין שליחה אוטומטית. התמונה תעובד רק לאחר אישור ולחיצה על חילוץ נתונים. המצלמה נסגרת ביציאה מהחלון.</p><label className="outline-button file-button">צילום דרך המכשיר / בחירת תמונה<input aria-label="צילום תלוש דרך המכשיר" type="file" accept="image/jpeg,image/png" capture="environment" onChange={event=>{const file=event.target.files?.[0];if(file){onCapture(file);stop();onClose()}}}/></label></section></div>;
}
