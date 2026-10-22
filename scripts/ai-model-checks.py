"""Browser checks with fictional keys; never invokes a paid provider."""
import json
from playwright.sync_api import expect

def check(page,query,nav,screenshots):
 baseline=query('/admin/configuration')
 nav('ניהול המערכת');page.get_by_role('tab',name='ספקי AI',exact=True).click()
 page.get_by_role('button',name='הוספת מודל',exact=True).click()
 page.get_by_label('שם המודל בקטלוג',exact=True).fill('PDF browser test')
 editor=page.locator('.ai-model-editor')
 editor.locator('input[aria-label^="מודל "]').fill('test-document-model')
 editor.locator('input[type="password"]').first.fill('TEST-ONLY-PDF-PRIMARY')
 page.get_by_label('תמיכת המודל במסמכים').check()
 page.get_by_role('button',name='הוספת מפתח גיבוי',exact=True).click()
 page.get_by_label('שם גיבוי 1',exact=True).fill('Backup account A')
 page.get_by_label('מפתח גיבוי 1',exact=True).fill('TEST-ONLY-PDF-BACKUP-A')
 page.get_by_role('button',name='הוספת מפתח גיבוי',exact=True).click()
 page.get_by_label('מפתח גיבוי 2',exact=True).fill('TEST-ONLY-PDF-BACKUP-B')
 page.get_by_label('הפעלת גיבוי 2',exact=True).uncheck()
 # Connection testing must use unsaved keys and the exact values visible in the editor.
 probes=[]
 def probe(route):
  probes.append(route.request.post_data_json)
  route.fulfill(status=200,content_type='application/json',body=json.dumps({'success':True,'code':'ok','message':'בדיקת טיוטה תקינה'}))
 page.route('**/api/admin/test-ai',probe)
 page.get_by_label('מגבלת טוקנים לפלט',exact=True).fill('8192')
 page.get_by_label('זמן המתנה כולל (שניות)',exact=True).fill('60')
 page.get_by_role('button',name='בדיקת חיבור עם גיבוי',exact=True).click()
 expect(page.locator('.admin-test')).to_contain_text('בדיקת טיוטה תקינה')
 assert probes[-1]['profile']['maxOutputTokens']==8192 and probes[-1]['profile']['timeoutSeconds']==60
 assert probes[-1]['apiKeys']['primary']=='TEST-ONLY-PDF-PRIMARY'
 assert len(probes[-1]['apiKeys'])==3 and 'apiKey' not in probes[-1]
 page.get_by_label('זמן המתנה כולל (שניות)',exact=True).fill('91')
 expect(page.locator('.admin-test')).to_have_count(0)
 page.get_by_role('button',name='בדיקת מפתח ראשי',exact=True).click()
 expect(page.locator('.admin-test')).to_contain_text('91')
 assert len(probes)==1 # Invalid limits are caught before a provider request.
 page.get_by_label('זמן המתנה כולל (שניות)',exact=True).fill('90')
 page.get_by_label('מגבלת טוקנים לפלט',exact=True).fill('128')
 page.get_by_role('button',name='בדיקת מפתח ראשי',exact=True).click()
 expect(page.locator('.admin-test')).to_contain_text('בדיקת טיוטה תקינה')
 assert probes[-1]['apiKey']=='TEST-ONLY-PDF-PRIMARY' and 'apiKeys' not in probes[-1]
 assert probes[-1]['profile']['maxOutputTokens']==128 and probes[-1]['profile']['timeoutSeconds']==90
 # A hidden invalid model must be named when saving another model with valid values.
 page.get_by_label('מגבלת טוקנים לפלט',exact=True).fill('127')
 page.locator('.ai-model-list button').first.click()
 page.get_by_role('button',name='שמירת הגדרות',exact=True).click()
 expect(page.locator('.admin-page .error-banner')).to_contain_text('PDF browser test')
 expect(page.locator('.admin-page .error-banner')).to_contain_text('127')
 page.locator('.ai-model-list button').filter(has_text='PDF browser test').click()
 page.get_by_label('מגבלת טוקנים לפלט',exact=True).fill('1200')
 page.get_by_label('זמן המתנה כולל (שניות)',exact=True).fill('35')
 # A late response for an edited profile must never appear as a result for the new settings.
 page.unroute('**/api/admin/test-ai',probe)
 held=[]
 page.route('**/api/admin/test-ai',lambda route:held.append(route))
 page.get_by_role('button',name='בדיקת חיבור עם גיבוי',exact=True).click()
 expect(page.get_by_role('button',name='בודקים…',exact=True)).to_be_visible()
 page.get_by_label('מגבלת טוקנים לפלט',exact=True).fill('1400')
 assert held
 held[0].fulfill(status=200,content_type='application/json',body=json.dumps({'success':False,'code':'old_failure','message':'obsolete test result'}))
 expect(page.get_by_role('button',name='בדיקת חיבור עם גיבוי',exact=True)).to_be_enabled()
 expect(page.locator('.admin-test')).to_have_count(0)
 page.unroute('**/api/admin/test-ai')
 page.get_by_label('מגבלת טוקנים לפלט',exact=True).fill('1200')
 pdf_id=page.get_by_label('מודל לשירות narration',exact=True).locator('option').filter(has_text='PDF browser test').get_attribute('value')
 page.get_by_label('מודל לשירות narration',exact=True).select_option(pdf_id)
 page.get_by_label('מודל לשירות payslip',exact=True).select_option(pdf_id)
 expect(page.get_by_role('button',name='הסרת מודל',exact=True)).to_be_disabled()
 expect(page.get_by_label('סיבת שינוי הגדרות')).to_have_count(0)
 page.get_by_role('button',name='שמירת הגדרות',exact=True).click();expect(page.locator('.admin-success')).to_contain_text('הגדרות ה־AI נשמרו והופעלו')
 saved=query('/admin/configuration');ai=saved['configuration']['ai']
 assert saved['policyRevision']==baseline['policyRevision'] and saved['history']==baseline['history']
 assert saved['operations'][0]['aiChanged']
 assert len(ai['models'])==3 and ai['serviceModels']['narration']==pdf_id
 pdf=next(m for m in ai['models'] if m['id']==pdf_id)
 assert pdf['profile']['supportsDocuments'] and len(pdf['profile']['backupKeys'])==2
 assert not pdf['profile']['backupKeys'][1]['enabled']
 assert all(k['configured'] for k in saved['keyPools'][pdf_id])
 assert 'TEST-ONLY-PDF-' not in json.dumps(saved)
 page.reload();page.wait_for_selector('.vehicle-card');nav('ניהול המערכת');page.get_by_role('tab',name='ספקי AI',exact=True).click()
 expect(page.get_by_label('מודל לשירות narration',exact=True)).to_have_value(pdf_id)
 page.locator('.ai-model-list button').filter(has_text='PDF browser test').click()
 expect(page.get_by_label('מפתח גיבוי 1',exact=True)).to_have_value('')
 expect(page.get_by_label('הפעלת גיבוי 2',exact=True)).not_to_be_checked()
 # Switch two services to the same existing model, leaving the document model independent.
 extraction_id=ai['serviceModels']['extraction']
 page.get_by_label('מודל לשירות narration',exact=True).select_option(extraction_id)
 expect(page.get_by_label('סיבת שינוי הגדרות')).to_have_count(0)
 page.get_by_role('button',name='שמירת הגדרות',exact=True).click();expect(page.locator('.admin-success')).to_contain_text('הגדרות ה־AI נשמרו והופעלו')
 saved=query('/admin/configuration');assert saved['configuration']['ai']['serviceModels']['narration']==extraction_id
 page.screenshot(path=str(screenshots/'ai-models-desktop.png'),full_page=True)
 page.set_viewport_size({'width':390,'height':844})
 page.wait_for_timeout(300) # Let the existing sidebar CSS transition finish before the capture.
 assert not page.evaluate('document.documentElement.scrollWidth > innerWidth')
 page.screenshot(path=str(screenshots/'ai-models-mobile.png'),full_page=True)
 page.set_viewport_size({'width':1440,'height':1100})
 # Payslip activation is independent from conversation AI and uses the same readiness in both UIs.
 page.get_by_role('tab',name='פרומפטים ותלושים',exact=True).click()
 page.get_by_label('הפעלת חילוץ תלושים').check()
 expect(page.get_by_text('השינוי עדיין בטיוטה.',exact=False)).to_be_visible()
 expect(page.get_by_label('סיבת שינוי הגדרות')).to_have_count(0)
 page.get_by_role('button',name='שמירת הגדרות',exact=True).click();expect(page.locator('.admin-success')).to_contain_text('הגדרות ה־AI נשמרו והופעלו')
 assert query('/admin/configuration')['configuration']['ai']['enabled'] is False
 assert query('/data')['payslipEnabled'] and query('/payslip/status')['code']=='ready'
 nav('הנתונים שלי');expect(page.get_by_label('העלאת תלוש שכר')).to_have_count(1)
 nav('ניהול המערכת');page.get_by_role('tab',name='פרומפטים ותלושים',exact=True).click()
 page.get_by_label('הפעלת חילוץ תלושים').uncheck();expect(page.get_by_label('סיבת שינוי הגדרות')).to_have_count(0)
 page.get_by_role('button',name='שמירת הגדרות',exact=True).click();expect(page.locator('.admin-success')).to_contain_text('הגדרות ה־AI נשמרו והופעלו')
 nav('הנתונים שלי');expect(page.get_by_text('חילוץ תלושים אינו זמין כרגע.',exact=False)).to_be_visible()
 expect(page.get_by_label('העלאת תלוש שכר')).to_have_count(1)
 expect(page.get_by_role('button',name='צילום תלוש',exact=True)).to_be_enabled()
 expect(page.get_by_role('button',name='חילוץ נתונים מהתלוש',exact=True)).to_be_disabled()
 # Simulate a saved administrator change while the worker's page is already open.
 latest=query('/admin/configuration');latest['configuration']['ai']['payslipEnabled']=True
 saved=query('/admin/configuration',{'expectedRevision':latest['revision'],'configuration':latest['configuration'],'note':'another administrator enables documents'})
 page.get_by_role('button',name='בדיקת זמינות מחדש',exact=True).click();expect(page.get_by_label('העלאת תלוש שכר')).to_have_count(1)
 assert saved['policyRevision']==baseline['policyRevision'] and saved['history']==baseline['history']
 saved['configuration']['ai']=baseline['configuration']['ai']
 restored=query('/admin/configuration',{'expectedRevision':saved['revision'],'configuration':saved['configuration'],'note':''})
 page.reload();page.wait_for_selector('.vehicle-card');nav('ניהול המערכת')
 print('PASS: draft connection probes, valid boundary values, hidden invalid model context, stale-result prevention; direct AI settings saves without reason or policy versions, document routing and shared conversation binding, encrypted primary/backup keys, disabled keys, persistence and mobile layout; no paid calls.')
 return restored['revision']

