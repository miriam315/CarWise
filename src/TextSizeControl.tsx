import {useLayoutEffect,useState} from 'react';
const key='carwise-text-scale';
const minimum=.6,maximum=1.5,defaultScale=.8;
export default function TextSizeControl(){
 const [scale,setScale]=useState(()=>{try{const value=Number(localStorage.getItem(key));return Number.isFinite(value)&&value>=minimum&&value<=maximum?value:defaultScale}catch{return defaultScale}});
 useLayoutEffect(()=>{document.documentElement.style.setProperty('--text-scale',String(scale));try{localStorage.setItem(key,String(scale))}catch{}},[scale]);
 function change(delta:number){setScale(s=>Math.round(Math.max(minimum,Math.min(maximum,s+delta))*10)/10)}
 return <div className="text-size-control" role="group" aria-label="גודל טקסט"><span>טקסט</span><button aria-label="הקטנת טקסט" disabled={scale<=minimum} onClick={()=>change(-.1)}>א−</button><button aria-label="הגדלת טקסט" disabled={scale>=maximum} onClick={()=>change(.1)}>א+</button><button className="text-size-reset" aria-label="איפוס גודל הטקסט" onClick={()=>setScale(defaultScale)}>איפוס</button><span className="visually-hidden" role="status">גודל טקסט {Math.round(scale*100)} אחוז</span></div>;
}
