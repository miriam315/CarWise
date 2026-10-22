import {ArrowLeft,ShieldCheck,Clock} from 'lucide-react';
import type {Result} from './types';
import './net-summary.css';

const money=(value:number)=>new Intl.NumberFormat('he-IL',{maximumFractionDigits:0}).format(Math.abs(value));
const effect=(value:number)=>value===0?'ללא שינוי':Math.abs(value)<.5?(value>0?'תוספת קטנה מ־1 ₪':'ירידה קטנה מ־1 ₪'):`${value>0?'+':'−'}${money(value)} ₪`;
const net=(value:number)=>new Intl.NumberFormat('he-IL',{maximumFractionDigits:0}).format(value);
export default function NetSummary({result,pending,onAgent,onDetails,compact=false}:{result:Result;pending:boolean;onAgent?:()=>void;onDetails?:()=>void;compact?:boolean}){
 const ready=!pending&&result.eligible&&Number.isFinite(result.net)&&Number.isFinite(result.delta)&&Number.isFinite(result.annual);
 const title=result.car?.model||'ויתור על הרכב';
 return <section className={'net-summary '+(compact?'compact ':'')+(!ready?'waiting':result.delta<0?'loss':'gain')} aria-label="סיכום השינוי בנטו"><div className="net-summary-heading"><span>{title}</span>{ready?<span><ShieldCheck size={18}/> לפי המדיניות הפעילה</span>:<span><Clock size={18}/> ממתין לאישור</span>}</div>
 {!ready?<><h2>חסר אישור לפני שנציג נטו סופי.</h2><p>מה חסר? סכום ההשתתפות החודשית ברכב, באישור נציג.</p><p>עד לקבלת התשובה לא מוצג שינוי כספי סופי. לאחר האישור ההשוואה תתעדכן ותוכלו להמשיך מאותה שיחה.</p>{onAgent&&<button type="button" className="outline-button" onClick={onAgent}>לבדיקת הפנייה והנתון החסר <ArrowLeft size={18}/></button>}</>:<><h2>{result.delta===0?'הנטו החודשי נשאר זהה.':Math.abs(result.delta)<.5?'השינוי בנטו קטן מ־1 ₪ בחודש.':<>הנטו החודשי צפוי {result.delta>0?'לעלות':'לרדת'} ב־<bdi>{money(result.delta)} ₪</bdi>.</>}</h2><p className="net-summary-net">הנטו בחלופה: <bdi>{net(result.net)} ₪</bdi>{result.delta!==0&&<> · {effect(result.annual)} בשנה, אם התנאים נשארים זהים במשך 12 חודשים</>}</p>
 {result.car&&<p className="participation-source">השתתפות: {net(result.participation)} ₪ · מקור: {result.participationSource==='representative'?'אישור נציג':result.participationSource==='employee'?'דיווח העובד':'ברירת מחדל בקטלוג'}. {(!result.participationSource||result.participationSource==='catalogue')&&'אם אצלך סוכם אחרת, אפשר לעדכן או לבקש אישור נציג.'}</p>}
 {!compact&&result.explanation&&<><h3>שלושת הרכיבים שמסבירים את השינוי</h3><div className="net-drivers">{result.explanation.drivers.map(driver=><article key={driver.id}><div><strong>{driver.title}</strong><bdi className={driver.impact<0?'negative':'positive'}>{effect(driver.impact)}</bdi></div><details><summary aria-label={'פירוט '+driver.title}>מה נכלל ברכיב?</summary><p>{driver.description}</p></details></article>)}</div><small>סכומי הרכיבים מעוגלים בנפרד בתצוגה; החישוב מתבצע לפני עיגול. סכום שווי השימוש אינו מנוכה שוב מהנטו.</small></>}
 {onDetails&&<button type="button" className="text-button" onClick={onDetails}>לכל המספרים והמקורות <ArrowLeft size={18}/></button>}<p className="net-summary-note">הערכה לפי נתוני השכר והמדיניות הפעילים. אישור השתתפות נציג אינו אישור סופי להקצאת רכב או להחלפה.</p></>}
 </section>;
}
