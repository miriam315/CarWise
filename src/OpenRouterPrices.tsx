import {useEffect,useState} from 'react';
import {RefreshCw,AlertCircle} from 'lucide-react';
import {api} from './api';
export type Price={provider:string;host:string;model:string;inputUsdPerMillion:number;cachedInputUsdPerMillion:number|null;outputUsdPerMillion:number;cacheWriteUsdPerMillion:number|null;source:string;retrievedAt?:string;sourceModel?:string;referenceOnly?:boolean};
type Profile={provider:string;baseUrl:string;model:string};
export type PricingSettings={models?:{id:string;name:string;profile:Profile}[];pricing?:Price[];openRouterPricingEnabled?:boolean;openRouterPricingUpdateHourUtc?:number;openRouterPriceMappings?:Record<string,string>;extraction:Profile;narration:Profile};
type Rate={inputUsdPerMillion:number|null;cachedInputUsdPerMillion:number|null;outputUsdPerMillion:number|null;cacheWriteUsdPerMillion:number|null;unavailableReason:string|null};
type Status={source:string;catalog:{retrievedAt:string;models:Record<string,Rate>}|null;lastAttempt:string|null;error:string|null;stale:boolean};
function host(profile:Profile){try{return new URL(profile.baseUrl).hostname}catch{return ''}}
function key(profile:Profile){return profile.provider+'/'+host(profile)+'/'+profile.model}
function routedThroughOpenRouter(profile:Profile){try{const u=new URL(profile.baseUrl);return ['compatible','openai'].includes(profile.provider)&&u.origin==='https://openrouter.ai'&&u.pathname.replace(/\/+$/,'')==='/api/v1'&&!u.search&&!u.hash&&!u.username&&!u.password}catch{return false}}
function sourceModel(profile:Profile,settings:PricingSettings){
 const mapped=settings.openRouterPriceMappings?.[key(profile)];if(mapped)return mapped;
 if(routedThroughOpenRouter(profile))return profile.model;
 const prefix:Record<string,string>={'openai/api.openai.com':'openai/','gemini/generativelanguage.googleapis.com':'google/','anthropic/api.anthropic.com':'anthropic/'};
 const providerPrefix=prefix[profile.provider+'/'+host(profile)];return providerPrefix?providerPrefix+profile.model:null;
}
const rate=(value:number|null|undefined)=>value==null?'לא פורסם':new Intl.NumberFormat('he-IL',{maximumFractionDigits:8}).format(value);
const when=(value:string)=>new Date(value).toLocaleString('he-IL',{timeZone:'Asia/Jerusalem'});
export default function OpenRouterPrices({settings,update}:{settings:PricingSettings;update:(path:(string|number)[],value:unknown)=>void}){
 const [status,setStatus]=useState<Status|null>(null),[busy,setBusy]=useState(false),[error,setError]=useState('');
 useEffect(()=>{let active=true;api('/admin/pricing/openrouter').then(result=>{if(active)setStatus(result)}).catch(e=>{if(active)setError((e as Error).message)});return()=>{active=false}},[]);
 const enabled=settings.openRouterPricingEnabled??true;
 const models=settings.models?.length?settings.models:[{id:'extraction',name:'הבנת השיחה',profile:settings.extraction},{id:'narration',name:'ניסוח תשובה',profile:settings.narration}];
 async function refresh(){setBusy(true);setError('');try{setStatus(await api('/admin/pricing/openrouter/refresh',{}))}catch(e){setError((e as Error).message)}finally{setBusy(false)}}
 function map(profile:Profile,value:string){const mappings={...settings.openRouterPriceMappings};if(value)mappings[key(profile)]=value;else delete mappings[key(profile)];update(['ai','openRouterPriceMappings'],mappings)}
 return <section className="panel admin-section" aria-label="מחירון OpenRouter">
  <div className="admin-section-heading"><div><h2>מחירון אוטומטי מ־OpenRouter</h2><p className="admin-intro">משיכת מחירון בלבד, ללא שינוי ספק ה־AI או ניתוב הקריאות. לספק ישיר זהו תעריף ייחוס של OpenRouter, שעשוי להיות שונה ממחיר החשבון בפועל. תעריף ידני שהוגדר מקבל קדימות. המחירים בדולר למיליון טוקנים.</p></div><button type="button" className="outline-button" disabled={busy} onClick={()=>void refresh()}><RefreshCw size={15}/>{busy?'מעדכנים מחירון…':'עדכון מחירי OpenRouter עכשיו'}</button></div>
  <div className="admin-form-grid"><label className="check-row"><input aria-label="סנכרון מחירי OpenRouter אוטומטי" type="checkbox" checked={enabled} onChange={e=>update(['ai','openRouterPricingEnabled'],e.target.checked)}/> שימוש בתעריפי ייחוס מ־OpenRouter ועדכון יומי אוטומטי</label><label>שעת עדכון המחירון (UTC)<input aria-label="שעת עדכון מחירון OpenRouter" type="number" min={0} max={23} step={1} value={settings.openRouterPricingUpdateHourUtc??3} onChange={e=>update(['ai','openRouterPricingUpdateHourUtc'],Number(e.target.value))}/><small>ההגדרות מופעלות לאחר שמירה. משיכה אוטומטית פעם ביום לפי השעה שנבחרה; לאחר כשל הניסיון הבא יהיה ביום הבא. אפשר לעדכן ידנית בכל עת. סנכרון המחירון אינו יוצר גרסת מדיניות.</small></label></div>
  <dl className="admin-connections"><div><dt>מקור</dt><dd><a href="https://openrouter.ai/api/v1/models" target="_blank" rel="noreferrer">OpenRouter Models API</a></dd></div><div><dt>עדכון אחרון שנשמר</dt><dd>{status?.catalog?when(status.catalog.retrievedAt):'טרם נשמר מחירון'}</dd></div><div><dt>ניסיון עדכון אחרון במופע זה</dt><dd>{status?.lastAttempt?when(status.lastAttempt):'טרם בוצע'}</dd></div></dl>
  {error&&<p className="error-banner" role="alert">{error}</p>}{status?.error&&<p className="error-banner" role="alert">{status.error}</p>}{status?.stale&&<p className="form-info"><AlertCircle size={17}/> אין מחירון שמור או שהוא בן יותר מ־36 שעות. במקרה של תקלה נשאר המחירון האחרון, ותאריך המקור נשמר בכל פעולה חדשה.</p>}
  <datalist id="openrouter-price-models">{Object.keys(status?.catalog?.models||{}).sort().map(id=><option key={id} value={id}/>)}</datalist>
  <div className="admin-table-wrap"><table className="admin-table"><thead><tr><th>המודל במערכת</th><th>מזהה במחירון OpenRouter</th><th>קלט $</th><th>קלט ממטמון $</th><th>פלט $</th><th>כתיבה למטמון $</th><th>מצב התמחור</th></tr></thead><tbody>{models.map(model=>{
   const p=model.profile,id=sourceModel(p,settings),tariff=id?status?.catalog?.models[id]:undefined,manual=settings.pricing?.find(row=>row.provider===p.provider&&row.host===host(p)&&row.model===p.model),direct=!routedThroughOpenRouter(p);
   return <tr key={model.id}><td><strong>{model.name}</strong><small dir="ltr">{p.model}</small><small dir="ltr">{host(p)}</small></td><td><input aria-label={'מזהה תמחור OpenRouter '+model.id} list="openrouter-price-models" dir="ltr" maxLength={160} value={settings.openRouterPriceMappings?.[key(p)]??''} placeholder={id||'בחירת מודל מקור'} onChange={e=>map(p,e.target.value)}/><small dir="ltr">{id}</small><small>השדה משנה רק את מקור המחיר. ריק = התאמה לפי מזהה מדויק.</small></td><td>{rate(tariff?.inputUsdPerMillion)}</td><td>{rate(tariff?.cachedInputUsdPerMillion)}</td><td>{rate(tariff?.outputUsdPerMillion)}</td><td>{rate(tariff?.cacheWriteUsdPerMillion)}</td><td>{manual?'תעריף ידני קודם למחירון':!enabled?'סנכרון כבוי — תעריף ידני בלבד':!tariff?'לא נמצאה התאמה מדויקת במחירון':tariff.unavailableReason||(direct?'אומדן לפי תעריף ייחוס OpenRouter':'אוטומטי — מחיר בסיס OpenRouter')}</td></tr>
  })}</tbody></table></div>
  <p className="admin-footnote">אין התאמה משוערת של שמות או גרסאות. כאשר המזהה שונה, בחרו במפורש את המודל המתאים במחירון. מחיר חסר נשאר לא ידוע, כולל תעריף מטמון שלא פורסם. מודלים עם מדרגות הקשר או תנאי תמחור נוספים דורשים תעריף ידני מתאים לחישוב. אומדן הטוקנים אינו כולל תוספות תמונה, אודיו, כלים, עמלות או הנחות חשבון, ואינו חשבונית. קריאות קודמות שומרות את התעריף המקורי.</p>
 </section>
}
