"""Embedded chat, grouped admin navigation and unified sign-in. Recovery is deliberately simulated."""
import json
from playwright.sync_api import expect


def check(query,page,nav,screenshots):
    errors=[];page.on('pageerror',lambda error:errors.append(error.stack or str(error)))
    # Text can shrink below the old minimum and the chosen size survives reload.
    size=page.get_by_role('group',name='גודל טקסט',exact=True)
    expect(size).to_contain_text('80 אחוז')
    initial_font=page.evaluate('parseFloat(getComputedStyle(document.documentElement).fontSize)')
    for _ in range(2):page.get_by_role('button',name='הקטנת טקסט',exact=True).click()
    expect(size).to_contain_text('60 אחוז')
    expect(page.get_by_role('button',name='הקטנת טקסט',exact=True)).to_be_disabled()
    assert page.evaluate('parseFloat(getComputedStyle(document.documentElement).fontSize)')<initial_font
    page.reload()
    expect(size).to_contain_text('60 אחוז')
    page.get_by_role('button',name='איפוס גודל הטקסט',exact=True).click()
    expect(size).to_contain_text('80 אחוז')

    nav('קטלוג הרכבים')
    heading=page.get_by_role('heading',name='נעים להכיר. האפשרויות שלך.',exact=True)
    expect(heading).to_be_visible();windows=len(page.context.pages)
    page.get_by_role('button',name='פתיחת היועץ החכם',exact=True).click()
    chat=page.get_by_role('dialog',name='היועץ החכם',exact=True)
    expect(chat).to_be_visible();expect(heading).to_be_visible();assert len(page.context.pages)==windows
    expect(chat).not_to_contain_text('מבוסס כללים');expect(chat).not_to_contain_text('מפתח בשרת')
    question='האם אפשר לוותר על הרכב?'
    chat.get_by_label('הודעה ליועץ',exact=True).fill(question)
    chat.get_by_role('button',name='שליחת הודעה',exact=True).click()
    expect(chat.locator('.typing')).to_have_count(0,timeout=30000)
    expect(chat.locator('.chat-messages')).to_contain_text(question)
    page.screenshot(path=str(screenshots/'embedded-chat-desktop.png'))
    chat.get_by_role('button',name='סגירת היועץ',exact=True).click();expect(chat).to_have_count(0)
    page.get_by_role('button',name='פתיחת היועץ החכם',exact=True).click()
    expect(chat.locator('.chat-messages')).to_contain_text(question)
    page.set_viewport_size({'width':390,'height':844});page.wait_for_timeout(200)
    box=chat.bounding_box();assert box['x']>=0 and box['x']+box['width']<=391 and box['y']+box['height']<=845
    assert not page.evaluate('document.documentElement.scrollWidth>innerWidth')
    page.screenshot(path=str(screenshots/'embedded-chat-mobile.png'))
    chat.get_by_role('button',name='סגירת היועץ',exact=True).click()
    page.set_viewport_size({'width':1440,'height':1100})
    # A late reply for a previously selected employee must not replace the current profile/chat.
    employees=query('/data')['employees'];original=employees[0];other=employees[1]
    held=[]
    page.route('**/api/chat',lambda route:held.append(route))
    page.get_by_role('button',name='פתיחת היועץ החכם',exact=True).click()
    chat.get_by_label('הודעה ליועץ',exact=True).fill('Pending synthetic request')
    chat.get_by_role('button',name='שליחת הודעה',exact=True).click()
    page.wait_for_timeout(100);assert held
    page.get_by_label('בחירת עובד לדוגמה',exact=True).select_option(other['id'])
    held[0].fulfill(json={'reply':'OLD EMPLOYEE RESPONSE MUST NOT APPEAR','employee':original})
    page.wait_for_timeout(200)
    expect(page.get_by_label('בחירת עובד לדוגמה',exact=True)).to_have_value(other['id'])
    page.get_by_role('button',name='פתיחת היועץ החכם',exact=True).click()
    expect(chat).not_to_contain_text('OLD EMPLOYEE RESPONSE MUST NOT APPEAR')
    chat.get_by_role('button',name='סגירת היועץ',exact=True).click()
    page.unroute('**/api/chat')
    page.get_by_label('בחירת עובד לדוגמה',exact=True).select_option(original['id'])
    nav('ניהול המערכת')
    tabs=page.get_by_role('tablist',name='תחומי ניהול',exact=True)
    expect(tabs.locator('.admin-tab-group')).to_have_count(4)
    expect(tabs.locator('.admin-tab-group').filter(has=page.get_by_role('tab',name='ספקי AI',exact=True))).to_contain_text('פרומפטים ותלושים')
    assert tabs.evaluate('(e)=>e.scrollWidth<=e.clientWidth+1')
    page.screenshot(path=str(screenshots/'grouped-admin-tabs-desktop.png'))
    page.set_viewport_size({'width':390,'height':844});page.wait_for_timeout(250)
    assert tabs.evaluate('(e)=>e.scrollWidth<=e.clientWidth+1')
    assert not page.evaluate('document.documentElement.scrollWidth>innerWidth')
    page.screenshot(path=str(screenshots/'grouped-admin-tabs-mobile.png'))
    page.set_viewport_size({'width':1440,'height':1100})
    page.get_by_role('tab',name='ספקי AI',exact=True).click()
    provider=page.locator('select[aria-label^="ספק "]');provider.select_option('other')
    page.get_by_label('שם ספק לתצוגה',exact=True).fill('ספק מותאם לבדיקה')
    page.get_by_label('תאימות API לספק אחר',exact=True).select_option('gemini')
    url=page.locator('input[aria-label^="כתובת ספק "]');url.fill('https://api.custom-supplier.com/v1/')
    approval=page.get_by_label('אישור כתובת ספק ידני',exact=True);approval.check()
    url.fill('https://api.another-supplier.com/v1/');expect(approval).not_to_be_checked()
    expect(provider).to_have_value('other')
    expect(page.get_by_label('תאימות API לספק אחר',exact=True)).to_have_value('gemini')
    # Simulate the authentication-required shell only. Backend role checks remain covered in .NET.
    runtime=query('/runtime');runtime.update(authenticationRequired=True,demoMode=False)
    page.route('**/api/runtime',lambda route:route.fulfill(json=runtime))
    page.route('**/api/setup/admin/status',lambda route:route.fulfill(json={'needsAdmin':False}))
    page.evaluate("sessionStorage.removeItem('carwise-access-token')");page.reload()
    expect(page.get_by_role('heading',name='ברוכים הבאים ל־CarWise',exact=True)).to_be_visible()
    expect(page.get_by_role('tab',name='כניסת מנהל',exact=True)).to_have_count(0)
    expect(page.get_by_role('tab',name='כניסת עובדים',exact=True)).to_have_count(0)
    posts=[]
    page.on('request',lambda request:posts.append(request.url) if request.method=='POST' else None)
    login=[]
    def probe(route):
        login.append(route.request.post_data_json)
        route.fulfill(status=400,json={'error':'בדיקת התחברות בלבד'})
    page.route('**/api/auth/login',probe)
    review=page.get_by_role('region',name='חשבונות לבדיקה',exact=True)
    expect(review).to_be_visible();expect(review.locator('li')).to_have_count(5)
    for account in review.locator('li').all():
        displayed=account.locator('dd code').all_inner_texts()
        account.get_by_role('button').click()
        assert page.get_by_label('דוא״ל להתחברות',exact=True).input_value()==displayed[0]
        assert page.get_by_label('סיסמה להתחברות',exact=True).input_value()==displayed[1]
    assert not login # Filling a review account never submits or changes server accounts.
    page.set_viewport_size({'width':390,'height':844})
    assert not page.evaluate('document.documentElement.scrollWidth>innerWidth')
    page.set_viewport_size({'width':1440,'height':1100})

    page.get_by_label('דוא״ל להתחברות',exact=True).fill('synthetic@example.invalid')
    page.get_by_label('סיסמה להתחברות',exact=True).fill('Synthetic-password-only!')
    page.get_by_role('button',name='כניסה למערכת',exact=True).click()
    expect(page.locator('.auth-card [role=alert]')).to_contain_text('בדיקת התחברות')
    assert len(login)==1 and set(login[0])=={'email','password'}
    count=len(posts)
    page.get_by_role('button',name='שכחתי סיסמה',exact=True).click()
    expect(page.get_by_label('סיסמה להתחברות',exact=True)).to_have_count(0)
    page.get_by_role('button',name='שליחת סיסמה זמנית',exact=True).click()
    expect(page.locator('.auth-recovery-notice')).to_contain_text('נשלחה סיסמה זמנית למייל')
    expect(page.locator('.auth-recovery-notice')).to_contain_text('לא נשלח דוא״ל ולא בוצע איפוס')
    assert len(posts)==count # No mail/reset/auth provider call is allowed for this prototype.
    page.screenshot(path=str(screenshots/'unified-login-recovery-demo.png'))
    page.get_by_role('button',name='חזרה להתחברות',exact=True).click()
    expect(page.get_by_label('סיסמה להתחברות',exact=True)).to_be_visible()
    assert not errors,errors
    (screenshots/'workspace.json').write_text(json.dumps({'passed':True,'embeddedChat':True,'singleBrowserWindow':True,'chatHistorySurvivesClose':True,
       'groupedAdminNavigation':True,'desktopAndMobileFit':True,'unifiedLogin':True,'recoveryIsLabelledSimulation':True,'recoveryNetworkRequests':0,'browserErrors':errors},indent=2)+'\n')
    print('PASS: embedded persistent chat without page/window navigation, responsive grouped admin tabs, unified sign-in and explicitly simulated password recovery with zero requests.',flush=True)
