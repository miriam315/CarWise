import {useEffect,useState} from 'react';
import {Car,ShieldCheck,ArrowLeft,Lock,AlertCircle,Mail} from 'lucide-react';
import {api} from './api';
import StraussLogo from './StraussLogo';
export default function AuthScreen(){
 const [first,setFirst]=useState(false),[canCreate,setCanCreate]=useState(false),[email,setEmail]=useState(''),[password,setPassword]=useState(''),[confirm,setConfirm]=useState(''),[busy,setBusy]=useState(false),[error,setError]=useState(''),[forgot,setForgot]=useState(false),[recoverySent,setRecoverySent]=useState(false);
 useEffect(()=>{api('/setup/admin/status').then(s=>{setCanCreate(s.needsAdmin);if(s.needsAdmin)setFirst(true)}).catch(()=>{})},[]);
 async function submit(event:React.FormEvent){
  event.preventDefault();setError('');
  if(forgot){setRecoverySent(true);return} // Explicit prototype: no request and no password change.
  if(first&&password!==confirm){setError('הסיסמאות אינן זהות.');return}
  setBusy(true);
  try{
   if(first){await api('/setup/admin',{email,password,role:'admin',employeeId:null});setCanCreate(false);setFirst(false)}
   const result=await api('/auth/login',{email,password});sessionStorage.setItem('carwise-access-token',result.accessToken);setPassword('');setConfirm('');location.reload();
  }catch(e){setError((e as Error).message)}finally{setBusy(false)}
 }
 function back(){setForgot(false);setRecoverySent(false);setError('');setPassword('')}
 return <div className="auth-shell"><section className="auth-story"><header className="auth-branding"><a className="brand" href="#"><span className="brand-icon"><Car size={26}/></span><span dir="ltr">CarWise.</span></a><StraussLogo/></header><div><span className="eyebrow">הרכב שלך. הדרך שלך.</span><h1>החלטה טובה מתחילה<br/>בתמונה ברורה.</h1><p>כל הנתונים, ההשוואות והשיחה האישית — במקום אחד.</p></div><span className="auth-trust"><ShieldCheck size={19}/> גישה אישית לחשבון שלך</span></section>
 <section className="auth-card panel"><span className="auth-lock">{forgot?<Mail size={24}/>:<Lock size={24}/>}</span><h2>{forgot?'שכחת את הסיסמה?':first?'ניצור את המנהל הראשון.':'ברוכים הבאים ל־CarWise'}</h2>
 <p>{forgot?'הזינו את כתובת המייל המשויכת לחשבון.':first?'הגדרה חד־פעמית מהמחשב שמריץ את השרת. אין סיסמת מנהל משותפת או קבועה.':'נכנסים עם כתובת המייל והסיסמה האישית שלך.'}</p>
 {recoverySent?<><div className="auth-recovery-notice" role="status"><strong>הודעת הדגמה: נשלחה סיסמה זמנית למייל.</strong><p dir="auto">{email}</p><small>זו הדגמה בלבד. לא נשלח דוא״ל ולא בוצע איפוס סיסמה. לאיפוס אמיתי יש לפנות למנהל המערכת.</small></div><button type="button" className="text-button" onClick={back}>חזרה להתחברות</button></>:<form onSubmit={submit}>
 <label>דוא״ל<input aria-label="דוא״ל להתחברות" type="email" required value={email} autoComplete="username" maxLength={254} onChange={e=>setEmail(e.target.value)}/></label>
 {!forgot&&<label>סיסמה<input aria-label="סיסמה להתחברות" type="password" required value={password} autoComplete={first?'new-password':'current-password'} minLength={first?12:undefined} maxLength={128} onChange={e=>setPassword(e.target.value)}/>{first&&<small>לפחות 12 תווים</small>}</label>}
 {first&&!forgot&&<label>אימות סיסמה<input aria-label="אימות סיסמת מנהל" type="password" required value={confirm} autoComplete="new-password" minLength={12} maxLength={128} onChange={e=>setConfirm(e.target.value)}/></label>}
 {error&&<div className="error-banner" role="alert"><AlertCircle size={16}/>{error}</div>}
 <button className="primary-button" disabled={busy}>{busy?'מתחברים…':forgot?'שליחת סיסמה זמנית':first?'יצירת מנהל וכניסה':'כניסה למערכת'}<ArrowLeft size={16}/></button>
 {!first&&!forgot&&<button type="button" className="auth-forgot text-button" onClick={()=>{setForgot(true);setPassword('');setError('')}}>שכחתי סיסמה</button>}
 {forgot&&<><p className="auth-help">המסך מדגים שחזור סיסמה בלבד; אין שליחת מייל בפועל.</p><button type="button" className="text-button" onClick={back}>חזרה להתחברות</button></>}
 </form>}
 {canCreate&&!first&&!forgot&&<button type="button" className="text-button" onClick={()=>{setFirst(true);setError('')}}>הגדרת מנהל ראשון</button>}
 {!first&&!forgot&&<p className="auth-help">אין לך חשבון? יש לפנות למנהל המערכת.</p>}
 </section></div>;
}
