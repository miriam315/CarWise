// Shared review credentials, intentionally published at the user's request for assessment.
// VITE_SHOW_REVIEW_ACCOUNTS=false removes this section from the production build.
const accounts=[
 {role:'מנהל (admin)',email:'1234@gmail.com',password:'a2j9HMdkZaWGpd4',admin:true},
 {role:'עובד 1',email:'5678@gmail.com',password:'9vYqBZcHw8Hs9M8'},
 {role:'עובד 2',email:'98765@gmail.com',password:'v5GZ8wr7P3QDfEt'},
 {role:'עובד 3',email:'654@gmail.com',password:'a2j9HMdkZaWGpd4'},
 {role:'עובד 4',email:'111@gmail.com',password:'9vYqBZcHw8Hs9M8'},
];
export default function ReviewAccounts({onSelect}:{onSelect:(email:string,password:string)=>void}){
 return <section className="auth-review-access" aria-label="חשבונות לבדיקה"><h3>חשבונות לבדיקה</h3><p>לבדיקת הפרויקט אפשר להיכנס עם החשבונות הבאים.</p>{[{label:'מנהל',admin:true},{label:'עובדים',admin:false}].map(group=><section key={group.label} aria-label={'חשבונות '+group.label}><h4>{group.label}</h4><ul>{accounts.filter(account=>!!account.admin===group.admin).map(account=><li key={account.email}><strong>{account.role}</strong><dl><div><dt>דוא״ל</dt><dd><code dir="ltr">{account.email}</code></dd></div><div><dt>סיסמה</dt><dd><code dir="ltr">{account.password}</code></dd></div></dl><button type="button" className="text-button" aria-label={'מילוי פרטי '+account.role+' לבדיקה'} onClick={()=>onSelect(account.email,account.password)}>מילוי פרטי התחברות</button></li>)}</ul></section>)}</section>;
}
