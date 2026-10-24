import {useEffect,useState} from 'react';
import {Users,RefreshCw} from 'lucide-react';
import {api} from './api';

type Entry={id:string;email:string;role:string;published:boolean;passwordSaved:boolean};
type Directory={enabled:boolean;available:boolean;items:Entry[]};
export default function ReviewAccountsManagement({refresh,onModeChange}:{refresh:number;onModeChange:(enabled:boolean)=>void}){
 const [directory,setDirectory]=useState<Directory|null>(null),[error,setError]=useState(''),[notice,setNotice]=useState(''),[busy,setBusy]=useState(false);
 const [editing,setEditing]=useState(''),[password,setPassword]=useState('');
 async function load(){setError('');try{const result=await api('/admin/review-accounts');setDirectory(result);onModeChange(result.enabled)}catch(e){setError((e as Error).message)}}
 useEffect(()=>{void load()},[refresh]);
 async function save(path:string,body:unknown,optimistic?:Directory){const previous=directory;if(optimistic)setDirectory(optimistic);setBusy(true);setError('');setNotice('');try{const result=await api(path,body);setDirectory(result);onModeChange(result.enabled);setEditing('');setPassword('');setNotice('רשימת חשבונות הבדיקה עודכנה.')}catch(e){setDirectory(previous);setError((e as Error).message)}finally{setBusy(false)}}
 return <section className="panel admin-section" aria-label="ניהול חשבונות לבדיקה">
  <div className="admin-section-title"><span className="stat-icon lavender"><Users size={22}/></span><div><h2>חשבונות לבדיקה במסך ההתחברות</h2><p>הרשימה מתעדכנת מהחשבונות ב־Supabase. חשבונות חדשים מצטרפים אוטומטית.</p></div></div>
  {error&&<p className="error-banner" role="alert">{error}</p>}{notice&&<p className="admin-success" role="status">{notice}</p>}
  <button type="button" className="outline-button" disabled={busy} onClick={()=>void load()}><RefreshCw size={15}/> רענון ואיתור חשבונות חסרים</button>
  {directory&&<>
   <label className="check-row"><input type="checkbox" aria-label="פרסום חשבונות לבדיקה במסך ההתחברות" checked={directory.enabled} disabled={busy||!directory.available} onChange={e=>void save('/admin/review-accounts',{enabled:e.target.checked},{...directory,enabled:e.target.checked})}/> הצגת חשבונות לבדיקה במסך ההתחברות</label>
   <div className="form-info">מיועד להגשת הפרויקט עם נתוני בדיקה בלבד: פרטי הכניסה של חשבונות שפורסמו זמינים לכל מבקר, כולל הרשאת מנהל. כשהאפשרות פעילה, סיסמאות שנקבעות כאן לחשבונות חדשים או באיפוס מצטרפות לרשימה. לפני שימוש ארגוני יש לכבות את הפרסום. אפשר להסתיר גם חשבון מסוים.</div>
   {!directory.available&&<p>הפרסום הושבת בהגדרות השרת.</p>}
   <div className="admin-table-wrap"><table className="admin-table"><thead><tr><th>דוא״ל</th><th>הרשאה</th><th>סיסמה לבדיקה</th><th>פרסום</th><th>עדכון</th></tr></thead><tbody>{directory.items.map(a=><tr key={a.id}>
    <td dir="ltr">{a.email}</td><td>{{admin:'מנהל',agent:'נציג',employee:'עובד'}[a.role]}</td><td>{a.passwordSaved?'שמורה':'נדרשת השלמה'}</td>
    <td><input type="checkbox" aria-label={'פרסום חשבון '+a.email} checked={a.published} disabled={busy} onChange={e=>void save('/admin/review-accounts/'+encodeURIComponent(a.id),{published:e.target.checked},{...directory,items:directory.items.map(item=>item.id===a.id?{...item,published:e.target.checked}:item)})}/></td>
    <td><button type="button" className="outline-button" onClick={()=>{setEditing(a.id);setPassword('');setError('');setNotice('')}}>השלמת סיסמה ידועה</button></td>
   </tr>)}</tbody></table></div>
   {!directory.items.length&&<p>לא אותרו חשבונות לבדיקה.</p>}
   {editing&&<form className="admin-form-grid" onSubmit={e=>{e.preventDefault();const entry=directory.items.find(a=>a.id===editing);if(entry)void save('/admin/review-accounts/'+encodeURIComponent(editing),{published:entry.published,password})}}>
    <label>סיסמה קיימת עבור {directory.items.find(a=>a.id===editing)?.email}<input aria-label="סיסמה קיימת לפרסום בבדיקה" type="password" autoComplete="new-password" minLength={12} maxLength={128} required value={password} onChange={e=>setPassword(e.target.value)}/><small>אין אפשרות לשלוף סיסמה קיימת מ־Supabase. הסיסמה שתוזן תאומת לפני השמירה; היא לא תוחלף.</small></label>
    <div><button className="primary-button" disabled={busy||password.length<12}>אימות ושמירה לרשימת הבדיקה</button><button type="button" className="text-button" disabled={busy} onClick={()=>{setEditing('');setPassword('')}}>ביטול</button></div>
   </form>}
  </>}
 </section>;
}