def check_api(query):
 """Persist catalogue/bindings through the real database adapter, without inference."""
 from copy import deepcopy
 baseline=query('/admin/configuration');config=deepcopy(baseline['configuration']);ai=config['ai']
 profile=deepcopy(ai['extraction']);profile.update(primaryKeyEnabled=True,backupKeys=[])
 pdf=deepcopy(profile);pdf.update(model='isolated-document-model',supportsDocuments=True,backupKeys=[{'id':'aabbccddeeff0011','name':'isolated backup','keyReference':'environment:CARWISE_AI_UNCONFIGURED','enabled':True}])
 ai.update(enabled=False,models=[{'id':'text-model','name':'Text fixture','profile':profile},{'id':'pdf-model','name':'PDF fixture','profile':pdf}],serviceModels={'extraction':'text-model','narration':'text-model','payslip':'pdf-model'},payslipEnabled=True)
 saved=query('/admin/configuration',{'expectedRevision':baseline['revision'],'configuration':config,'note':'isolated registry REST test','apiKeys':{'text-model':'TEST-ONLY-REGISTRY-TEXT','pdf-model':'TEST-ONLY-REGISTRY-PDF'},'backupApiKeys':{'pdf-model':{'aabbccddeeff0011':'TEST-ONLY-REGISTRY-BACKUP'}}})
 loaded=query('/admin/configuration');assert loaded['configuration']['ai']['serviceModels']['payslip']=='pdf-model'
 assert loaded['configuration']['ai']['models'][1]['profile']['backupKeys'][0]['keyReference'].startswith('vault:')
 assert loaded['keyPools']['pdf-model'][1]['configured']
 assert 'TEST-ONLY-REGISTRY' not in json.dumps(loaded)
 assert loaded['policyRevision']==baseline['policyRevision'] and loaded['history']==baseline['history']
 loaded['configuration']['ai']=baseline['configuration']['ai']
 query('/admin/configuration',{'expectedRevision':loaded['revision'],'configuration':loaded['configuration'],'note':''})
 print('PASS: model registry, service bindings and encrypted model key pools persisted through database REST adapter without inference.')
