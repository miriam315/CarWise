from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from pathlib import Path
import json, sqlite3, uuid, mimetypes, re, os
from urllib.parse import urlparse
from engine import *
ROOT=Path(__file__).resolve().parent.parent
LOCAL=Path(os.environ.get('CARWISE_STATE_DIR',str(ROOT/'.local'))); LOCAL.mkdir(parents=True,exist_ok=True)
DB=LOCAL/'carwise.db'
def connection():
 c=sqlite3.connect(DB); c.row_factory=sqlite3.Row; return c
with connection() as db:
 db.execute('CREATE TABLE IF NOT EXISTS tickets (id TEXT PRIMARY KEY, employee TEXT, target TEXT, question TEXT, status TEXT, answer TEXT, participation REAL, created TEXT DEFAULT CURRENT_TIMESTAMP)')
 db.execute('CREATE TABLE IF NOT EXISTS messages (id INTEGER PRIMARY KEY, employee TEXT, role TEXT, text TEXT, created TEXT DEFAULT CURRENT_TIMESTAMP)')
def tickets():
 with connection() as db: return [dict(r) for r in db.execute('SELECT * FROM tickets ORDER BY created DESC')]
def history(employee):
 with connection() as db: return [dict(r) for r in db.execute('SELECT role,text FROM messages WHERE employee=? ORDER BY id',(employee,))]
def message(employee,role,text):
 with connection() as db: db.execute('INSERT INTO messages(employee,role,text) VALUES(?,?,?)',(employee,role,text))
