"""Real API/browser acceptance for six decision features. Synthetic payroll only."""
from copy import deepcopy
from pathlib import Path
from zipfile import ZipFile
import json,time,urllib.error
from playwright.sync_api import expect
from redacted_payslip_fixture import configure,prepare,extract,mock

LABELS={'salary':'שכר בסיס חודשי (₪)','commission':'עמלות חודשיות (₪)','bonus':'בונוס חודשי (₪)','pension':'פנסיית עובד (%)','fund':'קרן השתלמות עובד (%)','credits':'נקודות זיכוי','participation':'השתתפות עובד ברכב (₪)'}

def check(page,query,nav,screenshots,all_browser_files=True):
 root=Path(__file__).resolve().parent.parent
 baseline=query('/admin/configuration');configuration=deepcopy(baseline['configuration'])
 keys=configure(configuration)
 configuration=query('/admin/configuration',{'expectedRevision':baseline['revision'],'configuration':configuration,'apiKeys':keys,'note':'six-feature synthetic acceptance with mocked document provider'})['configuration']
 employee=query('/data')['employees'][0];conversation=query('/conversation/'+employee['id']);employee=(conversation or {}).get('employee') or employee
 query('/profile',{'employee':employee})
 before_profile=deepcopy(query('/conversation/'+employee['id'])['employee']);before_history=query('/history/'+employee['id'])
 posts=[];external=[];browser_errors=[]
 def on_request(request):
  if request.method=='POST' and '/payslip/extract' in request.url:posts.append(request.url)
  if request.url.startswith('http') and not request.url.startswith(page.url.split('/')[0]+'//'+page.url.split('/')[2]):external.append(request.url)
 def on_error(error):browser_errors.append(error.stack or str(error))
 page.on('request',on_request);page.on('pageerror',on_error)
 evidence={'browserFiles':[],'conditionalBoundary':None,'externalRequests':[],'documentUploads':0,'passed':False}
 try:
  original=query('/simulate',{'employee':employee})
  result=query('/decision/analyze',{'employee':employee,'target':'CAR-10','axis':'participation','rangeMaximum':2000})
  threshold=result['boundary']['maximumWithoutLoss'];assert threshold is not None
  low=query('/decision/analyze',{'employee':employee,'target':'CAR-10','hypotheticalParticipation':threshold,'axis':'participation','rangeMaximum':2000})
  high=query('/decision/analyze',{'employee':employee,'target':'CAR-10','hypotheticalParticipation':threshold+.01,'axis':'participation','rangeMaximum':2000})
  assert low['outcome']['delta']>=0 and high['outcome']['delta']<0
  assert len(low['points'])==11 and low['conditional'] and low['profileUnchanged']
  evidence['conditionalBoundary']=result['boundary']
  assert query('/conversation/'+employee['id'])['employee']==before_profile
  assert query('/history/'+employee['id'])==before_history
  state=query('/decision/state/'+employee['id']);assert not state['months']
  saved=query('/decision/choice',{'employee':employee,'target':'CAR-10','expectedRevision':state['revision']})
  assert saved['choice']['employee']['name']=='' and saved['choice']['employee']['question']==''
  old=query('/decision/state/'+employee['id']);assert old['change']['impact']==0
  # Unknown participation has useful conditional information but no final net or saved choice.
  current=query('/admin/configuration');changed=deepcopy(current['configuration'])
  for c in changed['cars']:
   if c['id']=='CAR-10':c['participationKnown']=False
  query('/admin/configuration',{'expectedRevision':current['revision'],'configuration':changed,'note':'unknown participation acceptance'})
  pending=query('/simulate',{'employee':employee});alt=next(r for r in pending['results'] if r['id']=='CAR-10')
  assert alt['pending'] and alt['net'] is None
  conditional=query('/decision/analyze',{'employee':employee,'target':'CAR-10','axis':'participation','rangeMaximum':2000})
  assert conditional['conditional'] and conditional['outcome'] is None and conditional['boundary']
  try:query('/decision/choice',{'employee':employee,'target':'CAR-10','expectedRevision':saved['revision']});raise AssertionError('pending choice accepted')
  except urllib.error.HTTPError as error:assert error.code==400
  current=query('/admin/configuration');query('/admin/configuration',{'expectedRevision':current['revision'],'configuration':configuration,'note':'restore known amounts'})
  # Change an approved active rule. Same salary assumptions, different policy result.
  current=query('/admin/configuration');changed=deepcopy(configuration);changed['rules']['electric']['netBonus']+=100
  query('/admin/configuration',{'expectedRevision':current['revision'],'configuration':changed,'note':'synthetic policy delta'})
  updated=query('/decision/state/'+employee['id']);assert updated['change']['policyChanged']
  assert abs(updated['change']['impact']-100)<.000001,updated['change']
  assert updated['choice']['chosen']['net']==old['choice']['chosen']['net'] # snapshot immutable
  current=query('/admin/configuration');query('/admin/configuration',{'expectedRevision':current['revision'],'configuration':configuration,'note':'restore for browser OCR'})
  page.reload();page.wait_for_selector('.vehicle-card');nav('הנתונים שלי')
  expect(page.get_by_label('מיקום עיבוד התלוש')).to_have_count(0)
  # The new UI prepares image-only redacted copies; provider responses are controlled fixtures, not accuracy claims.
  values={'salary':18000,'commission':0,'bonus':0,'pension':6,'fund':2.5,'credits':2.25,'participation':350}
  uploaded=[];handler=mock(page,lambda:values,uploaded)
  with ZipFile(root/'examples/payslips/complex/CarWise-complex-demo-payslips.zip') as archive:
   names=[n for n in archive.namelist() if n.endswith(('01-complex-monthly.pdf','02-complex-sales.pdf')) and '-scanned' not in n]
   page.get_by_label('העלאת תלוש שכר').set_input_files([{'name':Path(n).name,'mimeType':'application/pdf','buffer':archive.read(n)} for n in sorted(names)])
   expect(page.locator('.payslip-queue')).to_contain_text('עוד 1 תלושים')
   assert not uploaded
   for index,period in enumerate(['2026-08','2026-09']):
    values['bonus']=0 if index==0 else 1500
    prepare(page);extract(page)
    select_all=page.get_by_label('בחר הכול — אישור שדות התלוש',exact=True);select_all.check()
    for label in LABELS.values():expect(page.get_by_label('אישור '+label,exact=True)).to_be_checked()
    if index==1:expect(page.locator('.month-differences')).to_contain_text('בונוס חודשי (₪) השתנה')
    page.get_by_label('חודש התלוש',exact=True).fill(period)
    page.get_by_label('סוג הבונוס',exact=True).select_option('none' if index==0 else 'oneOff')
    page.get_by_role('button',name='שמירת חודש מאושר להשוואה',exact=True).click()
    expect(page.locator('.payslip-month-save [role=status]')).to_contain_text('הפרופיל הנוכחי לא השתנה')
    if index==0:page.get_by_role('button',name='מעבר לתלוש הבא').click()
  assert len(uploaded)==2 and len(posts)==2 and not external
  page.unroute('**/api/payslip/extract?**',handler)
  evidence['reviewedRedactedCopies']=2;evidence['provider']='mocked, no paid AI or extraction accuracy claim'
  persisted=query('/decision/state/'+employee['id']);assert len(persisted['months'])==2
  assert query('/conversation/'+employee['id'])['employee']==before_profile
  assert query('/history/'+employee['id'])==before_history
  assert persisted['months'][1]['regular']['employee']['bonus']==0
  assert persisted['months'][1]['actual']['employee']['bonus']==1500
  # The source filename, document bytes, source boxes and raw OCR text are not saved.
  assert 'complex-monthly.pdf' not in json.dumps(persisted) and 'regions' not in json.dumps(persisted)
  nav('מעבדת ההחלטה');expect(page.get_by_label('החלופה שנבדקת').get_by_role('option',name='CARNIVAL',exact=False)).to_have_attribute('disabled','')
  evidence['ineligibleOptionsDisabled']=True
  page.get_by_role('tab',name='חודש רגיל מול חריג',exact=True).click()
  expect(page.locator('.decision-months')).to_contain_text('2026-08');expect(page.locator('.decision-months')).to_contain_text('ללא הבונוס החד־פעמי')
  page.screenshot(path=str(screenshots/'decision-months.png'),full_page=True)
  page.get_by_role('tab',name='מעבדת הנטו',exact=True).click()
  expect(page.locator('.decision-chart svg')).to_be_visible();expect(page.locator('.decision-outcome')).to_contain_text('התוצאה בתנאים שבחרת')
  page.get_by_label('השתתפות היפותטית',exact=True).fill(str(threshold+100));expect(page.locator('.decision-outcome')).to_contain_text('תרחיש מותנה בלבד')
  assert query('/conversation/'+employee['id'])['employee']==before_profile
  page.screenshot(path=str(screenshots/'decision-laboratory.png'),full_page=True)
  page.set_viewport_size({'width':390,'height':844});page.wait_for_function("document.querySelector('.sidebar').getBoundingClientRect().left >= innerWidth - 1");assert not page.evaluate('document.documentElement.scrollWidth>innerWidth')
  page.screenshot(path=str(screenshots/'decision-laboratory-mobile.png'),full_page=True);page.set_viewport_size({'width':1440,'height':1100})
  page.reload();page.wait_for_selector('.vehicle-card');nav('מעבדת ההחלטה');page.get_by_role('tab',name='מה השתנה מאז שבחרתי?',exact=True).click()
  expect(page.locator('.decision-watch')).to_contain_text('בחירה שמורה')
  page.get_by_role('tab',name='חודש רגיל מול חריג',exact=True).click();expect(page.locator('.decision-months')).to_contain_text('2026-09')
  page.get_by_role('button',name='הסרת חודש 2026-08',exact=True).click();expect(page.locator('.decision-months .conditional-insight')).to_have_count(1)
  assert len(query('/decision/state/'+employee['id'])['months'])==1
  # The active page detects a real policy revision without a manual page reload.
  current=query('/admin/configuration');changed=deepcopy(current['configuration']);changed['rules']['electric']['netBonus']+=100
  query('/admin/configuration',{'expectedRevision':current['revision'],'configuration':changed,'note':'automatic saved-choice notification acceptance'})
  expect(page.get_by_label('שינוי בבחירה השמורה',exact=True)).to_contain_text('100',timeout=25000)
  page.get_by_label('שינוי בבחירה השמורה',exact=True).get_by_role('button').click()
  expect(page.get_by_role('tab',name='מה השתנה מאז שבחרתי?',exact=True)).to_have_attribute('aria-selected','true')
  evidence['automaticActivePolicyNotification']=True
  assert not browser_errors,browser_errors
  evidence['externalRequests']=external;evidence['documentUploads']=len(posts);evidence['passed']=True
  (screenshots.parent/'innovation-verification.json').write_text(json.dumps(evidence,ensure_ascii=False,indent=2)+'\n')
 finally:
  evidence['browserErrors']=browser_errors
  (screenshots.parent/'innovation-verification.json').write_text(json.dumps(evidence,ensure_ascii=False,indent=2)+'\n')
  page.remove_listener('request',on_request);page.remove_listener('pageerror',on_error)
  page.set_viewport_size({'width':1440,'height':1100})
  current=query('/admin/configuration');query('/admin/restore',{'expectedRevision':current['revision'],'revision':baseline['revision'],'note':'restore after six-feature verification'})
 print('PASS: conditional exact boundary, pending values remain null, immutable choice and active-policy recalculation; multi-month explicit review/classification and persistence/removal; locally prepared reviewed image-only documents with mocked AI responses; scenario chart without profile/history writes; mobile and reload.')
