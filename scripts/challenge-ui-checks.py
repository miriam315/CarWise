"""Acceptance checks for a genuinely incomplete employee in the browser."""
from copy import deepcopy
import json,re,subprocess
from playwright.sync_api import expect

FIELDS=['salary','commission','bonus','pension','fund','credits','children','currentCar','participation']
def check(page,query,nav,screenshots,database=None):
 baseline=query('/admin/configuration');config=deepcopy(baseline['configuration']);config['ai']['enabled']=False
 employee={**config['employees'][0],'id':'E-CHALLENGE-UI','name':'עובד השלמת פרטים','knownFields':[field for field in FIELDS if field not in ('salary','bonus','currentCar','participation')],'advisorState':None}
 config['employees'].append(employee)
 next(car for car in config['cars'] if car['id']=='CAR-10')['participationKnown']=False
 query('/admin/configuration',{'expectedRevision':baseline['revision'],'configuration':config,'note':'synthetic browser test for missing fields'})
 original_storage=page.evaluate("localStorage.getItem('carwise-profile')")
 try:
  page.evaluate('(employee)=>localStorage.setItem("carwise-profile",JSON.stringify(employee))',employee)
  page.reload();expect(page.get_by_role('heading',name='נשלים רק את מה שחסר.')).to_be_visible()
  expect(page.locator('.stats-grid')).to_have_count(0)
  nav('הנתונים שלי')
  expect(page.get_by_label('שכר בסיס חודשי (₪)',exact=True)).to_have_value('')
  expect(page.get_by_label('בונוס חודשי (₪)',exact=True)).to_have_value('')
  expect(page.get_by_label('השתתפות נוכחית (₪)',exact=True)).to_have_value('')
  assert not page.locator('.profile-form').evaluate('(form)=>form.checkValidity()')
  page.get_by_role('button',name='השלמת נתונים בשיחה',exact=True).click()
  expect(page.locator('.chat-messages')).to_contain_text('מה שכר הבסיס החודשי')
  def send(text):
   page.get_by_label('הודעה ליועץ').fill(text);page.get_by_role('button',name='שליחת הודעה',exact=True).click()
   expect(page.locator('.typing')).to_have_count(0)
  send('19000.25');expect(page.locator('.chat-messages')).to_contain_text('מה סכום הבונוס החודשי')
  page.reload();expect(page.get_by_role('heading',name='נשלים רק את מה שחסר.')).to_be_visible();nav('היועץ החכם')
  send('0');expect(page.locator('.chat-messages')).to_contain_text('איזה רכב יש לך היום')
  page.reload();expect(page.get_by_role('heading',name='נשלים רק את מה שחסר.')).to_be_visible();nav('היועץ החכם')
  page.locator('.candidate-list').last.get_by_role('button',name='COROLLA CROSS',exact=False).click()
  expect(page.locator('.chat-messages')).to_contain_text('מה ההשתתפות החודשית שלך ברכב הנוכחי')
  send('350')
  expect(page.get_by_role('heading',name='נשלים רק את מה שחסר.')).to_have_count(0)
  nav('הנתונים שלי');expect(page.get_by_label('שכר בסיס חודשי (₪)',exact=True)).to_have_value('19000.25')
  expect(page.get_by_label('בונוס חודשי (₪)',exact=True)).to_have_value('0')
  page.screenshot(path=str(screenshots/'challenge-intake-complete.png'),full_page=True)
  nav('הסקירה שלי');expect(page.locator('.stats-grid')).to_be_visible()
  saved=query('/conversation/'+employee['id'])['employee'];assert saved['salary']==19000.25 and saved['bonus']==0 and saved['advisorState']['awaitingField'] is None
  nav('קטלוג הרכבים');page.get_by_label('חיפוש רכב').fill('ATTO')
  page.get_by_role('button',name='לבדיקת החלופה',exact=True).click()
  expect(page.get_by_role('button',name='הפנייה ממתינה לנציג',exact=True)).to_be_disabled()
  tickets=[ticket for ticket in query('/tickets') if json.loads(ticket['employee'])['id']==employee['id']]
  assert len(tickets)==1 and tickets[0]['status']=='pending'
  expect(page.get_by_role('dialog').locator('.detail-total strong')).to_contain_text('ממתין')
  query('/tickets/'+tickets[0]['id'],{'answer':'אישור השתתפות לבדיקת הממשק','participation':300})
  expect(page.get_by_role('dialog').locator('.detail-total strong')).not_to_contain_text('ממתין',timeout=15000)
  assert page.get_by_role('dialog').locator('.detail-total strong').inner_text().strip()!='0 ₪'
  page.get_by_role('button',name='סגירת פירוט',exact=True).click()

 finally:
  latest=query('/admin/configuration');query('/admin/restore',{'expectedRevision':latest['revision'],'revision':baseline['revision'],'note':'restore after browser intake test'})
  page.evaluate('(value)=>value===null?localStorage.removeItem("carwise-profile"):localStorage.setItem("carwise-profile",value)',original_storage)
  page.reload();page.wait_for_selector('.vehicle-card')
  if database is not None:
   assert re.fullmatch(r'carwise_[a-z_0-9]+',database)
   sql="DELETE FROM carwise_messages WHERE employee_id='E-CHALLENGE-UI'; DELETE FROM carwise_conversations WHERE employee_id='E-CHALLENGE-UI'; DELETE FROM carwise_tickets WHERE employee_id='E-CHALLENGE-UI';"
   subprocess.run(['docker','exec','carwise-postgres','psql','-U','carwise','-d',database,'-v','ON_ERROR_STOP=1','-c',sql],check=True,stdout=subprocess.DEVNULL)

 print('PASS: incomplete employee hides financial results, unknown form fields remain blank, short answers and explicit zero, reload resumes the saved question, current-car confirmation and completed dashboard/profile.')
