"""Isolated end-to-end smoke test. Requires playwright and Chromium."""
from pathlib import Path
import os, sys, subprocess, tempfile, time, json, urllib.request, shutil, re, uuid, signal
from playwright.sync_api import sync_playwright, expect
ROOT=Path(__file__).resolve().parent.parent
BASE='http://127.0.0.1:8001'
def request(path,body=None):
 data=None if body is None else json.dumps(body).encode()
 with urllib.request.urlopen(urllib.request.Request(BASE+'/api'+path,data=data,headers={'Content-Type':'application/json'})) as r:return json.load(r)
def upload_api(path,content):
 with urllib.request.urlopen(urllib.request.Request(BASE+'/api'+path,data=content,headers={'Content-Type':'application/octet-stream'}),timeout=20) as response:return json.load(response)
def start(state):
 process=subprocess.Popen(['node','scripts/dotnet.mjs','run','--project','backend/CarWise.Api','--no-launch-profile','--no-build'],cwd=ROOT,
  env={**os.environ,'ASPNETCORE_ENVIRONMENT':'Development','ASPNETCORE_URLS':BASE,'ConnectionStrings__CarWise':state,'SUPABASE_URL':'','CARWISE_SUPABASE_KEY':'','CARWISE_AI_API_KEY':''},
  stdout=(ROOT/'.local/smoke-server.log').open('w'),stderr=subprocess.STDOUT,start_new_session=True)
 for _ in range(600):
  try:request('/health');return process
  except Exception:
   if process.poll() is not None: raise RuntimeError('ASP.NET Core test server exited')
   time.sleep(.1)
 stop(process);raise RuntimeError('Test server did not start')
def stop(process):
 if process.poll() is None:
  os.killpg(process.pid,signal.SIGTERM);process.wait(timeout=10)
 # dotnet run has a child process; wait for its listener to close before restarting.
 for _ in range(100):
  try:request('/health')
  except Exception:return
  time.sleep(.05)
 raise RuntimeError('Previous ASP.NET Core listener did not stop')
