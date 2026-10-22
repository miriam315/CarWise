"""Timed judge rehearsal through the real UI and isolated DB; synthetic data only."""
from copy import deepcopy
import json
import subprocess
import time
from pathlib import Path
from playwright.sync_api import expect
from redacted_payslip_fixture import configure,prepare,extract,mock


def check(query,page,nav,screenshots):
    started=time.monotonic();steps=[];uploads=[]
    def step(name):
        steps.append({'step':name,'elapsedSeconds':round(time.monotonic()-started,3)})
    def request(item):
        if item.method=='POST' and '/payslip/extract' in item.url:uploads.append(item.url)
    page.on('request',request)
    root=Path(__file__).resolve().parent.parent
    readiness=subprocess.run(['node','scripts/check-cloud.mjs',page.url],cwd=root,capture_output=True,text=True,check=True)
    assert json.loads(readiness.stdout)['passed']
    step('Read-only readiness confirms actual local server, database and built assets')
    config=query('/admin/configuration');draft=deepcopy(config['configuration']);keys=configure(draft)
    employee={**draft['employees'][0],'id':'E-JUDGE-REHEARSAL','name':'עובד הדגמה לשופטים','children':4,'currentCar':'CAR-09','knownFields':['children','currentCar'],'advisorState':None}
    draft['employees'].append(employee)
    next(c for c in draft['cars'] if c['id']=='CAR-10')['participationKnown']=False
    query('/admin/configuration',{'expectedRevision':config['revision'],'configuration':draft,'apiKeys':keys,'note':'Synthetic isolated judge rehearsal'})
    page.reload();page.wait_for_selector('.vehicle-card')
    page.get_by_label('בחירת עובד לדוגמה',exact=True).select_option(employee['id'])
    expect(page.get_by_role('heading',name='נשלים רק את מה שחסר.',exact=True)).to_be_visible()
    page.get_by_role('button',name='השלמת נתונים בשיחה',exact=True).click()
    expect(page.locator('.chat-messages')).to_contain_text('מה שכר הבסיס החודשי')
    expect(page.locator('.chat-messages')).not_to_contain_text('מפתח בשרת')
    expect(page.locator('body')).not_to_contain_text('יועץ מבוסס כללים')
    expect(page.locator('.chat-messages')).not_to_contain_text('ניהול שיחה באמצעות AI')
    step('Missing salary prompts one question; no final net')

    nav('הנתונים שלי')
    controlled=[]
    handler=mock(page,{'salary':18000,'commission':0,'bonus':0,'pension':6,'fund':2.5,'credits':2.25,'participation':350},controlled)
    path=Path(__file__).resolve().parent.parent/'examples/payslips/complex/01-complex-monthly.pdf'
    page.get_by_label('העלאת תלוש שכר',exact=True).set_input_files(str(path))
    prepare(page);extract(page)
    page.unroute('**/api/payslip/extract?**',handler)
    assert 'salary' not in query('/conversation/'+employee['id'])['employee']['knownFields']
    page.get_by_label('בחר הכול — אישור שדות התלוש',exact=True).check()
    page.get_by_role('button',name='אישור והעברת השדות המסומנים לטופס',exact=True).click()
    page.get_by_role('button',name='שמירה וחישוב מחדש',exact=False).click()
    page.wait_for_selector('.vehicle-card')
    step('Dense PDF rasterized and reviewed locally, mocked AI fields explicitly saved')

    nav('קטלוג הרכבים')
    page.get_by_label('חיפוש רכב',exact=True).fill('ATTO')
    page.get_by_role('button',name='לבדיקת החלופה',exact=True).click()
    dialog=page.get_by_role('dialog',name='פירוט חישוב',exact=True)
    summary=dialog.get_by_label('סיכום השינוי בנטו',exact=True)
    expect(summary).to_contain_text('מה חסר? סכום ההשתתפות')
    expect(summary.locator('.net-drivers')).to_have_count(0)
    pending=[t for t in query('/tickets') if json.loads(t['employee'])['id']==employee['id'] and t['target']=='CAR-10']
    assert len(pending)==1 and pending[0]['status']=='pending'
    simulation=query('/simulate',{'employee':query('/conversation/'+employee['id'])['employee']})
    alternative=next(r for r in simulation['results'] if r['id']=='CAR-10')
    assert alternative['net'] is None and alternative['delta'] is None and alternative['explanation'] is None
    page.screenshot(path=str(screenshots/'judge-pending-approval.png'),full_page=True)
    step('Unknown participation automatically escalated; no final amount leaked')
    dialog.get_by_role('button',name='סגירת פירוט',exact=True).click()

    nav('מרכז הנציגים')
    ticket=page.locator('.ticket-card').filter(has=page.get_by_role('heading',name=employee['name'],exact=True))
    ticket.get_by_label('השתתפות עובד מאושרת (₪)',exact=True).fill('300')
    ticket.get_by_label('תשובת הנציג',exact=True).fill('אושרה השתתפות חודשית של 300 ₪ — הדגמה סינתטית')
    ticket.get_by_role('button',name='שליחת תשובה והמשך השיחה',exact=False).click()
    expect(ticket).to_contain_text('אושרה השתתפות חודשית')
    step('Agent answered through actual representative screen')

    nav('היועץ החכם')
    expect(page.locator('.chat-messages')).to_contain_text('הנציג ענה',timeout=15000)
    page.get_by_label('הודעה ליועץ',exact=True).fill('נמשיך')
    page.get_by_role('button',name='שליחת הודעה',exact=True).click()
    expect(page.locator('.typing')).to_have_count(0)
    expect(page.locator('.chat-messages')).to_contain_text('הנטו המשוער')
    nav('הסקירה שלי')
    summary=page.get_by_label('סיכום השינוי בנטו',exact=True)
    expect(summary.locator('.net-drivers article')).to_have_count(3)
    expect(summary).not_to_contain_text('חסר אישור')
    actual=query('/conversation/'+employee['id'])['employee']
    result=next(r for r in query('/simulate',{'employee':actual})['results'] if r['id']=='CAR-10')
    assert not result['pending'] and result['participation']==300
    assert abs(sum(d['impact'] for d in result['explanation']['drivers'])-result['delta'])<.000001
    assert abs(result['explanation']['monthlyChange']-result['delta'])<.000001
    assert abs(result['explanation']['annualChange']-result['delta']*12)<.000001
    page.screenshot(path=str(screenshots/'judge-final-net-summary.png'),full_page=True)
    step('Same conversation resumed with reconciled three-driver summary')

    page.set_viewport_size({'width':390,'height':844})
    page.wait_for_function("document.querySelector('.sidebar').getBoundingClientRect().left>=innerWidth-1")
    assert not page.evaluate('document.documentElement.scrollWidth>innerWidth')
    page.screenshot(path=str(screenshots/'judge-net-summary-mobile.png'),full_page=True)
    page.set_viewport_size({'width':1440,'height':1100})
    nav('מעבדת ההחלטה')
    page.get_by_role('button',name='שמירת הבחירה המקורית למעקב',exact=True).click()
    expect(page.get_by_text('הבחירה וגרסת החישוב נשמרו למעקב. תרחישי המעבדה לא נשמרו בפרופיל.',exact=True)).to_be_visible()
    assert query('/decision/state/'+employee['id'])['choice']['target']=='CAR-10'
    page.reload();page.wait_for_selector('.vehicle-card')
    expect(page.get_by_label('סיכום השינוי בנטו',exact=True).locator('.net-drivers article')).to_have_count(3)
    assert query('/conversation/'+employee['id'])['status']=='Active'
    step('Approved choice and conversation persist after reload; mobile fits')
    assert len(uploads)==1 and len(controlled)==1
    page.remove_listener('request',request)
    report={'passed':True,'environment':'isolated local PostgreSQL, demo identity, rule-based chat; mocked document provider; no hosted Auth or paid AI accuracy claim',
            'seconds':round(time.monotonic()-started,3),'steps':steps,'documentUploads':len(uploads),
            'note':'Elapsed time is an automated local rehearsal, not a cloud SLA or human presentation duration.'}
    (screenshots/'judge-rehearsal.json').write_text(json.dumps(report,indent=2)+'\n')
    print('PASS: timed judge journey — missing fields, dense payslip, explicit review, pending referral, representative UI, resumed chat, net explanation, save, reload and mobile.',flush=True)
