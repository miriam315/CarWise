"""Isolated end-to-end smoke test. Requires playwright and Chromium."""
from pathlib import Path
import os, sys, subprocess, tempfile, time, json, urllib.request, shutil
from playwright.sync_api import sync_playwright, expect
ROOT=Path(__file__).resolve().parent.parent
BASE='http://127.0.0.1:8001'
def request(path,body=None):
 data=None if body is None else json.dumps(body).encode()
 with urllib.request.urlopen(urllib.request.Request(BASE+'/api'+path,data=data,headers={'Content-Type':'application/json'})) as r:return json.load(r)
def start(state):
 process=subprocess.Popen([sys.executable,'server/app.py'],cwd=ROOT,env={**os.environ,'CARWISE_PORT':'8001','CARWISE_STATE_DIR':str(state)},stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
 for _ in range(50):
  try:request('/health');return process
  except Exception:time.sleep(.1)
 process.terminate();raise RuntimeError('Test server did not start')
def stop(process):process.terminate();process.wait(timeout=5)
with tempfile.TemporaryDirectory(prefix='carwise-smoke-') as temp:
 process=start(temp)
 try:
  data=request('/data');assert data['officialCount']==77988
  employee=data['employees'][0]
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
   page.screenshot(path=str(screenshots/'desktop.png'),full_page=True)
   def nav(name):page.locator('.sidebar nav').get_by_role('button',name=name,exact=False).click()
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
   assert request('/tickets',{'employee':employee,'target':'CAR-10'})['id']==ticket_id
   assert len(request('/tickets'))==1
   nav('הסקירה שלי');expect(page.locator('.insight-banner')).to_contain_text('מחכים לתשובת הנציג')
   page.reload();page.wait_for_selector('.insight-banner');expect(page.locator('.insight-banner')).to_contain_text('מחכים לתשובת הנציג')
   nav('מרכז הנציגים');expect(page.locator('.ticket-card')).to_have_count(1)
   page.get_by_label('השתתפות עובד מאושרת (₪)').fill('350')
   page.get_by_label('תשובת הנציג').fill('אושר: 350 ₪ לחודש, בתוקף מיידי.')
   page.get_by_role('button',name='שליחת תשובה והמשך השיחה').click()
   expect(page.locator('.resolved-answer')).to_contain_text('350')
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
   page.set_viewport_size({'width':390,'height':844});page.wait_for_timeout(350);page.screenshot(path=str(screenshots/'mobile.png'),full_page=True)
   assert not page.evaluate('document.documentElement.scrollWidth > innerWidth')
   page.get_by_role('button',name='פתיחת תפריט').click();nav('קטלוג הרכבים');expect(page.locator('.catalog-grid')).to_be_visible()
   assert not errors,errors
   browser.close()
  # Restart the API and confirm answer, participation and resumed conversation survived.
  stop(process);process=start(temp)
  t=request('/tickets');assert len(t)==1 and t[0]['status']=='resolved' and t[0]['participation']==350
  assert any('הנציג ענה' in m['text'] for m in request('/history/E-001'))
  print('PASS: salary edits, catalogue filtering, official keys, pending/resolved handoff, conversation resume, six employee profiles, waiver denial, desktop/mobile overflow, persisted restart; no browser errors.')
 finally:stop(process)
