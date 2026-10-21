"""Exercise the real server, encrypted connection file, restart supervisor and login UI.
Database traffic uses real PostgREST; Supabase Auth uses a controlled GoTrue fixture.
"""
from pathlib import Path
import json,urllib.request,urllib.error,time,shutil
from playwright.sync_api import sync_playwright,expect

def check(base,query,settings,service_token):
 def authorized(path,token,body=None):
  raw=None if body is None else json.dumps(body).encode()
  with urllib.request.urlopen(urllib.request.Request(base+'/api'+path,data=raw,headers={'Content-Type':'application/json','Authorization':'Bearer '+token}),timeout=20) as r:return json.load(r)
 def denied(path,token,status,body=None):
  try:authorized(path,token,body);raise AssertionError('Expected authorization denial')
  except urllib.error.HTTPError as e:assert e.code==status,(path,e.code)
 with sync_playwright() as p:
  executable=shutil.which('chromium');browser=p.chromium.launch(headless=True,args=['--no-sandbox'],**({'executable_path':executable} if executable else {}));page=browser.new_page(viewport={'width':1440,'height':1000});errors=[];page.on('pageerror',lambda e:errors.append(str(e)))
  profile_requests=[];page.on('request',lambda r:profile_requests.append(r.url) if any(path in r.url for path in ['/api/conversation/','/api/history/','/api/simulate']) else None)
  page.goto(base);expect(page.get_by_role('heading',name='מחברים פעם אחת. ממשיכים לעבוד.')).to_be_visible()
  old=query('/runtime')['instanceId'];page.get_by_label('כתובת פרויקט Supabase',exact=True).fill('http://127.0.0.1:55434');page.get_by_label('מפתח שרת Supabase',exact=True).fill(service_token)
  page.get_by_role('button',name='בדיקת חיבור ושמירה קבועה',exact=True).click()
  expect(page.get_by_role('heading',name='ניצור את המנהל הראשון.')).to_be_visible(timeout=90000)
  assert query('/runtime')['instanceId']!=old and query('/runtime')['authenticationRequired']
  assert settings.exists() and service_token not in settings.read_text()
  page.get_by_label('דוא״ל להתחברות',exact=True).fill('admin@example.test');page.get_by_label('סיסמה להתחברות',exact=True).fill('TEST-ONLY-ADMIN-PASSWORD-123');page.get_by_label('אימות סיסמת מנהל',exact=True).fill('TEST-ONLY-ADMIN-PASSWORD-123');page.get_by_role('button',name='יצירת מנהל וכניסה',exact=True).click()
  try:page.wait_for_selector('.admin-page')
  except Exception:
   print('Auth screen diagnostics:',page.locator('.error-banner').all_text_contents(),page.locator('h1,h2').all_text_contents(),errors);raise
  admin=page.evaluate("sessionStorage.getItem('carwise-access-token')")
  expect(page.locator('.signed-in-user')).to_contain_text('admin@example.test')
  expect(page.get_by_label('עובד לצפייה',exact=True)).to_have_value('');assert not profile_requests,profile_requests
  assert page.locator('.topbar .profile-picker').count()==0
  page.get_by_role('tab',name='חשבונות והרשאות',exact=True).click();page.get_by_role('button',name='הוספת עובד חדש',exact=True).click()
  editor=page.get_by_role('dialog',name='עריכת עובד',exact=True)
  editor.get_by_label('שם',exact=True).fill('עובד חדש לבדיקת הרשאות');editor.get_by_label('שכר בסיס (₪)',exact=True).fill('18000')
  editor.get_by_label('פנסיית עובד (%)',exact=True).fill('6');editor.get_by_label('נקודות זיכוי',exact=True).fill('2.25')
  page.get_by_role('button',name='סיום עריכה בטיוטה',exact=True).click();page.get_by_label('סיבת שינוי הגדרות').fill('add a genuinely new employee profile before creating its login')
  page.get_by_role('button',name='שמירה והפעלה',exact=True).click();expect(page.locator('.admin-success')).to_contain_text('נשמרו והופעלו')
  new_employee=next(e for e in authorized('/data',admin)['employees'] if e['name']=='עובד חדש לבדיקת הרשאות')['id']
  assert not profile_requests,profile_requests
  page.get_by_role('tab',name='חשבונות והרשאות',exact=True).click();page.get_by_label('דוא״ל לחשבון חדש').fill('employee@example.test');page.get_by_label('סיסמה לחשבון חדש').fill('TEST-ONLY-EMPLOYEE-PASSWORD-123');page.get_by_label('פרופיל לחשבון חדש').select_option(new_employee);page.get_by_role('button',name='יצירת חשבון',exact=True).click();expect(page.locator('.admin-success').filter(has_text='החשבון נוצר')).to_be_visible()
  accounts=authorized('/admin/accounts',admin)['items'];worker=next(u for u in accounts if u['email']=='employee@example.test')
  authorized('/admin/accounts/'+worker['id']+'/password',admin,{'password':'TEST-ONLY-NEW-PASSWORD-123'})
  page.get_by_role('button',name='יציאה מהחשבון',exact=True).click();expect(page.get_by_role('heading',name='נכנסים לדרך שלך.')).to_be_visible();page.get_by_label('דוא״ל להתחברות',exact=True).fill('employee@example.test');page.get_by_label('סיסמה להתחברות',exact=True).fill('TEST-ONLY-NEW-PASSWORD-123');page.get_by_role('button',name='כניסה למערכת',exact=True).click();expect(page.get_by_role('heading',name='נשלים רק את מה שחסר.')).to_be_visible()
  employee=page.evaluate("sessionStorage.getItem('carwise-access-token')");own=authorized('/data',employee);assert len(own['employees'])==1 and own['employees'][0]['id']==new_employee and not own['canAdmin'] and not own['canAgent']
  expect(page.locator('.sidebar nav').get_by_role('button',name='ניהול המערכת',exact=False)).to_have_count(0);expect(page.locator('.sidebar nav').get_by_role('button',name='מרכז הנציגים',exact=False)).to_have_count(0)
  denied('/conversation/E-002',employee,403);denied('/admin/configuration',employee,403);denied('/admin/server-connection',employee,403)
  denied('/payslip/extract?employee=E-002&consent=true',employee,403,{})
  denied('/decision/state/E-002',employee,403)
  denied('/fleet/state/E-002',employee,403);denied('/fleet/status/E-002',employee,403);denied('/fleet/officer',employee,403)
  for endpoint in ['request','propose','withdraw','consent']:
   denied('/fleet/'+endpoint,employee,403,{'expectedRevision':0,'employeeId':'E-002','category':'family','consent':True,'id':'00000000-0000-0000-0000-000000000000'})
  denied('/fleet/handover',employee,403,{'expectedRevision':0,'id':'00000000-0000-0000-0000-000000000000','handoverOn':'2026-10-21','note':'forged handover'})
  denied('/fleet/decision',employee,403,{'expectedRevision':0,'id':'00000000-0000-0000-0000-000000000000','approve':True,'note':'forged officer'})
  denied('/fleet/assignment',employee,403,{'expectedRevision':0,'id':'FORGED','employeeId':new_employee,'carId':'CAR-09','assignedOn':'2025-01-01','leaseEndsOn':None,'earlyReturnFee':None,'transferAllowed':True})
  other={**own['employees'][0],'id':'E-002'}
  denied('/decision/analyze',employee,403,{'employee':other,'target':'CAR-10'})
  denied('/decision/choice',employee,403,{'employee':other,'expectedRevision':0,'target':'CAR-10'})
  denied('/decision/month',employee,403,{'employee':other,'expectedRevision':0,'month':{'period':'2026-09','fields':{},'bonusKind':'none'}})
  denied('/decision/month/remove',employee,403,{'employee':other,'expectedRevision':0,'period':'2026-09'})
  denied('/payslip/status','invalid-user-token',401)
  denied('/data','invalid-user-token',401)
  # A real employee session completes only fields the administrator did not confirm.
  page.get_by_role('button',name='השלמת נתונים בשיחה',exact=True).click()
  expect(page.locator('.message.assistant').last).to_contain_text('עמלות')
  for answer in ['0','0','0','0','ללא רכב']:
   page.get_by_label('הודעה ליועץ').fill(answer);page.get_by_role('button',name='שליחת הודעה',exact=True).click()
   expect(page.locator('.typing')).to_have_count(0)
  expect(page.get_by_role('heading',name='נשלים רק את מה שחסר.')).to_have_count(0)
  page.locator('.sidebar nav').get_by_role('button',name='הסקירה שלי',exact=False).click();page.wait_for_selector('.vehicle-card')
  completed=authorized('/conversation/'+new_employee,employee)['employee']
  assert completed['salary']==18000 and completed['pension']==6 and completed['currentCar'] is None and completed['participation']==0

  screenshots=Path(__file__).resolve().parent.parent/'.local/screenshots';screenshots.mkdir(parents=True,exist_ok=True)
  page.get_by_role('button',name='יציאה מהחשבון',exact=True).click();page.get_by_role('tab',name='כניסת מנהל',exact=True).click();page.screenshot(path=str(screenshots/'admin-login.png'),full_page=True)
  page.get_by_label('דוא״ל להתחברות',exact=True).fill('admin@example.test');page.get_by_label('סיסמה להתחברות',exact=True).fill('TEST-ONLY-ADMIN-PASSWORD-123');page.get_by_role('button',name='כניסה למערכת',exact=True).click();page.wait_for_selector('.admin-page');admin=page.evaluate("sessionStorage.getItem('carwise-access-token')")
  expect(page.locator('.signed-in-user')).to_contain_text('admin@example.test');page.get_by_label('עובד לצפייה',exact=True).select_option(new_employee)
  page.get_by_role('button',name='פתיחת תצוגת העובד',exact=True).click();page.wait_for_selector('.vehicle-card')
  expect(page.locator('.signed-in-user')).to_contain_text('admin@example.test');expect(page.get_by_label('צפייה בנתוני עובד',exact=True)).to_have_value(new_employee)
  page.locator('.sidebar nav').get_by_role('button',name='ניהול המערכת',exact=False).click();page.wait_for_selector('.admin-page');expect(page.get_by_label('עובד לצפייה',exact=True)).to_have_value('')
  page.get_by_role('tab',name='חיבור Supabase',exact=True).click();expect(page.get_by_label('מפתח שרת Supabase',exact=True)).to_have_value('');expect(page.locator('.admin-connections')).to_contain_text('פעילה')
  connection=authorized('/admin/server-connection',admin);assert connection['persisted'] and service_token not in json.dumps(connection)
  old=query('/runtime')['instanceId'];page.get_by_role('button',name='בדיקת חיבור ושמירה קבועה',exact=True).click()
  for _ in range(600):
   try:
    state=query('/runtime')
    if isinstance(state.get('instanceId'),str) and state['instanceId']!=old and not state.get('requiresSetup'):break
   except Exception:pass
   time.sleep(.15)
  else:raise AssertionError('Server did not restart from its saved connection')
  expect(page.get_by_role('tab',name='סקירה',exact=True)).to_have_attribute('aria-selected','true',timeout=90000)
  page.wait_for_selector('.admin-page');assert service_token not in settings.read_text()
  page.get_by_role('tab',name='חיבור Supabase',exact=True).click();page.screenshot(path=str(screenshots/'saved-connection.png'),full_page=True)
  denied('/setup/admin',admin,403,{'email':'other@example.test','password':'TEST-ONLY-OTHER-PASSWORD','role':'admin','employeeId':None})
  assert not query('/setup/admin/status')['needsAdmin']
  assert not errors,errors
  browser.close()
 print('PASS: first-run connection UI, encrypted persistence, automatic server restart, first-admin creation, worker account creation/password reset, employee/admin login, role isolation, hidden keys and repeat-bootstrap denial.')