with tempfile.TemporaryDirectory(prefix='carwise-smoke-') as temp:
 dbname='carwise_smoke_'+uuid.uuid4().hex[:12]
 subprocess.run(['docker','exec','carwise-postgres','createdb','-U','carwise',dbname],check=True)
 config=json.loads((ROOT/'.local/appsettings.Development.json').read_text())
 connection=re.sub(r'Database=[^;]+',f'Database={dbname}',config['ConnectionStrings']['CarWise'])
 process=start(connection)
 try:
  from importlib.machinery import SourceFileLoader
  admin_revision=SourceFileLoader('admin_checks',str(ROOT/'scripts/admin-checks.py')).load_module().check_admin(request,dbname,upload_api)
  data=request('/data');assert data['officialCount']==77988
  employee=data['employees'][0]
  runtime=request('/runtime');assert runtime['server']=='ASP.NET Core 10' and runtime['database']=='PostgreSQL'
  sim=request('/simulate',{'employee':employee,'overrides':{'CAR-10':0}})
  assert next(r for r in sim['results'] if r['id']=='CAR-10')['participation']==300 # Client cannot forge approval.
  from concurrent.futures import ThreadPoolExecutor
  # The browser below will also submit; PostgreSQL serializes duplicate requests.

  extracted=request('/chat',{'employee':{**employee,'id':'E-SMOKE-EXTRACTION'},'text':'שכר בסיס 19500, השווה ATTO 3'})
  assert extracted['employee']['salary']==19500 and extracted['target']=='CAR-10'
  assert 'עדכנתי' in extracted['reply']
  with sync_playwright() as p:
   args={'executable_path':shutil.which('chromium')} if shutil.which('chromium') else {}
   browser=p.chromium.launch(headless=True,args=['--no-sandbox'],**args)
   page=browser.new_page(viewport={'width':1440,'height':1100})
   errors=[];page.on('pageerror',lambda error:errors.append(str(error)))
   page.goto(BASE);page.wait_for_selector('.vehicle-card')
   expect(page.locator('.stat-card').first).to_contain_text('11,032')
   expect(page.locator('.insight-banner')).to_contain_text('661')
   assert not page.evaluate('document.documentElement.scrollWidth > innerWidth')
   with page.expect_download() as dl:
    page.get_by_role('button',name='הורדת סיכום').click()
   assert dl.value.suggested_filename=='CarWise-comparison.csv'
   assert '11' in Path(dl.value.path()).read_text(encoding='utf-8-sig')
   screenshots=ROOT/'.local/screenshots';screenshots.mkdir(parents=True,exist_ok=True)
   def nav(name):page.locator('.sidebar nav').get_by_role('button',name=name,exact=False).click()
   # Short laptop windows and 125%-equivalent viewport: every sidebar control must scroll into view.
   for width,height in [(1366,768),(1280,600),(1093,614),(800,450)]:
    page.set_viewport_size({'width':width,'height':height})
    page.locator('.sidebar-bottom .strauss-logo').scroll_into_view_if_needed()
    bounds=page.locator('.sidebar-bottom .strauss-logo').bounding_box()
    assert bounds['y']>=0 and bounds['y']+bounds['height']<=height+1,(width,height,bounds)
    nav('מקורות וכללים');expect(page.locator('.source-card').first).to_be_visible()
    page.locator('.sidebar .brand').scroll_into_view_if_needed()
    bounds=page.locator('.sidebar .brand').bounding_box();assert bounds['y']>=0
    nav('הסקירה שלי')
    assert not page.evaluate('document.documentElement.scrollWidth > innerWidth')
   page.set_viewport_size({'width':1366,'height':768})
   page.locator('.sidebar-bottom .strauss-logo').scroll_into_view_if_needed()
   page.screenshot(path=str(screenshots/'sidebar-laptop.png'),full_page=False)
   page.locator('.sidebar .brand').scroll_into_view_if_needed()
   page.set_viewport_size({'width':1440,'height':1100})
   page.screenshot(path=str(screenshots/'desktop.png'),full_page=True)
   nav('הנתונים שלי')
   page.get_by_label('שכר בסיס חודשי (₪)').fill('19500')
   page.get_by_role('button',name='שמירה וחישוב מחדש').click()
   page.wait_for_selector('.stats-grid');expect(page.locator('.stat-card').first).not_to_contain_text('11,032')
   nav('הנתונים שלי');page.get_by_role('button',name='איפוס לדוגמה').click();page.get_by_role('button',name='שמירה וחישוב מחדש').click()
   page.wait_for_selector('.vehicles-grid');expect(page.locator('.stat-card').first).to_contain_text('11,032')
   nav('קטלוג הרכבים');page.locator('.filter-tabs').get_by_role('button',name='חשמלי',exact=True).click()
   assert page.locator('.catalog-card').count()==5
   page.get_by_label('חיפוש רכב').fill('ATTO');expect(page.locator('.catalog-card')).to_have_count(1)
   page.get_by_role('button',name='לבדיקת החלופה').click()
   expect(page.get_by_role('dialog')).to_contain_text('2026 / 2025 / 1 / 01014 / 00003')
   page.get_by_role('button',name='לא בטוחים בהשתתפות? שואלים נציג').click()
   page.wait_for_selector('.toast')
   ticket_id=request('/tickets')[0]['id']
   assert request('/tickets',{'employee':employee,'target':'CAR-10','question':'מהי ההשתתפות המאושרת?'})['id']==ticket_id
   assert len(request('/tickets'))==1
   assert request('/conversation/E-001')['status']=='PendingAgentResponse'
   with ThreadPoolExecutor(max_workers=4) as pool:
    ids=list(pool.map(lambda _:request('/tickets',{'employee':employee,'target':'CAR-10','question':'מהי ההשתתפות המאושרת?'})['id'],range(4)))
   assert len(set(ids))==1 and ids[0]==ticket_id
   assert 'ממתינה' in request('/chat',{'employee':employee,'text':'השווה ATTO 3','target':'CAR-10'})['reply']
   nav('הסקירה שלי');expect(page.locator('.insight-banner')).to_contain_text('מחכים לתשובת הנציג')
   page.reload();page.wait_for_selector('.insight-banner');expect(page.locator('.insight-banner')).to_contain_text('מחכים לתשובת הנציג')
   nav('מרכז הנציגים');expect(page.locator('.ticket-card')).to_have_count(1)
   page.get_by_label('השתתפות עובד מאושרת (₪)').fill('350')
   page.get_by_label('תשובת הנציג').fill('אושר: 350 ₪ לחודש, בתוקף מיידי.')
   page.get_by_role('button',name='שליחת תשובה והמשך השיחה').click()
   expect(page.locator('.resolved-answer')).to_contain_text('350')
   assert request('/conversation/E-001')['status']=='Active'
   resolved=request('/simulate',{'employee':employee,'overrides':{'CAR-10':0}})
   assert next(r for r in resolved['results'] if r['id']=='CAR-10')['participation']==350
   nav('היועץ החכם');expect(page.locator('.chat-messages')).to_contain_text('הנציג ענה')
   page.get_by_label('הודעה ליועץ').fill('מה יקרה אם אוותר על הרכב?')
   page.get_by_role('button',name='שליחת הודעה').click()
   expect(page.locator('.message.assistant').last).to_contain_text('אחזקת הרכב')
   page.screenshot(path=str(screenshots/'advisor.png'),full_page=True)
   # Changing employee exercises eligibility and all six data-driven scenarios.
   for e in data['employees']:
    page.get_by_label('בחירת עובד לדוגמה').select_option(e['id'])
    expect(page.locator('.profile-picker')).to_contain_text('קבוצת זכאות '+e['group'])
   page.get_by_label('בחירת עובד לדוגמה').select_option('E-002')
   page.get_by_label('הודעה ליועץ').fill('אני רוצה לוותר על הרכב')
   page.get_by_role('button',name='שליחת הודעה').click()
   expect(page.locator('.message.assistant').last).to_contain_text('כלי עבודה')
   nav('הסקירה שלי');page.wait_for_selector('.vehicles-grid')
   page.get_by_label('בחירת עובד לדוגמה').select_option('E-001')
   page.wait_for_timeout(400)
   nav('ניהול המערכת');page.wait_for_selector('.admin-page')
   for label in ['ספקי AI','מאגר רשות המסים','פרומפטים ותלושים','עלויות AI ושער דולר','מדיניות רכב','מס והפרשות','קטלוג רכבים','עיסוקים','עובדים','היסטוריה','סקירה']:
    page.get_by_role('tab',name=label,exact=True).click()
    assert not page.evaluate('document.documentElement.scrollWidth > innerWidth')
   page.screenshot(path=str(screenshots/'admin-desktop.png'),full_page=True)
   page.get_by_role('tab',name='עלויות AI ושער דולר',exact=True).click()
   expect(page.get_by_role('heading',name='יומן שימוש ועלויות')).to_be_visible()
   page.get_by_role('button',name='הוספת המודלים הנבחרים').click()
   page.get_by_label('gpt-4.1-mini inputUsdPerMillion',exact=True).fill('1')
   page.get_by_label('gpt-4.1-mini cachedInputUsdPerMillion',exact=True).fill('0.5')
   page.get_by_label('gpt-4.1-mini outputUsdPerMillion',exact=True).fill('2')
   page.get_by_label('מקור תעריף gpt-4.1-mini',exact=True).fill('TEST ONLY - not a live quote')
   page.get_by_label('סיבת שינוי הגדרות').fill('browser test pricing configuration')
   page.get_by_role('button',name='שמירה והפעלה',exact=True).click()
   expect(page.locator('.admin-success')).to_contain_text('נשמרו והופעלו')
   assert request('/admin/configuration')['configuration']['ai']['pricing'][0]['inputUsdPerMillion']==1
   page.screenshot(path=str(screenshots/'admin-costs.png'),full_page=True)
   saved=request('/admin/configuration')
   reverted=request('/admin/restore',{'expectedRevision':saved['revision'],'revision':admin_revision,'note':'restore after browser pricing test'})
   admin_revision=reverted['revision']
   page.reload();page.wait_for_selector('.vehicle-card');nav('ניהול המערכת')
   page.get_by_role('tab',name='פרומפטים ותלושים',exact=True).click()
   page.get_by_label('הנחיות לניסוח התשובה',exact=True).fill('TEST: use concise Hebrew wording')
   page.get_by_label('סיבת שינוי הגדרות').fill('browser managed prompt test')
   page.get_by_role('button',name='שמירה והפעלה',exact=True).click()
   expect(page.locator('.admin-success')).to_contain_text('נשמרו והופעלו')
   prompt_config=request('/admin/configuration');assert prompt_config['configuration']['ai']['narrationPrompt']=='TEST: use concise Hebrew wording'
   reverted=request('/admin/restore',{'expectedRevision':prompt_config['revision'],'revision':admin_revision,'note':'restore original prompt'})
   admin_revision=reverted['revision']
   page.reload();page.wait_for_selector('.vehicle-card');nav('ניהול המערכת')
   page.get_by_role('tab',name='מאגר רשות המסים',exact=True).click()
   page.get_by_role('row').filter(has_text='official-2027.csv').get_by_role('button',name='בדיקה לפני הפעלה').click()
   expect(page.get_by_role('heading',name='תוצאות בדיקה · שנת 2027')).to_be_visible()
   page.get_by_label('סיבת הפעלת מאגר').fill('browser activation 2027')
   page.get_by_role('button',name='הפעלת גרסת 2027',exact=True).click()
   expect(page.locator('.admin-success')).to_contain_text('הופעלה')
   assert request('/data')['rules']['valueOfUse']['taxYear']==2027
   page.get_by_role('row').filter(has_text='מאגר מקורי').get_by_role('button',name='בדיקה לפני הפעלה').click()
   page.get_by_label('סיבת הפעלת מאגר').fill('browser rollback baseline')
   page.get_by_role('button',name='הפעלת גרסת 2026',exact=True).click()
   expect(page.locator('.admin-success')).to_contain_text('הופעלה')
   admin_revision=request('/admin/configuration')['revision']
   page.screenshot(path=str(screenshots/'official-versions.png'),full_page=True)
   # A synthetic provider response tests employee review without sending any real document or paid request.
   def mock_data(route):
    response=route.fetch();body=response.json();body['payslipEnabled']=True;route.fulfill(response=response,json=body)
   page.route('**/api/data',mock_data)
   fields={key:{'value':None,'evidence':None} for key in ['salary','commission','bonus','pension','fund','credits','participation']}
   fields['salary']={'value':19000,'evidence':'שכר בסיס 19,000'};fields['pension']={'value':6,'evidence':'פנסיית עובד 6%'}
   page.route('**/api/payslip/extract?*',lambda route:route.fulfill(json={'fields':fields,'warnings':['Synthetic test response; employee review required.'],'configurationRevision':admin_revision}))
   page.reload();page.wait_for_selector('.vehicle-card');nav('הנתונים שלי')
   before_salary=page.get_by_label('שכר בסיס חודשי (₪)',exact=True).input_value()
   page.get_by_label('העלאת תלוש שכר').set_input_files({'name':'fictional-test-payslip.pdf','mimeType':'application/pdf','buffer':b'%PDF-1.7 FAKE TEST DOCUMENT ONLY'})
   expect(page.get_by_role('button',name='חילוץ נתונים מהתלוש',exact=True)).to_be_disabled()
   page.locator('.payslip-consent input').check();page.get_by_role('button',name='חילוץ נתונים מהתלוש',exact=True).click()
   expect(page.get_by_label('חילוץ שכר בסיס חודשי (₪)',exact=True)).to_have_value('19000')
   expect(page.get_by_label('שכר בסיס חודשי (₪)',exact=True)).to_have_value(before_salary)
   page.screenshot(path=str(screenshots/'payslip-review.png'),full_page=True)
   page.get_by_role('button',name='אישור והעברת השדות המסומנים לטופס').click()
   expect(page.get_by_label('שכר בסיס חודשי (₪)',exact=True)).to_have_value('19000')
   page.get_by_role('button',name='שמירה וחישוב מחדש',exact=True).click();page.wait_for_selector('.vehicle-card')
   assert request('/conversation/E-001')['employee']['salary']==19000
   nav('הנתונים שלי');page.get_by_role('button',name='איפוס לדוגמה').click();page.get_by_role('button',name='שמירה וחישוב מחדש').click();page.wait_for_selector('.vehicle-card')
   page.unroute('**/api/data',mock_data);page.unroute('**/api/payslip/extract?*')
   nav('הסקירה שלי')
   page.set_viewport_size({'width':390,'height':844});page.wait_for_timeout(350);page.screenshot(path=str(screenshots/'mobile.png'),full_page=True)
   assert not page.evaluate('document.documentElement.scrollWidth > innerWidth')
   page.get_by_role('button',name='פתיחת תפריט').click();nav('קטלוג הרכבים');expect(page.locator('.catalog-grid')).to_be_visible()
   page.get_by_role('button',name='פתיחת תפריט').click();nav('ניהול המערכת')
   page.wait_for_selector('.admin-page')
   page.get_by_role('tab',name='עלויות AI ושער דולר',exact=True).click()
   expect(page.get_by_role('heading',name='יומן שימוש ועלויות')).to_be_visible()
   assert not page.evaluate('document.documentElement.scrollWidth > innerWidth')
   page.wait_for_timeout(350)
   heading=page.locator('.admin-page h1').bounding_box();assert heading['x']>=0 and heading['x']+heading['width']<=390
   page.screenshot(path=str(screenshots/'admin-mobile.png'),full_page=True)
   assert not errors,errors
   browser.close()
  # Restart the API and confirm answer, participation and resumed conversation survived.
  stop(process);process=start(connection)
  assert request('/admin/configuration')['revision']==admin_revision
  t=request('/tickets');assert len(t)==1 and t[0]['status']=='resolved' and t[0]['participation']==350
  assert any('הנציג ענה' in m['text'] for m in request('/history/E-001'))
  print('PASS: salary edits, catalogue filtering, official keys, pending/resolved handoff, conversation resume, six employee profiles, waiver denial, desktop/mobile overflow, persisted restart; no browser errors.')
 finally:
  stop(process)
  subprocess.run(['docker','exec','carwise-postgres','dropdb','-U','carwise','--force',dbname],check=True)
