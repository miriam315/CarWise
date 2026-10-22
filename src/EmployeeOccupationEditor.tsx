import {useState,useEffect} from 'react';
import type {Occupation} from './types';
export default function EmployeeOccupationEditor({occupations,groups,value,onSelect,onAdd,onPendingChange}:{onPendingChange:(pending:boolean)=>void;occupations:Occupation[];groups:string[];value:string;onSelect:(occupation:Occupation)=>void;onAdd:(occupation:Occupation)=>void}){
 const [custom,setCustom]=useState(false),[name,setName]=useState(''),[group,setGroup]=useState(''),[mandatory,setMandatory]=useState(false),[waiver,setWaiver]=useState(false),[maintenance,setMaintenance]=useState(0),[error,setError]=useState('');
 useEffect(()=>{onPendingChange(custom);return()=>onPendingChange(false)},[custom,onPendingChange]);
 function add(){
  const trimmed=name.trim();setError('');
  if(!trimmed||trimmed.length>100){setError('הזינו שם עיסוק באורך 1–100 תווים.');return}
  const existing=occupations.find(o=>o.occupation.toLocaleLowerCase()===trimmed.toLocaleLowerCase());
  if(existing){onSelect(existing);setCustom(false);return}
  if(!groups.includes(group)){setError('בחרו במפורש את קבוצת הזכאות של העיסוק החדש.');return}
  if(!Number.isFinite(maintenance)||maintenance<0||maintenance>30000){setError('אחזקת רכב חייבת להיות בין 0 ל־30,000 ₪.');return}
  onAdd({occupation:trimmed,group,mandatory,waiver:!mandatory&&waiver,maintenance:!mandatory&&waiver?maintenance:0});setCustom(false);
 }
 return <><label>עיסוק<select aria-label="עיסוק העובד" value={custom?'__new__':value} onChange={e=>{setError('');if(e.target.value==='__new__'){setCustom(true);return}setCustom(false);const selected=occupations.find(o=>o.occupation===e.target.value);if(selected)onSelect(selected)}}>{occupations.map(o=><option key={o.occupation} value={o.occupation}>{o.occupation}</option>)}<option value="__new__">אחר — עיסוק חדש</option></select></label>
 {custom&&<section className="full employee-new-occupation"><h3>הוספת עיסוק במלל חופשי</h3><p>כדי לחשב זכאות צריך להגדיר גם את כללי הרכב שלו. אלה כללי זכאות לכל העובדים בעיסוק החדש; הם אינם מעניקים הרשאת נציג.</p><div className="admin-form-grid"><label>שם העיסוק החדש<input aria-label="שם העיסוק החדש" maxLength={100} value={name} onChange={e=>setName(e.target.value)}/></label><label>קבוצת הזכאות לעיסוק החדש<select aria-label="קבוצת זכאות לעיסוק חדש" value={group} onChange={e=>setGroup(e.target.value)}><option value="">בחרו קבוצת זכאות</option>{groups.map(g=><option key={g}>{g}</option>)}</select></label><label className="check-row"><input type="checkbox" checked={mandatory} onChange={e=>{setMandatory(e.target.checked);if(e.target.checked){setWaiver(false);setMaintenance(0)}}}/> רכב חובה לעיסוק החדש</label><label className="check-row"><input type="checkbox" disabled={mandatory} checked={waiver} onChange={e=>{setWaiver(e.target.checked);if(!e.target.checked)setMaintenance(0)}}/> מותר לוותר על רכב בעיסוק החדש</label>{waiver&&<label>אחזקה חודשית בוויתור (₪)<input aria-label="אחזקת רכב לעיסוק חדש" type="number" min={0} max={30000} step="0.01" value={maintenance} onChange={e=>setMaintenance(Number(e.target.value))}/></label>}</div>{error&&<p role="alert">{error}</p>}<button type="button" className="outline-button" onClick={add}>הוספת העיסוק ושיוכו לעובד</button><small>עיסוק חדש משנה מדיניות זכאות ולכן שמירתו תדרוש סיבת שינוי. אם ויתור אינו מותר, יש לשייך לעובד רכב נוכחי.</small></section>}
 </>;
}
