"""Reciprocal fleet swaps against real PostgreSQL or the real PostgREST adapter.
Synthetic profiles only. The caller supplies an isolated test database/server.
"""
from copy import deepcopy
from datetime import date,timedelta
import json,urllib.error
from playwright.sync_api import expect


def check(query,page=None,nav=None,screenshots=None):
 original=query('/admin/configuration');config=deepcopy(original['configuration'])
 a={**config['employees'][0],'id':'FLEET-TEST-A','name':'עובד בדיקה החלפה א','children':4,'currentCar':'CAR-09','advisorState':None}
 b={**config['employees'][0],'id':'FLEET-TEST-B','name':'עובד בדיקה החלפה ב','children':4,'currentCar':'CAR-19','advisorState':None}
 config['employees'] += [a,b]
 config['fleet']={'enabled':True,'replacementMonths':36,'swapEnabled':True,'allowEarlySwap':True,'requestValidityDays':30,'exceptionReasons':['role','family','location','medical','vehicle']}
 def reject(path,body,code=400):
  try:query(path,body);raise AssertionError('Unexpected acceptance: '+path)
  except urllib.error.HTTPError as error:assert error.code==code,(path,error.code)
 def state(e):return query('/fleet/state/'+e['id'])
 def revision():return query('/fleet/officer')['revision']
 def action(path,e,**body):return query('/fleet/'+path,{'expectedRevision':revision(),'employeeId':e['id'],**body})
 def save_config(configuration):
  current=query('/admin/configuration');query('/admin/configuration',{'expectedRevision':current['revision'],'configuration':configuration,'note':'Synthetic isolated fleet acceptance'})
 save_config(config)
 try:
  assert state(a)['replacement']['status']=='unknown'
  today=date.today();assigned=str(today-timedelta(days=548));end=str(today+timedelta(days=548))
  def assignment(e,physical,**overrides):return {'expectedRevision':revision(),'id':physical,'employeeId':e['id'],'carId':e['currentCar'],'assignedOn':assigned,'leaseEndsOn':end,'earlyReturnFee':None,'transferAllowed':True,**overrides}
  reject('/fleet/assignment',assignment(a,'FLEET-PHYSICAL-A',assignedOn=str(today+timedelta(days=1))))
  for e,p in [(a,'FLEET-PHYSICAL-A'),(b,'FLEET-PHYSICAL-B')]:query('/fleet/assignment',assignment(e,p))
  reject('/fleet/assignment',assignment(a,'FLEET-PHYSICAL-OTHER'))
  reject('/fleet/assignment',assignment(b,'FLEET-PHYSICAL-A'))
  assert state(a)['replacement']['status']=='locked'
  query('/fleet/assignment',assignment(a,'FLEET-PHYSICAL-A',exceptionReason='family',exceptionUntil=str(today)))
  assert state(a)['replacement']['status']=='exception'
  query('/fleet/assignment',assignment(a,'FLEET-PHYSICAL-A'))
  assert state(a)['replacement']['status']=='locked'
  reject('/fleet/request',{'employeeId':a['id'],'expectedRevision':revision(),'category':'family','consent':False})
  bad=next(e for e in original['configuration']['employees'] if e['id']=='E-001')
  reject('/fleet/request',{'employeeId':bad['id'],'expectedRevision':revision(),'category':'family','consent':True})
  before=query('/chat',{'employee':a,'text':'אני מעוניין לעבור לרכב ממסלול ברוכת ילדים אבל נשארו לי עוד שנה וחצי לחוזה של הרכב הרגיל שלי.'})
  assert 'לא נמצאה' in before['reply'] and 'לא הוזנו דמי החזרה' in before['reply']
  action('request',b,category='regular',consent=True)
  chat=query('/chat',{'employee':a,'text':'אני מעוניין לעבור לרכב ממסלול ברוכת ילדים אבל נשארו לי עוד שנה וחצי לחוזה של הרכב הרגיל שלי.'})
  assert 'זיהיתי הזדמנות' in chat['reply'] and 'אין התחייבות לביטול עמלות' in chat['reply']
  if page is not None:
   page.reload();page.wait_for_selector('.vehicle-card')
   page.get_by_label('בחירת עובד לדוגמה').select_option(a['id']);nav('החלפות רכבים')
   page.get_by_label('מסלול החלפה מבוקש').select_option('family')
   page.locator('.fleet-page input[type=checkbox]').check()
   page.get_by_role('button',name='פרסום בקשת החלפה',exact=True).click()
   expect(page.locator('.fleet-page')).to_contain_text('בקשה פעילה:')
   own=state(a)
  else:own=action('request',a,category='family',consent=True)
  assert len(own['matches'])==1
  assert own['assignment']['leaseEndsOn']==end # Chat never turns a claimed duration into verified dates.
  private=json.dumps(own,ensure_ascii=False)
  assert b['name'] not in private and b['id'] not in private and '"salary"' not in private and '"consents"' not in private
  simulated=query('/simulate',{'employee':a});expected=next(r for r in simulated['results'] if r['id']=='CAR-19')['delta']
  assert abs(own['matches'][0]['net']['delta']-expected)<.000001
  profile=query('/conversation/'+a['id'])['employee'];assert profile['currentCar']=='CAR-09'
  if page is not None:
   page.get_by_role('button',name='הצעת החלפה והסכמה שלי',exact=True).click()
   expect(page.locator('.fleet-page')).to_contain_text('ממתינה להסכמת הצד השני')
   proposal=state(a)['proposals'][-1]
  else:proposal=action('propose',a,id=own['matches'][0]['requestId'])['proposals'][-1]
  reject('/fleet/decision',{'expectedRevision':revision(),'id':proposal['id'],'approve':True,'note':'Cannot approve with one consent'})
  # A pay change must invalidate consent, even when both cars remain eligible.
  changed_profile={**query('/conversation/'+a['id'])['employee'],'bonus':a['bonus']+1}
  query('/profile',{'employee':changed_profile})
  reject('/fleet/consent',{'expectedRevision':revision(),'employeeId':b['id'],'id':proposal['id'],'accept':True})
  assert not state(a)['proposals'][-1]['valid']
  action('consent',a,id=proposal['id'],accept=False)
  proposal=action('propose',a,id=own['matches'][0]['requestId'])['proposals'][-1]
  stale=revision()
  if page is not None:
   page.get_by_label('בחירת עובד לדוגמה').select_option(b['id']);nav('החלפות רכבים')
   page.get_by_role('button',name='אני מסכים/ה · העברה לקצין הרכב',exact=True).click()
   expect(page.locator('.fleet-page')).to_contain_text('ממתינה לקצין הרכב')
   accepted=state(b)
  else:accepted=action('consent',b,id=proposal['id'],accept=True)
  assert accepted['proposals'][-1]['status']=='pendingOfficer'
  reject('/fleet/decision',{'expectedRevision':stale,'id':proposal['id'],'approve':True,'note':'Stale decision'},409)
  if page is not None:
   nav('מרכז הנציגים')
   page.get_by_label('הערת קצין הרכב לשני העובדים').fill('Synthetic approval: coordinate insurance, participation and handover')
   page.get_by_role('button',name='אישור החלפה לתיאום מסירה',exact=True).click()
   expect(page.locator('.fleet-officer')).to_contain_text('אושרה · ממתינה לתיאום מסירה')
  else:query('/fleet/decision',{'expectedRevision':revision(),'id':proposal['id'],'approve':True,'note':'Synthetic approval; coordinate contracts, insurance and participation before handover'})
  assert state(a)['reserved'] and state(b)['reserved']
  assert state(a)['proposals'][-1]['status']=='approved'
  assert query('/conversation/'+a['id'])['employee']['currentCar']=='CAR-09' # Approval is not physical handover.
  reject('/fleet/assignment',assignment(a,'FLEET-PHYSICAL-A'))
  reject('/fleet/request',{'expectedRevision':revision(),'employeeId':a['id'],'category':'family','consent':True})
  if page is not None:
   page.reload();page.wait_for_selector('.vehicle-card')
   page.get_by_label('בחירת עובד לדוגמה').select_option(a['id']);nav('החלפות רכבים')
   expect(page.locator('.fleet-page')).to_contain_text('אושרה · ממתינה לתיאום מסירה')
   page.screenshot(path=str(screenshots/'fleet-swap-desktop.png'),full_page=True)
   page.set_viewport_size({'width':390,'height':844})
   page.wait_for_function("document.querySelector('.sidebar').getBoundingClientRect().left >= innerWidth - 1")
   assert not page.evaluate('document.documentElement.scrollWidth>innerWidth')
   page.screenshot(path=str(screenshots/'fleet-swap-mobile.png'),full_page=True)
   page.set_viewport_size({'width':1440,'height':1100});nav('ניהול המערכת')
   page.get_by_role('tab',name='צי והחלפות',exact=False).click()
   expect(page.get_by_label('תקופת החלפה רגילה בחודשים')).to_have_value('36')
   expect(page.locator('.fleet-officer')).to_contain_text(a['name'])
   page.screenshot(path=str(screenshots/'fleet-swap-officer.png'),full_page=True)
  query('/fleet/decision',{'expectedRevision':revision(),'id':proposal['id'],'approve':False,'note':'Synthetic cancellation before handover'})
  assert not state(a)['reserved']
  # Assignment changes invalidate consent, even though the catalogue model is identical.
  old_request=state(a)['request'];query('/fleet/assignment',assignment(a,'FLEET-PHYSICAL-A'))
  assert state(a)['request'] is None
  reject('/fleet/propose',{'expectedRevision':revision(),'employeeId':a['id'],'id':old_request['id']})
  action('request',a,category='family',consent=True)
  current=state(a);assert len(current['matches'])==1
  current_profile=query('/conversation/'+a['id'])['employee']
  query('/profile',{'employee':{**current_profile,'currentCar':'CAR-05'}})
  assert state(a)['replacement']['status']=='unknown' and state(a)['matches']==[]
  query('/profile',{'employee':current_profile})
  assert state(a)['replacement']['status']=='locked'
  config['fleet']['allowEarlySwap']=False;save_config(config)
  assert state(a)['matches']==[]
  config['fleet']['allowEarlySwap']=True;config['cars']=deepcopy(original['configuration']['cars'])
  for c in config['cars']:
   if c['id']=='CAR-19':c['participationKnown']=False
  save_config(config)
  pending=state(a)['matches'][0]['net'];assert pending['pending'] and pending['delta'] is None
  # Record handover only after an officer confirms the actual updated source profiles.
  config['cars']=deepcopy(original['configuration']['cars']);save_config(config)
  offer=action('propose',a,id=state(a)['matches'][0]['requestId'])['proposals'][-1]
  action('consent',b,id=offer['id'],accept=True)
  query('/fleet/decision',{'expectedRevision':revision(),'id':offer['id'],'approve':True,'note':'Approved for synthetic handover'})
  reject('/fleet/handover',{'expectedRevision':revision(),'id':offer['id'],'handoverOn':str(today),'note':'Source profiles still unchanged'})
  pa=query('/conversation/'+a['id'])['employee'];pb=b
  query('/profile',{'employee':{**pa,'currentCar':b['currentCar'],'participation':500}})
  query('/profile',{'employee':{**pb,'currentCar':a['currentCar'],'participation':350}})
  if page is not None:
   page.reload();page.wait_for_selector('.vehicle-card');nav('מרכז הנציגים')
   page.get_by_label('הערת קצין הרכב לשני העובדים').fill('Actual synthetic handover: insurance and payroll updated')
   page.get_by_label('תאריך המסירה בפועל').fill(str(today))
   page.get_by_role('button',name='תיעוד מסירה בפועל וסגירת ההחלפה',exact=True).click()
   expect(page.locator('.fleet-officer')).to_contain_text('הושלמה · המסירה תועדה')
   page.screenshot(path=str(screenshots/'fleet-swap-completed.png'),full_page=True)
  else:query('/fleet/handover',{'expectedRevision':revision(),'id':offer['id'],'handoverOn':str(today),'note':'Actual synthetic handover verified; insurance and payroll updated'})
  final=state(a);assert not final['reserved'] and final['request'] is None
  assert final['proposals'][-1]['status']=='completed' and final['proposals'][-1]['handedOverOn']==str(today)
  assert final['proposals'][-1]['note']=='Approved for synthetic handover'
  assert final['assignment']['carId']=='CAR-19' and final['assignment']['assignedOn']==str(today)
  assert final['assignment']['leaseEndsOn']==end and final['assignment']['earlyReturnFee'] is None and not final['assignment']['transferAllowed']
  assert state(b)['assignment']['carId']=='CAR-09'
  assert query('/fleet/officer')['audit']
  print('PASS: fleet policy dates and approved exceptions; real reciprocal requests, private matches, authoritative net, two consents, officer-only approval, CAS, reservations, cancellation, stale assignment invalidation, early-swap policy, unknown-money nulls and source-verified atomic handover.',flush=True)
 finally:
  save_config(original['configuration'])
  if page is not None:
   page.reload();page.wait_for_selector('.vehicle-card')
