import {useEffect,useLayoutEffect,useRef,useState} from 'react';
import {Sparkles} from 'lucide-react';
import './chat-feedback.css';

export function ChatProgress({startedAt}:{startedAt:number}){
 const [seconds,setSeconds]=useState(0);
 useEffect(()=>{setSeconds(Math.floor((Date.now()-startedAt)/1000));const timer=window.setInterval(()=>setSeconds(Math.floor((Date.now()-startedAt)/1000)),1000);return()=>window.clearInterval(timer)},[startedAt]);
 const note=seconds<10?'ההודעה נשלחה, מכינים את התשובה שלך.':seconds<25?'עדיין ממתינים לתשובה. אפשר להמשיך להשתמש באתר.':'זה לוקח קצת יותר זמן. הבקשה עדיין בטיפול.';
 return <div className="chat-progress" role="status" aria-live="polite"><span className="chat-progress-icon"><Sparkles size={22}/></span><div><strong>היועץ מכין תשובה…</strong><p>{note}</p><div className="chat-progress-dots" aria-hidden="true"><span>•</span><span>•</span><span>•</span></div><small aria-hidden="true">{seconds>0?'חלפו '+seconds+' שניות':'רגע, אני איתך'}</small></div></div>
}
export function TypingReply({text,active,onDone}:{text:string;active:boolean;onDone:()=>void}){
 const [length,setLength]=useState(active?0:text.length),done=useRef(onDone),paragraph=useRef<HTMLParagraphElement>(null);
 const finishRef=useRef<()=>void>(()=>{});done.current=onDone;
 useEffect(()=>{
  let cancelled=false,completed=false,timer=0;
  function finish(){if(cancelled||completed)return;completed=true;window.clearTimeout(timer);setLength(text.length);done.current()}
  finishRef.current=finish;
  if(!active){setLength(text.length);return}
  if(window.matchMedia('(prefers-reduced-motion: reduce)').matches||document.hidden){finish();return}
  setLength(0);const started=performance.now(),duration=Math.min(6000,Math.max(650,Array.from(text).length*7));
  const characters=Array.from(text);
  function tick(){
   if(cancelled||completed)return;
   let count=Math.min(characters.length,Math.max(1,Math.floor((performance.now()-started)/duration*characters.length)));
   while(count<characters.length&&!/\s/.test(characters[count]))count++;
   setLength(characters.slice(0,count).join('').length);
   if(count===characters.length)finish();else timer=window.setTimeout(tick,35);
  }
  timer=window.setTimeout(tick,35);const hidden=()=>{if(document.hidden)finish()};document.addEventListener('visibilitychange',hidden);
  return()=>{cancelled=true;window.clearTimeout(timer);document.removeEventListener('visibilitychange',hidden)};
 },[text,active]);
 useLayoutEffect(()=>{if(!active)return;const pane=paragraph.current?.closest('.chat-messages');if(pane&&pane.scrollHeight-pane.scrollTop-pane.clientHeight<130)pane.scrollTop=pane.scrollHeight},[length,active]);
 const typing=active&&length<text.length;
 return <><p ref={paragraph} className="chat-reply-text">{text.slice(0,active?length:text.length)}{typing&&<span className="chat-reply-caret" aria-hidden="true"/>}</p>{typing&&<div className="chat-reply-controls"><small><Sparkles size={12}/> היועץ מקליד…</small><button type="button" className="text-button" onClick={()=>finishRef.current()}>הצג הכול</button></div>}</>
}
