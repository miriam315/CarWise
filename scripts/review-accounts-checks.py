"""Disclosure UI and account-management integration; synthetic identity provider responses only.
.NET tests separately exercise actual AuthAccounts + encrypted DB registry lifecycle.
"""
from copy import deepcopy
from playwright.sync_api import expect


def check(query, page, nav, screenshots):
    errors=[];page.on('pageerror',lambda error:errors.append(str(error)))
    revision=query('/admin/configuration')['revision']
    # Real DB-backed global off switch also applies to the unauthenticated endpoint.
    query('/admin/review-accounts',{'enabled':False})
    assert query('/auth/review-accounts')=={'enabled':False,'items':[]}
    query('/admin/review-accounts',{'enabled':True})
    assert query('/admin/configuration')['revision']==revision
    employees=query('/data')['employees']
    entries=[{'id':'admin-test','email':'admin-review@example.invalid','role':'admin','employeeId':None,'published':True,'passwordSaved':True},
             {'id':'existing-test','email':'existing-review@example.invalid','role':'employee','employeeId':employees[0]['id'],'published':True,'passwordSaved':False}]
    secrets={'admin-test':'SYNTHETIC-ADMIN-PASSWORD'};state={'enabled':True};created=[];requests=[]
    def view():return {'enabled':state['enabled'],'available':True,'items':deepcopy(entries)}
    def directory(route):
        if route.request.method=='POST':state['enabled']=route.request.post_data_json['enabled']
        route.fulfill(json=view())
    def entry_update(route):
        entry=next(e for e in entries if e['id']==route.request.url.rsplit('/',1)[-1])
        body=route.request.post_data_json;entry['published']=body['published']
        if body.get('password'):secrets[entry['id']]=body['password'];entry['passwordSaved']=True
        route.fulfill(json=view())
    def accounts(route):
        if route.request.method=='POST':
            body=route.request.post_data_json;created.append(body)
            entry={'id':'new-test','email':body['email'],'role':body['role'],'employeeId':body['employeeId'],'published':True,'passwordSaved':True}
            entries.append(entry);secrets[entry['id']]=body['password'];route.fulfill(json=entry)
        else:route.fulfill(json={'items':entries})
    def reset(route):
        entry=next(e for e in entries if e['id']==route.request.url.split('/')[-2])
        secrets[entry['id']]=route.request.post_data_json['password'];entry['passwordSaved']=True
        route.fulfill(json={'saved':True})
    def public(route):
        requests.append(route.request.url)
        route.fulfill(json={'enabled':state['enabled'],'items':[{**e,'password':secrets.get(e['id'])} for e in entries if state['enabled'] and e['published']]})
    page.route('**/api/admin/accounts',accounts)
    page.route('**/api/admin/accounts/*/password',reset)
    page.route('**/api/admin/review-accounts',directory)
    page.route('**/api/admin/review-accounts/*',entry_update)
    page.route('**/api/auth/review-accounts',public)
    nav('ניהול המערכת');page.get_by_role('tab',name='חשבונות והרשאות',exact=True).click()
    manager=page.get_by_role('region',name='ניהול חשבונות לבדיקה',exact=True)
    expect(manager.get_by_label('פרסום חשבונות לבדיקה במסך ההתחברות',exact=True)).to_be_checked()
    existing=manager.locator('tbody tr').filter(has_text='existing-review@example.invalid')
    expect(existing).to_contain_text('נדרשת השלמה')
    existing.get_by_role('button',name='השלמת סיסמה ידועה',exact=True).click()
    manager.get_by_label('סיסמה קיימת לפרסום בבדיקה',exact=True).fill('SYNTHETIC-EXISTING-PASSWORD')
    manager.get_by_role('button',name='אימות ושמירה לרשימת הבדיקה',exact=True).click()
    expect(existing).to_contain_text('שמורה')
    page.get_by_label('דוא״ל לחשבון חדש',exact=True).fill('new-review@example.invalid')
    page.get_by_label('סיסמה לחשבון חדש',exact=True).fill('SYNTHETIC-NEW-PASSWORD')
    page.get_by_label('פרופיל לחשבון חדש',exact=True).select_option(employees[1]['id'])
    page.get_by_role('button',name='יצירת חשבון',exact=True).click()
    new=manager.locator('tbody tr').filter(has_text='new-review@example.invalid');expect(new).to_contain_text('שמורה')
    assert len(created)==1
    # Discover accounts created outside this screen, without requiring a new frontend build.
    entries.append({'id':'outside-test','email':'outside-review@example.invalid','role':'agent','employeeId':employees[2]['id'],'published':True,'passwordSaved':False})
    manager.get_by_role('button',name='רענון ואיתור חשבונות חסרים',exact=True).click()
    expect(manager.locator('tbody tr').filter(has_text='outside-review@example.invalid')).to_contain_text('נדרשת השלמה')
    new.get_by_label('פרסום חשבון new-review@example.invalid',exact=True).uncheck()
    expect(new.get_by_label('פרסום חשבון new-review@example.invalid',exact=True)).not_to_be_checked()
    page.screenshot(path=str(screenshots/'review-accounts-management.png'),full_page=True)
    # Production-style login shell. Reading/copying the list must never submit credentials.
    runtime=query('/runtime');runtime.update(authenticationRequired=True,demoMode=False)
    page.route('**/api/runtime',lambda route:route.fulfill(json=runtime))
    page.route('**/api/setup/admin/status',lambda route:route.fulfill(json={'needsAdmin':False}))
    page.evaluate("sessionStorage.removeItem('carwise-access-token')");page.reload()
    review=page.get_by_role('region',name='חשבונות לבדיקה',exact=True)
    expect(review.get_by_role('button',name='הצגת חשבונות לבדיקה',exact=True)).to_have_attribute('aria-expanded','false')
    expect(review.locator('li')).to_have_count(0);assert not requests
    page.evaluate('document.fonts.ready')
    def login_positions():
        return page.evaluate('''() => Object.fromEntries(['.auth-story > div','.auth-branding','.auth-trust',
            'input[aria-label="דוא״ל להתחברות"]','input[aria-label="סיסמה להתחברות"]'].map(selector=>{
                const rect=document.querySelector(selector).getBoundingClientRect();
                return [selector,{top:rect.top+scrollY,height:rect.height}];
            }))''')
    collapsed=login_positions()
    review.get_by_role('button',name='הצגת חשבונות לבדיקה',exact=True).click()
    expect(review.locator('li')).to_have_count(3)
    expanded=login_positions()
    for selector in collapsed:
        assert abs(expanded[selector]['top']-collapsed[selector]['top'])<1,(selector,collapsed,expanded)
    copy=expanded['.auth-story > div']
    assert copy['top']<expanded['input[aria-label="סיסמה להתחברות"]']['top']+expanded['input[aria-label="סיסמה להתחברות"]']['height']
    assert copy['top']+copy['height']>expanded['input[aria-label="דוא״ל להתחברות"]']['top']
    page.screenshot(path=str(screenshots/'review-accounts-stable-desktop.png'),full_page=True)
    expect(review).not_to_contain_text('new-review@example.invalid')
    outside=review.locator('li').filter(has_text='outside-review@example.invalid')
    expect(outside.get_by_role('button')).to_be_disabled()
    review.locator('li').filter(has_text='existing-review@example.invalid').get_by_role('button').click()
    expect(page.get_by_label('דוא״ל להתחברות',exact=True)).to_have_value('existing-review@example.invalid')
    expect(page.get_by_label('סיסמה להתחברות',exact=True)).to_have_value('SYNTHETIC-EXISTING-PASSWORD')
    review.get_by_role('button',name='הסתרת חשבונות לבדיקה',exact=True).click()
    expect(review.locator('li')).to_have_count(0)
    assert login_positions()==collapsed
    # Reopening fetches again, so newly added users and changed publication status are current.
    entries[-1]['published']=False
    review.get_by_role('button',name='הצגת חשבונות לבדיקה',exact=True).click()
    expect(review.locator('li')).to_have_count(2);assert len(requests)==2
    page.set_viewport_size({'width':390,'height':844})
    assert not page.evaluate('document.documentElement.scrollWidth > innerWidth')
    page.screenshot(path=str(screenshots/'review-accounts-mobile.png'),full_page=True)
    review.get_by_role('button',name='הסתרת חשבונות לבדיקה',exact=True).click();state['enabled']=False
    review.get_by_role('button',name='הצגת חשבונות לבדיקה',exact=True).click()
    expect(review).to_contain_text('פרסום חשבונות הבדיקה אינו פעיל')
    expect(review.locator('li')).to_have_count(0)
    assert not errors,errors
    print('PASS: collapsed login disclosure, lazy live list, account creation/discovery, missing-password completion, per-account hiding, global DB-backed off switch, no policy version writes and mobile layout.',flush=True)
