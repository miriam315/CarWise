import {useEffect,useRef,useState} from 'react';
import {ArrowRight,RefreshCw} from 'lucide-react';
import {api} from './api';
import './chat-history.css';
type Entry={index:number;role:string;text:string;at:string|null};
type Page={items:Entry[];nextBefore:number|null;total:number};
export default function ChatHistory({employeeId,onClose}:{employeeId:string;onClose:()=>void}){
 const [page,setPage]=useState<Page|null>(null),[items,setItems]=useState<Entry[]>([]),[busy,setBusy]=useState(false),[error,setError]=useState('');
 const alive=useRef(true),inFlight=useRef(false),generation=useRef(0),viewport=useRef<HTMLDivElement>(null);
 async function load(before?:number){
  if(inFlight.current)return;inFlight.current=true;const request=++generation.current;setBusy(true);setError('');
  const previousHeight=viewport.current?.scrollHeight??0,previousTop=viewport.current?.scrollTop??0;
  try{const result:Page=await api('/history/'+encodeURIComponent(employeeId)+'/archive?limit=50'+(before==null?'':'&before='+before));
   if(!alive.current||request!==generation.current)return;
   setPage(result);setItems(previous=>before==null?result.items:[...result.items,...previous]);
   requestAnimationFrame(()=>{if(!alive.current||!viewport.current)return;viewport.current.scrollTop=before==null?viewport.current.scrollHeight:previousTop+viewport.current.scrollHeight-previousHeight});
  }catch(e){if(alive.current&&request===generation.current)setError((e as Error).message)}finally{if(request===generation.current){inFlight.current=false;if(alive.current)setBusy(false)}}
 }
 useEffect(()=>{alive.current=true;void load();return()=>{alive.current=false;generation.current++;inFlight.current=false}},[employeeId]);
 return <section className="chat-history" aria-label="היסטוריית שיחות"><div className="chat-history-heading"><h4>היסטוריית שיחות</h4><button type="button" className="text-button" onClick={onClose}><ArrowRight size={16}/> חזרה לשיחה הנוכחית</button></div><p className="chat-history-note">הודעות משיחות קודמות, לצפייה בלבד. פתיחת שיחה חדשה שומרת את ההודעות הקודמות כאן.</p>
  <div className="chat-history-messages" ref={viewport} aria-live="polite">{page?.nextBefore!=null&&<button type="button" className="outline-button" disabled={busy} onClick={()=>void load(page.nextBefore!)}>הודעות קודמות</button>}
   {items.map(item=><article className={'message '+item.role} key={item.index}><div><small>{item.role==='user'?'ההודעה שלך':'היועץ'}{item.at&&<> · <time dateTime={item.at}>{new Date(item.at).toLocaleString('he-IL',{timeZone:'Asia/Jerusalem'})}</time></>}</small><p>{item.text}</p></div></article>)}
   {busy&&<p role="status">טוענים את ההיסטוריה…</p>}{!busy&&!error&&items.length===0&&<p>אין עדיין שיחות קודמות. הודעות השיחה הנוכחית זמינות בחזרה לשיחה.</p>}
   {error&&<div className="chat-error" role="alert">{error}<button type="button" className="text-button" disabled={busy} onClick={()=>void load(page?.nextBefore??undefined)}><RefreshCw size={16}/> ניסיון נוסף</button></div>}
  </div>
 </section>
}
