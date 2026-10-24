import {useEffect,useId,useState} from 'react';
import {ChevronDown,Users} from 'lucide-react';
import {api} from './api';

type Account={id:string;email:string;role:string;password:string|null};
const roles:Record<string,string>={admin:'מנהל',agent:'נציג',employee:'עובד'};
export default function ReviewAccounts({onSelect}:{onSelect:(email:string,password:string)=>void}){
 const [open,setOpen]=useState(false),[accounts,setAccounts]=useState<Account[]>([]);
 const [loading,setLoading]=useState(false),[error,setError]=useState(''),[enabled,setEnabled]=useState(true);
 const id=useId();
 useEffect(()=>{
  if(!open)return;
  let active=true;
  const load=async()=>{
   setLoading(true);setError('');
   try{const result=await api('/auth/review-accounts');if(active){setAccounts(result.items);setEnabled(result.enabled)}}
   catch(e){if(active){setAccounts([]);setError((e as Error).message)}}
   finally{if(active)setLoading(false)}
  };
  void load();window.addEventListener('focus',load);
  return()=>{active=false;window.removeEventListener('focus',load)};
 },[open]);
 return <section className="auth-review-access" aria-label="חשבונות לבדיקה">
  <button type="button" className="outline-button auth-review-toggle" aria-expanded={open} aria-controls={id} onClick={()=>setOpen(!open)}><Users size={18}/>{open?'הסתרת חשבונות לבדיקה':'הצגת חשבונות לבדיקה'}<ChevronDown size={17} style={{transform:open?'rotate(180deg)':undefined}}/></button>
  {open&&<div id={id}>
   {loading&&<p role="status">טוענים את חשבונות הבדיקה…</p>}
   {error&&<p role="alert">{error} <button type="button" className="text-button" onClick={()=>setOpen(false)}>סגירה וניסיון נוסף</button></p>}
   {!loading&&!error&&(!enabled?<p>פרסום חשבונות הבדיקה אינו פעיל.</p>:<>
    <p>חשבונות משותפים לבדיקת הפרויקט. בחירה ממלאת את הטופס בלבד.</p>
    {!accounts.length&&<p>עדיין אין חשבונות לבדיקה.</p>}
    {['admin','agent','employee'].map(role=>{
     const items=accounts.filter(a=>a.role===role);if(!items.length)return null;
     return <section key={role} aria-label={'חשבונות '+roles[role]}><h4>{roles[role]}</h4><ul>{items.map(account=><li key={account.id}>
      <strong>{roles[account.role]}</strong><dl><div><dt>דוא״ל</dt><dd><code dir="ltr">{account.email}</code></dd></div><div><dt>סיסמה</dt><dd>{account.password?<code dir="ltr">{account.password}</code>:<span>נדרשת השלמת סיסמת בדיקה על ידי המנהל</span>}</dd></div></dl>
      <button type="button" className="text-button" disabled={!account.password} aria-label={'מילוי פרטי '+account.email+' לבדיקה'} onClick={()=>account.password&&onSelect(account.email,account.password)}>מילוי פרטי התחברות</button>
     </li>)}</ul></section>;
    })}
   </>)}
  </div>}
 </section>;
}
