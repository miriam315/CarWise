import {useLayoutEffect,useState} from 'react';
const key='carwise-text-scale';
export default function TextSizeControl(){
 const [scale,setScale]=useState(()=>{try{const value=Number(localStorage.getItem(key));return Number.isFinite(value)&&value>=.8&&value<=1.5?value:1}catch{return 1}});
 useLayoutEffect(()=>{document.documentElement.style.setProperty('--text-scale',String(scale));try{localStorage.setItem(key,String(scale))}catch{}},[scale]);
 function change(delta:number){setScale(s=>Math.round(Math.max(.8,Math.min(1.5,s+delta))*10)/10)}
 return <div className="text-size-control" role="group" aria-label="גודל טקסט"><span>טקסט</span><button aria-label="הקטנת טקסט" disabled={scale<=.8} onClick={()=>change(-.1)}>א−</button><button aria-label="הגדלת טקסט" disabled={scale>=1.5} onClick={()=>change(.1)}>א+</button><button className="text-size-reset" aria-label="איפוס גודל הטקסט" onClick={()=>setScale(1)}>איפוס</button><span className="visually-hidden" role="status">גודל טקסט {Math.round(scale*100)} אחוז</span></div>;
}
