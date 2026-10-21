// Local browser counterpart of LocalPayrollLayout. Raw words never enter an API payload.
export const payrollLimits:Record<string,number>={salary:200000,commission:200000,bonus:200000,pension:20,fund:20,credits:30,participation:20000};
export type Box={page:number;x:number;y:number;width:number;height:number;kind:'value'|'label'|'column'};
export type PayrollRead={fields:Record<string,{value:number|null;evidence:null}>;regions:Record<string,Box[]>;warnings:string[];source:string;configurationRevision:number;conflicts?:string[]};
export type Word={text:string;x:number;y:number;width:number;height:number;confidence:number;page:number;pageWidth:number;pageHeight:number};
const patterns:Record<string,string>={salary:'(?:שכר (?:בסיס|יסוד)|משכורת יסוד|base salary|basic salary)',commission:'(?:עמלות|commissions?)',bonus:'(?:בונוס|bonus)',participation:'(?:השתתפות (?:עובד )?ברכב|employee car participation)',pension:'(?:פנסיה|פנסיית עובד|פנסיה עובד|employee pension|pension)',fund:'(?:קרן השתלמות|קרן השתלמות עובד|employee study fund|study fund)',credits:"(?:נקודות זיכוי|נק[.׳']? זיכוי|tax credit points)"};
const match=(s:string,p:string)=>new RegExp('^(?:'+p+')$','iu').test(s);
const clean=(s:string)=>s.normalize('NFKC').replace(/[\p{Cf}\p{Mn}]/gu,'').replace(/\s+/g,' ').trim().replace(/^[ :|]+|[ :|]+$/g,'');
const reverse=(s:string)=>Array.from(s).reverse().join('').replace(/\d[\d,.]*/g,n=>Array.from(n).reverse().join(''));
const cx=(w:Word)=>w.x+w.width/2,cy=(w:Word)=>w.y+w.height/2;
const mean=(a:number[])=>a.reduce((s,n)=>s+n,0)/a.length;
type Phrase={text:string;words:Word[];x:number;right:number;center:number;y:number;height:number};
const phrase=(text:string,words:Word[]):Phrase=>{const x=Math.min(...words.map(w=>w.x)),right=Math.max(...words.map(w=>w.x+w.width));return{text,words,x,right,center:(x+right)/2,y:mean(words.map(cy)),height:Math.max(...words.map(w=>w.height))}};
const amount=/^(?<value>(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d{1,2})?)(?<percent>%)?$/;
const desc='(?:תיאור(?: רכיב)?|רכיב|קופה|description|component|fund)';
const headerPattern='(?:תיאור(?: רכיב)?|רכיב|קופה|קוד|כמות|תעריף|בסיס|סכום(?: (?:עובד|מעסיק|חודשי|לתשלום))?|אחוז (?:עובד|מעסיק)|מצטבר(?: עובד)?|חודש נוכחי|נקודות זיכוי|שווי נקודה|אחוז משרה|שעות עבודה|ימי עבודה|amount|description|code|quantity|rate|base|employee (?:rate|amount)|employer (?:rate|amount)|year to date|tax credit points)';
const unique=<T,>(a:T[],key:(v:T)=>string)=>Array.from(new Map(a.map(x=>[key(x),x])).values());
export function parseTsv(tsv:string,page=0):Word[]{
 const words:Word[]=[];let pageWidth=0,pageHeight=0;
 for(const row of tsv.split('\n').slice(1)){const c=row.replace(/\r$/,'').split('\t');if(c[0]==='1'){pageWidth=Number(c[8]);pageHeight=Number(c[9])}if(c.length<12||c[0]!=='5'||!c[11].trim())continue;words.push({text:c[11],x:Number(c[6]),y:Number(c[7]),width:Number(c[8]),height:Number(c[9]),confidence:Number(c[10]),page,pageWidth,pageHeight})}
 return words;
}
export function mergeReads(reads:PayrollRead[],source:string):PayrollRead{
 const fields:PayrollRead['fields']={},regions:PayrollRead['regions']={},conflicts:string[]=[];
 for(const key of Object.keys(payrollLimits)){
  const values=new Set(reads.map(r=>r.fields[key]?.value).filter((v):v is number=>v!=null));const conflict=values.size>1||reads.some(r=>r.conflicts?.includes(key));
  const value=!conflict&&values.size===1?[...values][0]:null;fields[key]={value,evidence:null};if(conflict)conflicts.push(key);
  if(value!=null){const boxes=reads.find(r=>r.fields[key]?.value===value&&r.regions[key])?.regions[key];if(boxes)regions[key]=boxes}
 }
 return{fields,regions,source,configurationRevision:0,conflicts,warnings:['החילוץ בדפדפן הוא הצעה בלבד. בדקו את החודש, הסכומים ואחוזי העובד מול המקור. שדה חסר או סותר נשאר לבדיקה ידנית.']};
}
export function repairHebrewLabels(original:Word[],hebrew:Word[],minimum:number):Word[]{
 const allowed='(?:עובד|מעסיק|פנסיה|השתלמות|שכר|בסיס|יסוד|זיכוי|נקודות|קרן|עמלות|בונוס|השתתפות|ברכב|אחוז|סכום|תעריף|כמות|קוד|תיאור|רכיב|קופה)';
 return original.map(w=>{if(!/[a-z]/i.test(w.text)||/[\dא-ת]/.test(w.text))return w;
 const candidates=hebrew.filter(h=>h.page===w.page&&h.confidence>=minimum&&Math.abs(cx(h)-cx(w))<Math.min(h.width,w.width)*.2&&Math.abs(cy(h)-cy(w))<Math.min(h.height,w.height)*.3&&Math.min(h.width,w.width)/Math.max(h.width,w.width)>.8&&match(clean(h.text),allowed));return candidates.length===1?{...w,text:clean(candidates[0].text),confidence:candidates[0].confidence}:w});
}
export function extractLayout(input:Word[],minimum=75,source='browser-ocr',signal?:AbortSignal):PayrollRead{
 const words=input.filter(w=>[w.x,w.y,w.width,w.height,w.confidence].every(Number.isFinite)&&w.width>0&&w.height>0&&w.text.length<=160);
 if(words.length>30000)throw Error('תוכן המסמך ארוך מדי לעיבוד בדפדפן.');
 const found=Object.fromEntries(Object.keys(patterns).map(k=>[k,new Set<number>()])),regions:Record<string,Box[]>={};
 const simple:PayrollRead={fields:Object.fromEntries(Object.keys(patterns).map(k=>[k,{value:null,evidence:null}])),regions:{},warnings:[],source,configurationRevision:0,conflicts:[]};
 function add(key:string,w:Word,percent:boolean,label:Phrase,header?:Phrase){
  const m=clean(w.text).match(amount);if(w.confidence<minimum||!m||!percent&&m.groups?.percent)return;const value=Number(m.groups!.value.replaceAll(',',''));if(value>payrollLimits[key])return;found[key].add(value);
  if(w.pageWidth>0&&w.pageHeight>0){const box=(p:Phrase,kind:Box['kind']):Box=>({page:w.page,x:p.x/w.pageWidth,y:Math.min(...p.words.map(x=>x.y))/w.pageHeight,width:(p.right-p.x)/w.pageWidth,height:(Math.max(...p.words.map(x=>x.y+x.height))-Math.min(...p.words.map(x=>x.y)))/w.pageHeight,kind});regions[key]=[box(phrase('',[w]),'value'),box(label,'label'),...(header?[box(phrase('',[...header.words,w]),'column')]:[])]}
 }
 for(const pageIndex of new Set(words.map(w=>w.page))){signal?.throwIfAborted();const page=words.filter(w=>w.page===pageIndex),rows:Word[][]=[];
  const punctuation=new Set(page.filter(w=>match(clean(w.text),'[—–\\-_|]+')));
  for(const w of page.filter(w=>!punctuation.has(w)).sort((a,b)=>cy(a)-cy(b))){signal?.throwIfAborted();const row=[...rows].reverse().find(r=>Math.abs(mean(r.map(cy))-cy(w))<=Math.min(w.height,mean(r.map(x=>x.height)))*.8);if(row)row.push(w);else rows.push([w])}
  for(const w of punctuation){const row=[...rows].sort((a,b)=>Math.abs(mean(a.map(cy))-cy(w))-Math.abs(mean(b.map(cy))-cy(w)))[0];if(row&&Math.abs(mean(row.map(cy))-cy(w))<mean(row.map(x=>x.height)))row.push(w)}
  const phrases:Phrase[]=[];
  for(const row of rows){signal?.throwIfAborted();const ordered=row.filter(w=>!punctuation.has(w)).sort((a,b)=>a.x-b.x);
   for(let i=0;i<ordered.length;i++)for(let n=1;n<=4&&i+n<=ordered.length;n++){const span=ordered.slice(i,i+n);if(span.some((w,j)=>j>0&&w.x-span[j-1].x-span[j-1].width>Math.max(w.height,span[j-1].height)*1.6))break;if(span.some(w=>w.confidence<minimum))continue;
    for(const seq of [span,[...span].reverse()])for(const text of [clean(seq.map(w=>w.text).join(' ')),clean(reverse(seq.map(w=>w.text).join(' ')))])phrases.push(phrase(text,span));
   }
   // Full physical row only, never a Tesseract block fragment.
   if(row.every(w=>w.confidence>=minimum))for(const seq of [[...row].sort((a,b)=>a.x-b.x),[...row].sort((a,b)=>b.x-a.x)])for(const text of [clean(seq.map(w=>w.text).join(' ')),clean(reverse(seq.map(w=>w.text).join(' ')))]){
    for(const [key,label] of Object.entries(patterns)){
     const explicit=key==='pension'?'(?:פנסי(?:ה|י?ת) עובד|הפרשת עובד לפנסיה|employee pension|pension employee)':key==='fund'?'(?:קרן השתלמות עובד|הפרשת עובד לקרן השתלמות|employee study fund|employee education fund)':label;
     const number='(?<value>(?:\\d{1,3}(?:,\\d{3})+|\\d+)(?:\\.\\d{1,2})?)\\s*(?<unit>%|₪|NIS|ILS)?';
     const m=text.match(new RegExp('^(?:'+explicit+'\\s*[:=|]?\\s*'+number+'|'+number.replaceAll('?<value>','?<otherValue>').replaceAll('?<unit>','?<otherUnit>')+'\\s*[:=|]?\\s*'+explicit+')$','iu'));
     if(m){const value=Number((m.groups?.value||m.groups?.otherValue||'').replaceAll(',','')),percent=(m.groups?.unit||m.groups?.otherUnit)==='%';if(value<=payrollLimits[key]&&percent===(key==='pension'||key==='fund')){const previous=simple.fields[key].value;if(previous!=null&&previous!==value)simple.conflicts!.push(key);simple.fields[key]={value,evidence:null};const w=row.find(w=>clean(w.text).match(amount)&&Number(clean(w.text).replaceAll(',','').replace('%',''))===value);if(w)add(key,w,percent,phrase(explicit,row.filter(x=>x!==w)))}}
    }
   }
  }
  const descriptions=phrases.filter(p=>match(p.text,desc)),amountHeaders=phrases.filter(p=>match(p.text,'(?:סכום(?: חודשי| לתשלום)?|חודש נוכחי|amount|monthly amount)')),rateHeaders=phrases.filter(p=>match(p.text,'(?:אחוז עובד|אחוזי עובד|שיעור עובד|employee (?:rate|percent)|employee %|% employee)')),headers=phrases.filter(p=>match(p.text,headerPattern));
  for(const [key,pattern] of Object.entries(patterns))for(const label of unique(phrases.filter(p=>match(p.text,pattern)),p=>p.x+':'+p.y+':'+p.right)){
   signal?.throwIfAborted();const adjacent=page.filter(w=>Math.abs(cy(w)-label.y)<label.height*.8&&(w.x>=label.right&&w.x-label.right<label.height*1.6||w.x+w.width<=label.x&&label.x-w.x-w.width<label.height*1.6));
   if(adjacent.some(w=>[clean(w.text),clean(reverse(w.text))].some(t=>match(t,'(?:מצטבר(?:ים)?|שנתי|מעסיק|ברוטו|נטו|cumulative|annual|employer|gross|net|YTD)'))))continue;
   if(['salary','commission','bonus','participation'].includes(key)&&adjacent.some(w=>!match(clean(w.text),'[\\d,.%₪|:=()—–\\-]+')))continue;
   if(key==='credits'){const next=page.filter(w=>cy(w)>label.y+label.height*.6&&cy(w)<label.y+label.height*4&&cx(w)>=label.x-label.height&&cx(w)<=label.right+label.height).sort((a,b)=>cy(a)-cy(b));if(next.length){const nearest=next.filter(w=>Math.abs(cy(w)-cy(next[0]))<label.height*.8);if(nearest.length===1)add(key,nearest[0],false,label)}continue}
   const percent=key==='pension'||key==='fund';const applicable=(percent?rateHeaders:amountHeaders).filter(h=>h.y<label.y-label.height*.5&&label.y-h.y<label.height*26).sort((a,b)=>b.y-a.y);
   for(const referenceY of new Set(applicable.map(h=>h.y))){
    const band=applicable.filter(h=>Math.abs(h.y-referenceY)<label.height*2),ds=descriptions.filter(d=>Math.abs(d.y-referenceY)<label.height*2&&Math.abs(d.center-label.center)<label.height*6).sort((a,b)=>Math.abs(a.center-label.center)-Math.abs(b.center-label.center));if(!ds.length)continue;const d=ds[0];
    if(descriptions.some(x=>x.y>referenceY+label.height&&x.y<label.y-label.height*.5&&Math.abs(x.center-label.center)<label.height*6))break;
    const barriers=headers.filter(h=>Math.abs(h.y-referenceY)<label.height*2&&(match(h.text,'(?:קוד|code)')||match(h.text,desc)&&Math.abs(h.center-d.center)>label.height*3));
    const candidates=unique(band.filter(h=>!barriers.some(b=>b.center>Math.min(h.center,d.center)+label.height&&b.center<Math.max(h.center,d.center)-label.height)),h=>String(Math.round(h.center/label.height)));if(candidates.length!==1)continue;const h=candidates[0];
    const bounds=headers.filter(x=>Math.abs(x.y-h.y)<label.height*2&&Math.abs(x.center-h.center)>label.height*1.4);
    const lb=bounds.filter(x=>x.center<h.center).map(x=>(x.center+h.center)/2),rb=bounds.filter(x=>x.center>h.center).map(x=>(x.center+h.center)/2);const left=Math.max(...(lb.length?lb:[h.x-label.height*3])),right=Math.min(...(rb.length?rb:[h.right+label.height*3]));
    const values=page.filter(w=>cx(w)>left&&cx(w)<right&&Math.abs(cy(w)-label.y)<label.height*.9);if(values.length===1)add(key,values[0],percent,label,h);break;
   }
  }
 }
 const result:PayrollRead={fields:{},regions,warnings:[],source,configurationRevision:0,conflicts:[]};
 for(const [key,values] of Object.entries(found)){result.fields[key]={value:values.size===1?[...values][0]:null,evidence:null};if(values.size>1)result.conflicts!.push(key)}
 return mergeReads([result,simple],source);
}