class Handler(BaseHTTPRequestHandler):
 def send_json(self,obj,status=200):
  payload=json.dumps(obj,ensure_ascii=False).encode();self.send_response(status);self.send_header('Content-Type','application/json; charset=utf-8');self.send_header('Content-Length',str(len(payload)));self.end_headers();self.wfile.write(payload)
 def do_GET(self):
  path=urlparse(self.path)
  if path.path=='/api/data':
   return self.send_json({'cars':[{**c,'officialValue':int(official(c)['שווי שימוש']),'officialKey':['2026',str(c['year']),'1',c['makerCode'],c['modelCode']]} for c in CARS],'employees':EMPLOYEES,'occupations':OCCUPATIONS,'rules':RULES,'officialCount':len(OFFICIAL)})
  if path.path=='/api/tickets': return self.send_json(tickets())
  if path.path.startswith('/api/history/'):
   return self.send_json(history(path.path.split('/')[-1]))
  if path.path=='/api/health': return self.send_json({'status':'ok','records':len(OFFICIAL),'method':'Decimal / monthly 2025'})
  file=(ROOT/'dist'/path.path.lstrip('/')).resolve()
  if not file.is_relative_to(ROOT/'dist'): return self.send_json({'error':'Invalid path'},400)
  if not file.is_file(): file=ROOT/'dist/index.html'
  if not file.exists(): return self.send_json({'error':'Run npm run build, or use Vite on port 5173'},404)
  content=file.read_bytes();self.send_response(200);self.send_header('Content-Type',mimetypes.guess_type(str(file))[0] or 'application/octet-stream');self.send_header('Content-Length',str(len(content)));self.end_headers();self.wfile.write(content)
 def do_POST(self):
  try:
   length=int(self.headers.get('Content-Length','0'))
   if length>100000: return self.send_json({'error':'Request too large'},413)
   body=json.loads(self.rfile.read(length));path=urlparse(self.path).path
   if path=='/api/simulate': return self.send_json(simulate(body['employee'],body.get('overrides')))
   if path=='/api/tickets':
    e=validate_employee(body['employee']);target=body['target']
    if target not in [c['id'] for c in CARS]: raise ValueError('בחרו רכב מתוך הקטלוג')
    if not eligibility(e,next(c for c in CARS if c['id']==target))[0]: raise ValueError('אין זכאות לרכב המבוקש')
    question=str(body.get('question','מהי השתתפות העובד המאושרת ברכב החדש?'))[:2000]
    with connection() as db:
     existing=db.execute("SELECT id FROM tickets WHERE json_extract(employee, '$.id')=? AND target=? AND status=?",(e['id'],target,'pending')).fetchone()
     id=existing['id'] if existing else str(uuid.uuid4())
     if not existing: db.execute('INSERT INTO tickets(id,employee,target,question,status) VALUES(?,?,?,?,?)',(id,json.dumps(e,ensure_ascii=False),target,question,'pending'))
    message(e['id'],'assistant','הפנייה הועברה לנציג. החלופה ממתינה לאישור השתתפות; כשתתקבל תשובה נמשיך מאותה נקודה.')
    return self.send_json({'id':id,'status':'pending'})
   if path.startswith('/api/tickets/'):
    value=D(body['participation']);answer=str(body['answer']).strip()[:2000]
    if not value.is_finite() or value<0 or value>20000 or not answer: raise ValueError('נדרשים סכום השתתפות תקין ותשובה')
    with connection() as db:
     row=db.execute('SELECT * FROM tickets WHERE id=?',(path.split('/')[-1],)).fetchone()
     if not row: return self.send_json({'error':'הפנייה לא נמצאה'},404)
     db.execute('UPDATE tickets SET status=?,answer=?,participation=? WHERE id=?',('resolved',answer,float(value),row['id']))
    e=json.loads(row['employee']);car=next(c for c in CARS if c['id']==row['target']);simulation=simulate(e,{car['id']:float(value)})
    r=next(r for r in simulation['results'] if r['id']==car['id'])
    message(e['id'],'assistant',f"הנציג ענה: {answer}\nהשתתפות מאושרת: {rounded(value):,} ₪. חישבתי מחדש עבור {car['model']}: נטו {rounded(D(r['net'])):,} ₪; הפרש {rounded(D(r['delta'])):+,} ₪ בחודש.")
    return self.send_json({'status':'resolved'})
   if path=='/api/chat':
    e=validate_employee(body['employee']);text=str(body.get('text','')).strip()[:2000]
    if not text: raise ValueError('כתבו שאלה ליועץ')
    updates=extract_updates(text,e)
    if updates: e=validate_employee({**e,**updates})
    message(e['id'],'user',text)
    simulation=simulate(e,body.get('overrides'));lower=text.lower();target=body.get('target');candidates=[]
    aliases={'אטו':'CAR-10','atto':'CAR-10','קרניבל':'CAR-19','טסלה':'CAR-14','אוקטביה':'CAR-15','טוסון':'CAR-12','דולפין':'CAR-03','נירו':'CAR-05','סנטה':'CAR-20','קונה':'CAR-08','פיקנטו':'CAR-01','קורולה קרוס':'CAR-09','מאזדה':'CAR-13','סיל':'CAR-17'}
    if not target:
     for c in CARS:
      if c['model'].lower() in lower: candidates.append(c['id'])
     for alias,id in aliases.items():
      if alias in lower and id not in candidates: candidates.append(id)
     if len(candidates)==1: target=candidates[0]
     elif not candidates and any(s in lower for s in ['לוותר','אוותר','מוותר','ויתור','בלי רכב','אחזקת רכב']): target='waiver'
    if target:
     result=next((r for r in simulation['results'] if r['id']==target),None)
     if result is None: raise ValueError('הרכב לא נמצא; בחרו דגם מדויק מהקטלוג')
     waiting=any(json.loads(t['employee'])['id']==e['id'] and t['target']==target and t['status']=='pending' for t in tickets())
     if waiting: reply='החלופה הזו ממתינה לאישור השתתפות מנציג התחבורה. לא אציג נטו סופי עד לקבלת הסכום המאושר. אפשר בינתיים לבדוק חלופה אחרת.'
     elif not result['eligible']: reply='האפשרות הזו אינה זמינה: '+result['reason']+'. אפשר לבחור חלופה אחרת מהקטלוג.'
     else:
      name=result['car']['model'] if result['car'] else 'ויתור על הרכב'
      reply=f"בחלופה {name}, הנטו המשוער שלך יהיה {rounded(D(result['net'])):,} ₪ בחודש — שינוי של {rounded(D(result['delta'])):+,} ₪ לעומת היום.\n"
      reply+=f"שווי השימוש: {rounded(D(result['value'])):,} ₪; השתתפות: {rounded(D(result['participation'])):,} ₪. "
      if result['electricNet']: reply+='כלולים 250 ₪ נטו החזר חשמל, עם גילום מס מלא. '
      if result['familyGap']: reply+=f"חלקך בפער המסלול המשפחתי: {rounded(D(result['familyGap'])):,} ₪. "
      if result['maintenance']: reply+=f"אחזקת הרכב: {rounded(D(result['maintenance'])):,} ₪ ברוטו, חייבת במס ואינה פנסיונית. "
      reply+='\n'+result['reason']+'. הפירוט זמין בתצוגת ההשוואה.'
    elif 'חשמל' in lower or 'חסכ' in lower or 'כדאי' in lower:
     candidates=[r['id'] for r in sorted([r for r in simulation['results'] if r['eligible'] and r['car'] and r['car']['drive']=='חשמלי'],key=lambda r:r['net'],reverse=True)[:3]]
     reply='הנה רכבים חשמליים שמתאימים לקבוצת הזכאות שלך, לפי הנטו הגבוה ביותר. בחרו דגם מדויק כדי להציג חישוב והסבר.'
    else: reply='אפשר להשוות רכב מסוים, לבדוק מעבר לחשמלי או לשאול על ויתור על הרכב. בחרו דגם מהקטלוג או כתבו את שמו. נתוני השכר וההפרשות ניתנים לעדכון דרך ״הנתונים שלי״.'
    if updates:
     labels={'salary':'שכר בסיס','commission':'עמלות','bonus':'בונוס','pension':'פנסיה','fund':'קרן השתלמות','credits':'נקודות זיכוי','children':'ילדים עד גיל 23'}
     reply='עדכנתי את הנתונים שמסרת: '+', '.join(f"{labels[k]}: {v:g}" for k,v in updates.items())+'. כל החלופות חושבו מחדש.\n'+reply
    message(e['id'],'assistant',reply)
    return self.send_json({'reply':reply,'target':target,'candidates':candidates,'employee':e if updates else None})
   self.send_json({'error':'Not found'},404)
  except (ValueError,KeyError,TypeError,StopIteration) as error: self.send_json({'error':str(error)},400)
  except Exception as error:
   import traceback;traceback.print_exc();self.send_json({'error':'אירעה שגיאת שרת. נסו שוב.'},500)
if __name__=='__main__':
 port=int(os.environ.get('CARWISE_PORT','8000'))
 print(f'CarWise API: http://0.0.0.0:{port} — official records:',len(OFFICIAL),flush=True)
 ThreadingHTTPServer(('0.0.0.0',port),Handler).serve_forever()
