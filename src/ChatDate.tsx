const zone='Asia/Jerusalem';
const day=new Intl.DateTimeFormat('en-CA',{timeZone:zone,year:'numeric',month:'2-digit',day:'2-digit'});
const label=new Intl.DateTimeFormat('he-IL',{timeZone:zone,day:'numeric',month:'long',year:'numeric'});
const clock=new Intl.DateTimeFormat('he-IL',{timeZone:zone,hour:'2-digit',minute:'2-digit',hourCycle:'h23'});
function parsed(at?:string|null){if(!at)return null;const date=new Date(at);return Number.isNaN(date.getTime())?null:date}
export function chatDay(at?:string|null){const date=parsed(at);return date?day.format(date):'unknown'}
export function ChatDate({at}:{at?:string|null}){const date=parsed(at);return <h5 className="chat-date-separator">{date?<time dateTime={at!}>{label.format(date)}</time>:'תאריך לא זמין'}</h5>}
export function ChatTime({at}:{at?:string|null}){const date=parsed(at);return date?<time className="chat-message-time" dateTime={at!} title={label.format(date)+' · '+clock.format(date)+' (שעון ישראל)'}>{clock.format(date)}</time>:<span className="chat-message-time">שעה לא זמינה</span>}
