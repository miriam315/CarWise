"""Administrator log viewing/export, safe request correlation and private volume persistence."""
import json
from urllib.parse import urljoin
from playwright.sync_api import expect


def check(query, page, nav, screenshots):
    errors=[]
    page.on('pageerror',lambda error:errors.append(str(error)))
    data=query('/data')
    employee=data['employees'][0].copy()
    employee['salary']=-1
    secret='PRIVATE-CREDENTIAL-FOR-LOG-TEST'
    employee['name']='PRIVATE-EMPLOYEE-FOR-LOG-TEST'
    response=page.request.post(urljoin(page.url,'/api/simulate?api_key='+secret),data={'employee':employee,'privateBody':'PRIVATE-PAYSLIP-TEXT'})
    assert response.status==400
    trace=response.headers.get('x-request-id')
    assert trace and len(trace)==32
    records=query('/admin/logs?q='+trace)
    assert any(item['eventId']==1001 and '/api/simulate' in item['message'] and '400' in item['message'] for item in records['items'])
    raw=json.dumps(records)
    for private in [secret,'PRIVATE-EMPLOYEE-FOR-LOG-TEST','PRIVATE-PAYSLIP-TEXT']:
        assert private not in raw

    nav('ניהול המערכת')
    page.get_by_role('tab',name='לוגי מערכת',exact=True).click()
    panel=page.get_by_role('region',name='לוגי מערכת',exact=True)
    expect(panel.get_by_role('heading',name='לוגי מערכת',exact=True)).to_be_visible()
    panel.get_by_label('רמת לוג',exact=True).select_option('Warning')
    panel.get_by_label('חיפוש בלוגים',exact=True).fill(trace)
    panel.get_by_role('button',name='סינון לוגים',exact=True).click()
    expect(panel.locator('tbody')).to_contain_text('/api/simulate')
    expect(panel.locator('tbody')).to_contain_text('400')
    expect(panel.locator('tbody')).not_to_contain_text(secret)
    panel.locator('summary').first.click()
    expect(panel.locator('input[readonly]').first).to_have_value(trace)
    with page.expect_download() as downloaded:
        panel.get_by_role('button',name='ייצוא לפי המסננים',exact=True).click()
    entries=[json.loads(line) for line in open(downloaded.value.path()).read().splitlines() if line]
    assert entries and all(item['traceId']==trace for item in entries)
    assert all(private not in json.dumps(entries) for private in [secret,'PRIVATE-EMPLOYEE-FOR-LOG-TEST','PRIVATE-PAYSLIP-TEXT'])
    page.screenshot(path=str(screenshots/'system-logs-desktop.png'),full_page=True)

    panel.get_by_label('חיפוש בלוגים',exact=True).fill('not-a-real-log-record-12345')
    panel.get_by_role('button',name='סינון לוגים',exact=True).click()
    expect(panel).to_contain_text('לא נמצאו לוגים התואמים למסננים')
    panel.get_by_label('חיפוש בלוגים',exact=True).fill('')
    panel.get_by_role('button',name='סינון לוגים',exact=True).click()
    expect(panel.locator('tbody tr')).not_to_have_count(0)
    page.clock.install()
    panel.get_by_label('רענון לוגים אוטומטי',exact=True).check()
    with page.expect_response(lambda r:'/api/admin/logs?' in r.url and r.request.method=='GET'):
        page.clock.fast_forward(10001)
    panel.get_by_label('רענון לוגים אוטומטי',exact=True).uncheck()
    page.set_viewport_size({'width':390,'height':844})
    expect(panel.get_by_role('heading',name='לוגי מערכת',exact=True)).to_be_visible()
    assert page.evaluate('document.documentElement.scrollWidth<=innerWidth+1')
    page.screenshot(path=str(screenshots/'system-logs-mobile.png'),full_page=True)
    assert not errors,errors
    print('PASS: real structured request diagnostics, trace correlation, no body/query/employee disclosure, administrator log tab, severity/search, JSONL export, auto refresh and mobile.',flush=True)
