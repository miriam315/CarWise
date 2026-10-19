"""Deterministic monthly payroll illustration. Decimal only; no model arithmetic."""
from decimal import Decimal, getcontext, ROUND_HALF_UP
from pathlib import Path
import json, csv
getcontext().prec=36
DATA=Path(__file__).resolve().parent.parent/'data'
def load(name): return json.loads((DATA/f'{name}.json').read_text())
RULES=load('rules'); CARS=load('cars'); EMPLOYEES=load('employees'); OCCUPATIONS=load('occupations')
def D(x): return Decimal(str(x))
def rounded(x): return int(x.quantize(D(1),rounding=ROUND_HALF_UP))
OFFICIAL={}
with (DATA/'tax-authority-2026.csv').open(encoding='utf-8',newline='') as f:
 for row in csv.DictReader(f):
  key=tuple(row[k] for k in RULES['valueOfUse']['key'])
  if key in OFFICIAL and OFFICIAL[key]!=row: raise RuntimeError('Duplicate official key')
  OFFICIAL[key]=row

def official(car):
 key=('2026',str(car['year']),'1',car['makerCode'],car['modelCode'])
 if key not in OFFICIAL: raise ValueError('לא נמצאה התאמה מלאה לרכב במאגר הרשמי. נדרשת בדיקת נציג.')
 return OFFICIAL[key]

def occupation(e):
 return next(o for o in OCCUPATIONS if o['occupation']==e['occupation'])

def eligibility(e,car):
 o=occupation(e)
 if car is None: return (o['waiver'],'העיסוק מאפשר ויתור על רכב' if o['waiver'] else 'בתפקיד שטח הרכב הוא כלי עבודה ולכן אי אפשר לוותר עליו')
 if car['group']=='משפחתי':
  ok=e['children']>=RULES['familyTrack']['minChildren']
  return ok, 'מסלול משפחתי: לפחות 4 ילדים עד גיל 23; חלוקת פער התקציב 50/50' if ok else 'המסלול המשפחתי דורש לפחות 4 ילדים עד גיל 23'
 ok=RULES['groupOrder'].index(car['group'])<=RULES['groupOrder'].index(o['group'])
 return ok,'הרכב בקבוצת הזכאות שלך או בקבוצה נמוכה ממנה' if ok else 'הרכב בקבוצה גבוהה מקבוצת הזכאות שלך'

def calculate(e,car,participation=None):
 n=RULES['netMethod']; o=occupation(e)
 gross=sum(D(e[k]) for k in ['salary','commission','bonus'])
 pension=gross*D(e['pension'])/100; fund=gross*D(e['fund'])/100
 maintenance=D(o['maintenance']) if car is None else D(0)
 value=D(official(car)['שווי שימוש']) if car else D(0)
 part=D(participation if participation is not None else car['participation']) if car else D(0)
 family=max(D(0),D(car['cost'])-D(RULES['budgets'][o['group']]))*D(RULES['familyTrack']['gapSplitEmployee']) if car and car['group']=='משפחתי' else D(0)
 def payroll(extra):
  taxable=gross+maintenance+value+extra
  tax=D(0);lower=D(0)
  for ceiling,rate in n['brackets']:
   upper=D(ceiling) if ceiling is not None else taxable
   tax+=max(D(0),min(taxable,upper)-lower)*D(rate)
   lower=upper
  pc=n['pensionCredit']
  credit=min(pension,D(pc['maxContributionRate'])*D(pc['incomeCeiling']))*D(pc['rate'])
  tax=max(D(0),tax-D(e['credits'])*D(n['creditPointValue'])-credit)
  insurance=D(0)
  for name in ['nationalInsurance','health']:
   cfg=n[name];base=max(D(0),min(taxable,D(cfg['ceiling'])))
   insurance+=min(base,D(cfg['threshold']))*D(cfg['lowRate'])+max(D(0),base-D(cfg['threshold']))*D(cfg['highRate'])
  net=gross+maintenance+extra-tax-insurance-pension-fund-part-family
  return net,tax,insurance,taxable
 electric=D(0)
 if car and car['drive']=='חשמלי':
  target=D(RULES['electric']['netBonus'])
  if RULES['electric']['method']=='gross_up':
   base=payroll(D(0))[0];lo=D(0);hi=target*10
   for _ in range(120):
    mid=(lo+hi)/2
    if payroll(mid)[0]-base<target: lo=mid
    else: hi=mid
   electric=(lo+hi)/2
  else: electric=target
 net,tax,insurance,taxable=payroll(electric)
 return dict(net=net,tax=tax,insurance=insurance,taxable=taxable,gross=gross,pension=pension,fund=fund,value=value,participation=part,familyGap=family,maintenance=maintenance,electricGross=electric,electricNet=net-payroll(D(0))[0],budget=D(RULES['budgets'][o['group']]),cost=D(car['cost']) if car else D(0))

