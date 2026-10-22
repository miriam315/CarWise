import {useEffect,useState} from 'react';
import {ShieldCheck} from 'lucide-react';
import {api} from './api';
import './intent.css';
// Only deliberate interactions call this; dashboard refreshes never count as interest.
export async function recordIntent(employeeId:string,target:string){try{await api('/intent/explore',{employeeId,target})}catch{/* Optional research never interrupts payroll. */}}
type Preference={enabled:boolean;consented:boolean;canConsent:boolean;retentionDays:number;minimumCohort:number;explanation:string;reasons:Record<string,string>};
export function IntentPrivacy({employeeId}:{employeeId:string}){
 const [p,setP]=useState<Preference|null>(null),[error,setError]=useState(''),[busy,setBusy]=useState(false);
 useEffect(()=>{let live=true;api('/intent/preference/'+encodeURIComponent(employeeId)).then(v=>{if(live)setP(v)}).catch(()=>{});return()=>{live=false}},[employeeId]);
 async function choose(consent:boolean){setBusy(true);setError('');try{await api('/intent/preference',{employeeId,consent});setP(await api('/intent/preference/'+encodeURIComponent(employeeId)));window.dispatchEvent(new Event('carwise-intent-preference'))}catch(e){setError((e as Error).message)}finally{setBusy(false)}}
 if(!p||!p.enabled&&!p.consented)return null;
 return <section className="panel intent-privacy"><h2><ShieldCheck size={23}/> שיתוף מצומצם לשיפור מדיניות הרכב</h2><p>{p.explanation}</p><p>הדיווח להנהלה מצרפי, בסף של {p.minimumCohort} עובדים לפחות. האירועים נשמרים עד {p.retentionDays} ימים, עם ניקוי תקופתי. הסכמה אינה תנאי לקבלת ייעוץ.</p>{error&&<p role="alert">{error}</p>}{!p.canConsent?<p>רק העובד עצמו רשאי להסכים לשיתוף. צפיית מנהל בפרופיל אינה מתועדת כהתעניינות של העובד.</p>:p.consented?<><p role="status">הסכמה לשיתוף פעילה.</p><button className="outline-button" disabled={busy} onClick={()=>void choose(false)}>ביטול השיתוף ומחיקת היסטוריית המחקר שלי</button></>:<button className="outline-button" disabled={busy||!p.enabled} onClick={()=>void choose(true)}>אני מסכים/ה לשיתוף המצומצם שתואר</button>}</section>;
}
export function IntentFeedback({employeeId,target}:{employeeId:string;target:string}){
 const [p,setP]=useState<Preference|null>(null),[notice,setNotice]=useState(''),[busy,setBusy]=useState(false);
 useEffect(()=>{let live=true;const load=()=>api('/intent/preference/'+encodeURIComponent(employeeId)).then(v=>{if(live)setP(v)}).catch(()=>{});void load();window.addEventListener('carwise-intent-preference',load);return()=>{live=false;window.removeEventListener('carwise-intent-preference',load)}},[employeeId]);
 async function feedback(reason:string){setBusy(true);try{await api('/intent/feedback',{employeeId,target,reason});setNotice('המשוב נשמר למחקר המצרפי. הוא אינו משנה את הבחירה שלך.')}catch(e){setNotice((e as Error).message)}finally{setBusy(false)}}
 if(!p?.enabled||!p.canConsent)return null;
 return <section className="intent-feedback"><h3>מה עוד משפיע על ההחלטה שלך?</h3>{!p.consented?<p>אפשר לשתף שיקול באופן אופציונלי, לאחר הסכמה לשיתוף המצומצם ב״הנתונים שלי״.</p>:<><p>בחירה מרצון בלבד. לא מסיקים שזו הסיבה לאי־בחירה בלי שתציינו אותה.</p><div className="intent-reasons">{Object.entries(p.reasons).map(([key,label])=><button className="outline-button" key={key} disabled={busy} onClick={()=>void feedback(key)}>{label}</button>)}</div></>}{notice&&<p role="status">{notice}</p>}</section>;
}
