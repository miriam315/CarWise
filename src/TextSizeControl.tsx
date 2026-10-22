import {useId,useLayoutEffect,useRef,useState} from 'react';
import {Type} from 'lucide-react';
const key='carwise-text-scale';
const minimum=.6,maximum=1.5,defaultScale=.8;
export default function TextSizeControl(){
 const [open,setOpen]=useState(false),optionsId=useId(),toggle=useRef<HTMLButtonElement>(null);
 const [scale,setScale]=useState(()=>{try{const value=Number(localStorage.getItem(key));return Number.isFinite(value)&&value>=minimum&&value<=maximum?value:defaultScale}catch{return defaultScale}});
 useLayoutEffect(()=>{document.documentElement.style.setProperty('--text-scale',String(scale));try{localStorage.setItem(key,String(scale))}catch{}},[scale]);
 function change(delta:number){setScale(s=>Math.round(Math.max(minimum,Math.min(maximum,s+delta))*10)/10)}
 return <div className="text-size-control" role="group" aria-label="גודל טקסט" onKeyDown={event=>{if(event.key==='Escape'){setOpen(false);toggle.current?.focus()}}}><button ref={toggle} type="button" className="text-size-toggle" aria-label="אפשרויות גודל הטקסט" title="גודל הטקסט" aria-expanded={open} aria-controls={optionsId} onClick={()=>setOpen(value=>!value)}><Type size={18}/></button>{open&&<div id={optionsId} className="text-size-options"><button type="button" aria-label="הקטנת טקסט" disabled={scale<=minimum} onClick={()=>change(-.1)}>א−</button><button type="button" aria-label="הגדלת טקסט" disabled={scale>=maximum} onClick={()=>change(.1)}>א+</button><button type="button" className="text-size-reset" aria-label="איפוס גודל הטקסט" onClick={()=>setScale(defaultScale)}>איפוס</button></div>}<span className="visually-hidden" role="status">גודל טקסט {Math.round(scale*100)} אחוז</span></div>;
}
