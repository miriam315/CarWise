"""Opt-in aggregate research acceptance. Only run against an isolated test server."""
from copy import deepcopy
import json,urllib.error
from playwright.sync_api import expect

def check(query,page=None,nav=None,screenshots=None):
 original=query('/admin/configuration');config=deepcopy(original['configuration'])
 config['intentAnalytics']={'enabled':True,'minimumCohort':5,'retentionDays':90,'aiInsightsEnabled':True,'prompt':'Select only a supplied aggregate insight id.'}
 config['ai']['enabled']=False
 if config['ai'].get('models'):config['ai']['serviceModels']['insights']=config['ai']['serviceModels']['narration']
 people=[{**config['employees'][0],'id':'INTENT-TEST-'+str(i),'name':'עובד מחקר סינתטי '+str(i),'salary':18000+i*100,'advisorState':None} for i in range(12)]
 config['employees']+=people
 def save(configuration):
  current=query('/admin/configuration');query('/admin/configuration',{'expectedRevision':current['revision'],'configuration':configuration,'note':'Isolated opt-in aggregate research acceptance'})
 def reject(path,body=None,code=400):
  try:query(path,body);raise AssertionError('Unexpected acceptance '+path)
  except urllib.error.HTTPError as e:assert e.code==code,(path,e.code)
 def cohort():return next(c for c in query('/admin/intent/report')['cohorts'] if c['category']=='electric')
 save(config)
 try:
  query('/intent/explore',{'employeeId':people[0]['id'],'target':'CAR-10'})
  assert cohort()['suppressed'] # No capture before worker consent.
  reject('/intent/feedback',{'employeeId':people[0]['id'],'target':'CAR-10','reason':'net'})
  for e in people:
   query('/intent/preference',{'employeeId':e['id'],'consent':True})
   for _ in range(3):query('/intent/explore',{'employeeId':e['id'],'target':'CAR-10'})
  assert cohort()['explored']==12 and cohort()['savedChoice']==0 and cohort()['unselectedAfterSevenDays']==0
  for e in people[:6]:
   state=query('/decision/state/'+e['id']);query('/decision/choice',{'employee':e,'target':'CAR-10','expectedRevision':state['revision']})
   query('/intent/feedback',{'employeeId':e['id'],'target':'CAR-10','reason':'net'})
  observed=cohort();assert observed['explored']==12 and observed['savedChoice']==6 and observed['feedback']['net']==6
  report=query('/admin/intent/report');raw=json.dumps(report,ensure_ascii=False)
  assert all(e['id'] not in raw and e['name'] not in raw for e in people) and '"salary"' not in raw and '"events"' not in raw
  reject('/intent/feedback',{'employeeId':people[0]['id'],'target':'CAR-10','reason':'arbitrary personal text'})
  reject('/admin/intent/report?days=7')
  query('/intent/explore',{'employeeId':people[0]['id'],'target':'waiver'})
  waiver=next(c for c in query('/admin/intent/report')['cohorts'] if c['category']=='waiver');assert waiver['suppressed'] and waiver['explored'] is None
  scenario={'category':'electric','additionalBenefit':200,'days':30,'assumedAdoptionPercent':100,'monthlyOperatingSaving':900,'monthlyGrantCost':500,'oneOffCost':1000}
  before_config=query('/admin/configuration')['revision'];before_profiles=query('/data')['employees'];before_cohorts=query('/admin/intent/report')['cohorts']
  calculated=query('/admin/intent/scenario',scenario);assert calculated['interested']==6 and len(calculated['curve'])==5
  assert calculated['planning']['assumedAdopters']==6 and calculated['planning']['annualSampleNet']==22800
  assert 'לא תחזית' in calculated['planning']['label']
  reject('/admin/intent/scenario',{**scenario,'monthlyGrantCost':0});reject('/admin/intent/scenario',{**scenario,'additionalBenefit':-1})
  demo=query('/admin/intent/demo',{'category':'electric','additionalBenefit':400,'days':30,'assumedAdoptionPercent':50,'monthlyOperatingSaving':900,'monthlyGrantCost':500,'oneOffCost':1000})
  assert demo['synthetic'] and demo['report']['synthetic'] and 'נתונים סינתטיים' in demo['scenario']['explanation']
  assert query('/admin/configuration')['revision']==before_config and query('/data')['employees']==before_profiles and query('/admin/intent/report')['cohorts']==before_cohorts
  insight=query('/admin/intent/prioritize',{'days':30,'synthetic':False});assert not insight['usedAi'] and insight['insight']['id'] in [i['id'] for i in report['insights']]
  query('/intent/preference',{'employeeId':people[0]['id'],'consent':False})
  assert not query('/intent/preference/'+people[0]['id'])['consented'] and cohort()['explored']==11
  if page is not None:
   page.reload();page.wait_for_selector('.vehicle-card');page.get_by_label('בחירת עובד לדוגמה').select_option(people[0]['id']);nav('הנתונים שלי')
   page.get_by_role('button',name='אני מסכים/ה לשיתוף המצומצם שתואר',exact=True).click()
   expect(page.locator('.intent-privacy')).to_contain_text('הסכמה לשיתוף פעילה')
   nav('קטלוג הרכבים')
   card=page.locator('.catalog-card').filter(has=page.get_by_role('heading',name='ATTO 3',exact=True))
   card.get_by_role('button',name='לבדיקת החלופה',exact=False).click()
   page.locator('.intent-feedback').get_by_role('button',name='הפער בנטו',exact=True).click()
   expect(page.locator('.intent-feedback')).to_contain_text('המשוב נשמר למחקר המצרפי')
   page.get_by_role('button',name='סגירת פירוט',exact=True).click();nav('הנתונים שלי')
   page.get_by_role('button',name='ביטול השיתוף ומחיקת היסטוריית המחקר שלי',exact=True).click()
   expect(page.locator('.intent-privacy')).not_to_contain_text('הסכמה לשיתוף פעילה')
   assert cohort()['explored']==11
   nav('ניהול המערכת');page.get_by_role('tab',name='תובנות וכוונות',exact=True).click()
   expect(page.get_by_label('מספר עובדים מינימלי לפרסום קבוצה')).to_have_value('5')
   page.get_by_role('button',name='הדגמה סינתטית לשופטים',exact=True).click()
   expect(page.locator('.intent-panel>.intent-demo-banner')).to_contain_text('אינם נשמרים')
   expect(page.locator('.intent-scenario')).to_contain_text('אין כאן ניבוי התנהגות')
   expect(page.locator('.intent-curve .intent-bar')).to_have_count(5)
   page.screenshot(path=str(screenshots/'intent-analytics-desktop.png'),full_page=True)
   page.get_by_role('button',name='הצגת תובנה ניהולית',exact=True).click()
   dialog=page.get_by_role('dialog',name='תובנה ניהולית',exact=True);expect(dialog).to_contain_text('הדגמה סינתטית בלבד');expect(dialog).to_contain_text('לא בוצע תעדוף AI מוצלח')
   page.get_by_role('button',name='סגירת תובנה',exact=True).click()
   page.get_by_label('חלון דיווח כוונות',exact=True).select_option('90')
   expect(page.locator('.intent-cohorts .intent-card').first).to_contain_text('אין די נתונים')
   page.get_by_label('חלון דיווח כוונות',exact=True).select_option('30')
   expect(page.locator('.intent-cohorts .intent-card').first).to_contain_text('100%')
   page.set_viewport_size({'width':390,'height':844});page.wait_for_function("document.querySelector('.sidebar').getBoundingClientRect().left >= innerWidth - 1")
   assert not page.evaluate('document.documentElement.scrollWidth>innerWidth')
   page.screenshot(path=str(screenshots/'intent-analytics-mobile.png'),full_page=True);page.set_viewport_size({'width':1440,'height':1100})
   assert query('/admin/configuration')['revision']==before_config
  print('PASS: opt-in research, daily deduplication, saved-choice semantics, explicit feedback, cohort/complement suppression, no individual payroll disclosure, exact policy what-if, labelled planning assumptions, synthetic demo isolation, controlled insight selection and consent withdrawal.',flush=True)
 finally:
  save(original['configuration']);query('/admin/intent/purge',{})
  if page is not None:page.reload();page.wait_for_selector('.vehicle-card')
