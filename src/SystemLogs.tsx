import {useEffect,useRef,useState} from 'react';
import {Download,RefreshCw,Search,ScrollText} from 'lucide-react';
import {api,download} from './api';
import './system-logs.css';

type Entry={sequence:number;at:string;instanceId:string;level:string;category:string;eventId:number;message:string;exceptionType:string|null;traceId:string|null};
type Page={items:Entry[];categories:string[];hasMore:boolean;nextBefore:number|null;storedCount:number;maxEntries:number;retentionDays:number;storageState:string;storageError:string|null;lastPersistedAt:string|null;instanceId:string};
type Filters={level:string;category:string;q:string;from:string;to:string};
const initial:Filters={level:'Information',category:'',q:'',from:'',to:''};
const levels=[['','כל הרמות שנקלטו'],['Trace','Trace ומעלה'],['Debug','Debug ומעלה'],['Information','מידע ומעלה'],['Warning','אזהרות ומעלה'],['Error','שגיאות ומעלה'],['Critical','קריטי בלבד']];
const when=(value:string)=>new Intl.DateTimeFormat('he-IL',{dateStyle:'short',timeStyle:'medium',timeZone:'Asia/Jerusalem'}).format(new Date(value));
function params(filters:Filters,before?:number|null){const q=new URLSearchParams();for(const [key,value] of Object.entries(filters)){if(!value)continue;q.set(key,key==='from'||key==='to'?new Date(value).toISOString():value)}if(before)q.set('before',String(before));return q.toString()}
export default function SystemLogs(){
 const [draft,setDraft]=useState<Filters>(initial),[page,setPage]=useState<Page|null>(null),[items,setItems]=useState<Entry[]>([]),[busy,setBusy]=useState(false),[exporting,setExporting]=useState(false),[error,setError]=useState(''),[automatic,setAutomatic]=useState(false);
 const applied=useRef<Filters>(initial),cursor=useRef<number|null>(null),generation=useRef(0),loading=useRef(false),alive=useRef(true);
 async function load(older=false){
  const request=++generation.current;loading.current=true;setBusy(true);setError('');
  try{const result:Page=await api('/admin/logs?'+params(applied.current,older?cursor.current:null));if(!alive.current||request!==generation.current)return;setPage(result);cursor.current=result.nextBefore;setItems(previous=>older?[...previous,...result.items.filter(item=>!previous.some(old=>old.sequence===item.sequence))]:result.items)}
  catch(e){if(alive.current&&request===generation.current)setError((e as Error).message)}finally{if(alive.current&&request===generation.current){loading.current=false;setBusy(false)}}
 }
 useEffect(()=>{alive.current=true;void load();return()=>{alive.current=false;generation.current++}},[]);
 useEffect(()=>{if(!automatic)return;const timer=window.setInterval(()=>{if(!loading.current&&document.visibilityState==='visible')void load()},10000);return()=>window.clearInterval(timer)},[automatic]);
 async function exportLogs(){setExporting(true);setError('');try{const blob=await download('/admin/logs/export?'+params(applied.current));if(!alive.current)return;const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download='CarWise-logs-'+new Date().toISOString().slice(0,10)+'.jsonl';a.click();window.setTimeout(()=>URL.revokeObjectURL(url),1000)}catch(e){if(alive.current)setError((e as Error).message)}finally{if(alive.current)setExporting(false)}}
 const field=(name:keyof Filters,value:string)=>setDraft(previous=>({...previous,[name]:value}));
 return <section className="panel admin-section system-logs" aria-label="לוגי מערכת">
  <div className="system-logs-heading"><div><h2><ScrollText size={22}/> לוגי מערכת</h2><p>אירועי שרת, בקשות API ופעולות AI. תוכן שיחות, תלושים וסודות אינם חלק מהיומן.</p></div><div className="system-logs-actions"><button type="button" className="outline-button" disabled={busy} onClick={()=>void load()}><RefreshCw size={16}/> רענון לוגים</button><button type="button" className="outline-button" disabled={busy||exporting||!page} onClick={()=>void exportLogs()}><Download size={16}/>{exporting?'מייצאים…':'ייצוא לפי המסננים'}</button></div></div>
  <form className="system-log-filters" onSubmit={e=>{e.preventDefault();if(draft.from&&draft.to&&new Date(draft.from)>new Date(draft.to)){setError('תאריך ההתחלה צריך להיות לפני תאריך הסיום.');return}applied.current={...draft};cursor.current=null;setItems([]);void load()}}>
   <label>רמת לוג<select aria-label="רמת לוג" value={draft.level} onChange={e=>field('level',e.target.value)}>{levels.map(([value,label])=><option key={value} value={value}>{label}</option>)}</select></label>
   <label>רכיב<select aria-label="רכיב לוג" value={draft.category} onChange={e=>field('category',e.target.value)}><option value="">כל הרכיבים</option>{page?.categories.map(category=><option key={category} value={category}>{category}</option>)}</select></label>
   <label className="system-log-search">חיפוש<input aria-label="חיפוש בלוגים" maxLength={200} value={draft.q} onChange={e=>field('q',e.target.value)} placeholder="הודעה, קוד תקלה או מזהה בקשה"/></label>
   <label>מתאריך ושעה<input aria-label="לוגים מתאריך" type="datetime-local" value={draft.from} onChange={e=>field('from',e.target.value)}/></label>
   <label>עד תאריך ושעה<input aria-label="לוגים עד תאריך" type="datetime-local" value={draft.to} onChange={e=>field('to',e.target.value)}/></label>
   <button className="primary-button" disabled={busy}><Search size={16}/> סינון לוגים</button>
  </form>
  <div className="system-log-controls"><label className="check-row"><input aria-label="רענון לוגים אוטומטי" type="checkbox" checked={automatic} onChange={e=>setAutomatic(e.target.checked)}/> מעקב אחר הרשומות האחרונות — רענון כל 10 שניות</label><span role="status">{busy?'טוענים לוגים…':`${items.length} רשומות מוצגות`}</span></div>
  {error&&<p className="error-banner" role="alert">{error}</p>}
  {page?.storageState==='memory-only'&&<p className="error-banner" role="alert">השמירה לקובץ אינה זמינה כרגע; הלוגים החדשים זמינים בזיכרון השרת. קוד: {page.storageError}</p>}
  <div className="admin-table-wrap"><table className="admin-table system-log-table"><thead><tr><th>זמן</th><th>רמה</th><th>רכיב</th><th>הודעה</th><th>פרטים</th></tr></thead><tbody>{items.map(entry=><tr key={entry.sequence}><td><time dateTime={entry.at}>{when(entry.at)}</time></td><td><span className={'system-log-level '+entry.level.toLowerCase()}>{entry.level}</span></td><td dir="ltr">{entry.category}</td><td dir="auto">{entry.message}{entry.exceptionType&&<small dir="ltr">{entry.exceptionType}</small>}</td><td><details><summary>פרטי אירוע</summary><small>אירוע: {entry.eventId} · רשומה: {entry.sequence}</small>{entry.traceId&&<label>מזהה בקשה<input aria-label={'מזהה בקשה '+entry.sequence} readOnly dir="ltr" value={entry.traceId} onFocus={e=>e.target.select()}/></label>}<small dir="ltr">Instance: {entry.instanceId}</small></details></td></tr>)}</tbody></table></div>
  {!busy&&!items.length&&<p className="admin-footnote">לא נמצאו לוגים התואמים למסננים.</p>}
  {page?.hasMore&&<button type="button" className="outline-button" disabled={busy||automatic} onClick={()=>void load(true)}>טעינת לוגים קודמים</button>}
  {page&&<p className="admin-footnote">ביומן עד {page.maxEntries.toLocaleString('he-IL')} רשומות אחרונות, למשך עד {page.retentionDays} ימים. נשמרו בזיכרון: {page.storedCount.toLocaleString('he-IL')}. שמירה אחרונה לקובץ: {page.lastPersistedAt?when(page.lastPersistedAt):'טרם בוצעה'}. זמנים מוצגים לפי שעון ישראל; מסנני התאריכים לפי שעון המכשיר.</p>}
  <p className="admin-footnote">היומן מרכז את לוגי האפליקציה שנקלטו במופע שרת זה מאז הוספת התיעוד. השמירה לאחר הפעלה מחדש תלויה בדיסק הקבוע של השרת. לוגי תשתית היסטוריים של Railway ו־Supabase נשארים אצל הספקים. רמות שנחסמו בהגדרות השרת אינן נאספות.</p>
 </section>
}
