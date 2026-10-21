import type {Box} from './payrollLayout';
export default function PayslipHighlights({boxes,page}:{boxes?:Box[];page:number}){
 return <div className="payslip-highlights" aria-hidden="true">{boxes?.filter(b=>b.page===page).map((b,i)=><span key={i} className={'payslip-highlight '+b.kind} style={{left:b.x*100+'%',top:b.y*100+'%',width:b.width*100+'%',height:b.height*100+'%'}}/>)}</div>;
}
