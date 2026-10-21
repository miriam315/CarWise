"""Challenge acceptance checks against the actual API and database; no paid AI calls."""
from copy import deepcopy
import time,urllib.error,subprocess,re

def check(query,database=None):
 baseline=query('/admin/configuration');config=deepcopy(baseline['configuration'])
 config['ai']['enabled']=False
 original=deepcopy(config['employees'][0]);employee={**original,'id':'E-CHALLENGE-INTAKE','name':'עובד בדיקת אתגר','knownFields':[],'advisorState':None}
 config['employees'].append(employee)
 target=next(car for car in config['cars'] if car['id']=='CAR-10');target['participationKnown']=False
 query('/admin/configuration',{'expectedRevision':baseline['revision'],'configuration':config,'note':'synthetic challenge acceptance: unknown fields and automatic referral'})
 calls=[]
 def chat(text,target=None):
  start=time.monotonic();result=query('/chat',{'employee':employee,'text':text,'target':target});elapsed=time.monotonic()-start;calls.append(elapsed)
  assert elapsed<55,(elapsed,result)
  return result
 try:
  incomplete=query('/simulate',{'employee':employee});assert incomplete['current'] is None and incomplete['results']==[] and not incomplete['complete']
  response=chat('אני רוצה לעבור לרכב חשמלי')
  assert response['awaitingField']=='salary'
  sequence=[('salary','19000.25'),('commission','0'),('bonus','0'),('pension','6%'),('fund','2.5%'),('credits','2.25'),('children','4')]
  for field,answer in sequence:
   assert response['awaitingField']==field,response
   saved=query('/conversation/'+employee['id'])['employee']
   assert saved['advisorState']['awaitingField']==field
   response=chat(answer) # The server must ignore this deliberately stale client profile.
  assert response['awaitingField']=='currentCar',response
  response=chat('זה הרכב שלי היום','CAR-09');assert response['awaitingField']=='participation'
  response=chat('לא יודע');assert 'שלחתי שאלה' in response['reply'] and not response['complete']
  waiting=[t for t in query('/tickets') if t['target']=='CAR-09' and t['status']=='pending' and __import__('json').loads(t['employee'])['id']==employee['id']]
  assert len(waiting)==1
  assert query('/simulate',{'employee':query('/conversation/'+employee['id'])['employee']})['current'] is None
  query('/tickets/'+waiting[0]['id'],{'answer':'השתתפות נוכחית מאושרת — בדיקה','participation':350})
  response=chat('נמשיך');assert response['candidates'] and 'CAR-10' not in response['candidates'],response
  response=chat('בחרתי את הדגם המדויק','CAR-10')
  assert 'שלחתי שאלה' in response['reply'] and not response['complete']
  response=chat('נמשיך')
  pending=[t for t in query('/tickets') if t['target']=='CAR-10' and t['status']=='pending' and __import__('json').loads(t['employee'])['id']==employee['id']]
  assert len(pending)==1,pending
  saved=query('/conversation/'+employee['id']);assert saved['status']=='PendingAgentResponse'
  simulation=query('/simulate',{'employee':saved['employee']});pending_result=next(r for r in simulation['results'] if r['id']=='CAR-10')
  assert pending_result['pending'] and pending_result['net'] is None and pending_result['delta'] is None
  query('/tickets/'+pending[0]['id'],{'answer':'השתתפות חדשה מאושרת — בדיקה','participation':300})
  response=chat('נמשיך');assert response['complete'] and response['target']=='CAR-10' and 'הנטו המשוער' in response['reply'],response
  saved=query('/conversation/'+employee['id']);assert saved['status']=='Active'
  assert saved['employee']['salary']==19000.25 and saved['employee']['commission']==0 and saved['employee']['bonus']==0
  assert saved['employee']['advisorState']['awaitingField'] is None
  simulation=query('/simulate',{'employee':saved['employee']});result=next(r for r in simulation['results'] if r['id']=='CAR-10')
  assert result['participation']==300 and not result['pending']
  assert any('הנציג ענה' in message['text'] for message in query('/history/'+employee['id']))
  print(f'PASS: persisted stepwise intake, explicit zero, stale-client protection, current vehicle confirmation, automatic baseline/new participation tickets, deduplication, approved resume and no pending final money; {len(calls)} local chat requests, maximum {max(calls):.2f}s; no paid inference.')
 finally:
  latest=query('/admin/configuration');query('/admin/restore',{'expectedRevision':latest['revision'],'revision':baseline['revision'],'note':'restore after synthetic challenge acceptance'})

  if database is not None:
   assert re.fullmatch(r'carwise_[a-z_0-9]+',database), 'Only an isolated test database may be cleaned'
   cleanup="DELETE FROM carwise_messages WHERE employee_id='E-CHALLENGE-INTAKE'; DELETE FROM carwise_conversations WHERE employee_id='E-CHALLENGE-INTAKE'; DELETE FROM carwise_tickets WHERE employee_id='E-CHALLENGE-INTAKE';"
   subprocess.run(['docker','exec','carwise-postgres','psql','-U','carwise','-d',database,'-v','ON_ERROR_STOP=1','-c',cleanup],check=True,stdout=subprocess.DEVNULL)