def validate_employee(e):
 if e.get('occupation') not in [o['occupation'] for o in OCCUPATIONS]: raise ValueError('יש לבחור עיסוק תקין')
 for key,maxvalue in [('salary',200000),('commission',200000),('bonus',200000),('pension',20),('fund',20),('credits',30),('children',30),('participation',20000)]:
  try: value=D(e[key])
  except Exception: raise ValueError('נתון חסר או לא תקין: '+key)
  if not value.is_finite() or value<0 or value>D(maxvalue): raise ValueError('נתון מחוץ לטווח: '+key)
 if D(e['children'])!=int(D(e['children'])): raise ValueError('מספר הילדים חייב להיות שלם')
 if e.get('currentCar') is not None and not any(c['id']==e['currentCar'] for c in CARS): raise ValueError('הרכב הנוכחי אינו בקטלוג')
 if e.get('currentCar') is None and not occupation(e)['waiver']: raise ValueError('בעיסוק זה נדרש רכב נוכחי')
 e['group']=occupation(e)['group']
 return e

def serialize(result): return {k:float(v) for k,v in result.items()}

def simulate(employee,overrides=None):
 e=validate_employee(dict(employee)); overrides=overrides or {}
 current=next((c for c in CARS if c['id']==e['currentCar']),None)
 before=calculate(e,current,e['participation'])
 results=[]
 for car in CARS+[None]:
  ok,reason=eligibility(e,car); id=car['id'] if car else 'waiver'
  result={'id':id,'eligible':ok,'reason':reason,'car':car}
  if ok:
   part=overrides.get(id)
   if part is not None and (not D(part).is_finite() or not 0<=D(part)<=20000): raise ValueError('השתתפות לא תקינה')
   values=calculate(e,car,part)
   result.update(serialize(values)); result['delta']=float(values['net']-before['net']);result['annual']=float((values['net']-before['net'])*12)
  results.append(result)
 return {'employee':e,'current':serialize(before),'results':results}

def extract_updates(text,employee):
 """Conservative local extraction: only explicit labels, never inferred amounts."""
 import re
 patterns={
  'salary':r'(?:שכר(?:\s+בסיס)?|בסיס)\s*[:=]?\s*([\d,]+(?:\.\d+)?)',
  'commission':r'עמלות\s*[:=]?\s*([\d,]+(?:\.\d+)?)',
  'bonus':r'בונוס\s*[:=]?\s*([\d,]+(?:\.\d+)?)',
  'pension':r'פנסיה\s*[:=]?\s*([\d,]+(?:\.\d+)?)',
  'fund':r'(?:קרן השתלמות|קה[״\"]ש)\s*[:=]?\s*([\d,]+(?:\.\d+)?)',
  'credits':r'(?:נקודות זיכוי|נק[׳\x27]\s*זיכוי)\s*[:=]?\s*([\d,]+(?:\.\d+)?)',
  'children':r'(?:ילדים(?: עד גיל 23)?\s*[:=]\s*|יש לי\s*)(\d+)(?:\s*ילדים)?'
 }
 updates={}
 for key,pattern in patterns.items():
  match=re.search(pattern,text)
  if match: updates[key]=float(match.group(1).replace(',',''))
 child=re.search(r'(\d+)\s*ילדים',text)
 if child: updates['children']=int(child.group(1))
 # 'יש לי 5' alone is ambiguous. Require the actual word children.
 if 'children' in updates and 'ילדים' not in text: del updates['children']
 return updates
