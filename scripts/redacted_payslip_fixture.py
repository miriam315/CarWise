"""Synthetic UI fixture: intercepts extraction only, never calls a paid provider.
Real provider adapters, routing and accounting are covered separately in .NET tests.
"""
import json
from playwright.sync_api import expect

def configure(draft):
    profile={**draft['ai']['extraction'],'supportsDocuments':True,'keyReference':'environment:CARWISE_AI_TEST_MISSING','backupKeys':[]}
    draft['ai'].update(enabled=False,payslipEnabled=True,models=[{'id':'document-fixture','name':'Synthetic document fixture','profile':profile}],serviceModels={s:'document-fixture' for s in ['extraction','narration','payslip']})
    return {'document-fixture':'TEST-ONLY-NEVER-SENT-TO-PROVIDER'}

def result(values):
    return {'fields':{key:{'value':value,'evidence':None} for key,value in values.items()},'warnings':['תוצאת ספק סינתטית לבדיקת ממשק; אינה בדיקת דיוק AI.'],'source':'ai-redacted','configurationRevision':1}

def mock(page,values,uploads):
    def handle(route):
        request=route.request
        assert 'consent=true' in request.url and 'redactionReviewed=true' in request.url
        raw=request.post_data_buffer
        assert raw.startswith(b'%PDF-1.4') and b'Raster document' in raw
        uploads.append(raw)
        route.fulfill(status=200,content_type='application/json',body=json.dumps(result(values() if callable(values) else values)))
    page.route('**/api/payslip/extract?**',handle)
    return handle

def prepare(page,pages=1):
    for index in range(pages):
        page.get_by_label('בדקתי את עמוד '+str(index+1),exact=True).check()
        if index+1<pages:page.locator('.payslip-redactor').get_by_role('button',name='העמוד הבא',exact=True).click()
    page.get_by_role('button',name='יצירת עותק בדוק לשליחה',exact=True).click()
    expect(page.locator('.payslip-redactor [role=status]')).to_contain_text('העותק הבדוק מוכן')
    page.locator('.payslip-consent input').check()

def extract(page):
    page.get_by_role('button',name='חילוץ נתונים מהתלוש',exact=True).click()
    page.locator('.payslip-review').wait_for(timeout=30000)
