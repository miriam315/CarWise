"""Browser checks with fictional keys; never invokes a paid provider."""
import json
from playwright.sync_api import expect

def check(page,query,nav,screenshots):
 baseline=query('/admin/configuration');baseline_revision=baseline['revision']
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
 pdf_id=page.get_by_label('מודל לשירות payslip',exact=True).locator('option').filter(has_text='PDF browser test').get_attribute('value')
 page.get_by_label('מודל לשירות payslip',exact=True).select_option(pdf_id)
 expect(page.get_by_role('button',name='הסרת מודל',exact=True)).to_be_disabled()
 page.get_by_label('סיבת שינוי הגדרות').fill('browser model catalogue and encrypted backup keys')
 page.get_by_role('button',name='שמירה והפעלה',exact=True).click();expect(page.locator('.admin-success')).to_contain_text('נשמרו והופעלו')
 saved=query('/admin/configuration');ai=saved['configuration']['ai']
 assert len(ai['models'])==3 and ai['serviceModels']['payslip']==pdf_id
 pdf=next(m for m in ai['models'] if m['id']==pdf_id)
 assert pdf['profile']['supportsDocuments'] and len(pdf['profile']['backupKeys'])==2
 assert not pdf['profile']['backupKeys'][1]['enabled']
 assert all(k['configured'] for k in saved['keyPools'][pdf_id])
 assert 'TEST-ONLY-PDF-' not in json.dumps(saved)
 page.reload();page.wait_for_selector('.vehicle-card');nav('ניהול המערכת');page.get_by_role('tab',name='ספקי AI',exact=True).click()
 expect(page.get_by_label('מודל לשירות payslip',exact=True)).to_have_value(pdf_id)
 page.locator('.ai-model-list button').filter(has_text='PDF browser test').click()
 expect(page.get_by_label('מפתח גיבוי 1',exact=True)).to_have_value('')
 expect(page.get_by_label('הפעלת גיבוי 2',exact=True)).not_to_be_checked()
 # Switch two services to the same existing model, leaving the document model independent.
 extraction_id=ai['serviceModels']['extraction']
 page.get_by_label('מודל לשירות narration',exact=True).select_option(extraction_id)
 page.get_by_label('סיבת שינוי הגדרות').fill('share text model across conversation services')
 page.get_by_role('button',name='שמירה והפעלה',exact=True).click();expect(page.locator('.admin-success')).to_contain_text('נשמרו והופעלו')
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
 page.get_by_label('סיבת שינוי הגדרות').fill('enable payslips while conversation AI stays off')
 page.get_by_role('button',name='שמירה והפעלה',exact=True).click();expect(page.locator('.admin-success')).to_contain_text('נשמרו והופעלו')
 assert query('/admin/configuration')['configuration']['ai']['enabled'] is False
 assert query('/data')['payslipEnabled'] and query('/payslip/status')['code']=='ready'
 nav('הנתונים שלי');expect(page.get_by_label('העלאת תלוש שכר')).to_have_count(1)
 # An invalid file must reach file validation rather than being blocked by the chat toggle; no provider call.
 rejected=page.request.post(page.url.rstrip('/')+'/api/payslip/extract?employee=E-001&consent=true',data=b'not a payroll document',headers={'Content-Type':'application/octet-stream'})
 assert rejected.status==400 and 'PDF' in rejected.json()['error']
 nav('ניהול המערכת');page.get_by_role('tab',name='פרומפטים ותלושים',exact=True).click()
 page.get_by_label('הפעלת חילוץ תלושים').uncheck();page.get_by_label('סיבת שינוי הגדרות').fill('disable payslips independently')
 page.get_by_role('button',name='שמירה והפעלה',exact=True).click();expect(page.locator('.admin-success')).to_contain_text('נשמרו והופעלו')
 nav('הנתונים שלי');expect(page.get_by_text('חילוץ תלושים כבוי בהגדרות השמורות.',exact=False)).to_be_visible()
 assert page.get_by_label('העלאת תלוש שכר').count()==0
 # Simulate a saved administrator change while the worker's page is already open.
 latest=query('/admin/configuration');latest['configuration']['ai']['payslipEnabled']=True
 saved=query('/admin/configuration',{'expectedRevision':latest['revision'],'configuration':latest['configuration'],'note':'another administrator enables documents'})
 page.get_by_role('button',name='בדיקת זמינות מחדש',exact=True).click();expect(page.get_by_label('העלאת תלוש שכר')).to_have_count(1)
 restored=query('/admin/restore',{'expectedRevision':saved['revision'],'revision':baseline_revision,'note':'restore original models after isolated browser check'})
 page.reload();page.wait_for_selector('.vehicle-card');nav('ניהול המערכת')
 print('PASS: model catalogue UI, independent PDF binding, shared conversation binding, encrypted primary/backup keys, disabled keys, persistence and mobile layout; no paid calls.')
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
 query('/admin/restore',{'expectedRevision':saved['revision'],'revision':baseline['revision'],'note':'restore original registry after REST check'})
 print('PASS: model registry, service bindings and encrypted model key pools persisted through database REST adapter without inference.')
