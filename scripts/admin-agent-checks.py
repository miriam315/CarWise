"""Administrator opens and answers representative tickets without an employee profile."""
import json
from playwright.sync_api import expect


def check(query, page, nav, screenshots):
    errors = []
    page.on('pageerror', lambda error: errors.append(str(error)))
    data = query('/data')
    employee = data['employees'][0]
    ticket = query('/tickets', {'employee': employee, 'target': 'CAR-10',
                                'question': 'בדיקת מנהל: אישור השתתפות אישית'})
    runtime = query('/runtime')
    runtime.update(demoMode=False, authenticationRequired=True)
    data.update(canAdmin=True, canAgent=True, currentUser={
        'role': 'admin', 'employeeId': None, 'displayName': 'מנהלת בדיקה',
        'email': 'admin@example.invalid'})
    # Browser sees the production admin shell; ticket reads/writes use the real test DB.
    page.route('**/api/runtime', lambda route: route.fulfill(json=runtime))
    page.route('**/api/data', lambda route: route.fulfill(json=data))
    page.evaluate("sessionStorage.setItem('carwise-access-token','synthetic-test-session')")
    page.reload()
    expect(page.locator('.admin-employee-choice select')).to_have_value('')
    expect(page.locator('.profile-picker')).to_have_count(0)
    expect(page.locator('.chat-input')).to_have_count(0)
    page.get_by_role('button', name='פרומפטים ותלושים', exact=True).click()
    prompt = page.get_by_label('הנחיות לחילוץ משיחה', exact=True)
    original = prompt.input_value()
    prompt.fill(original + '\nטיוטה לבדיקה בלבד')
    revision = query('/admin/configuration')['revision']
    nav('מרכז הנציגים')
    expect(page.locator('.breadcrumb')).to_contain_text('מרכז הנציגים')
    expect(page.locator('.sidebar nav .active')).to_contain_text('מרכז הנציגים')
    card = page.locator('.ticket-card').filter(has_text='בדיקת מנהל: אישור השתתפות אישית')
    expect(card).to_be_visible()
    card.get_by_label('השתתפות עובד מאושרת (₪)', exact=True).fill('350')
    card.get_by_label('תשובת הנציג', exact=True).fill('השתתפות אישית אושרה על ידי מנהלת הבדיקה')
    with page.expect_response(lambda r: r.request.method == 'POST' and '/api/tickets/' in r.url) as answered:
        card.get_by_role('button', name='שליחת תשובה והמשך השיחה', exact=True).click()
    assert answered.value.ok, answered.value.text()
    expect(card).to_contain_text('נענה · השיחה עודכנה')
    expect(page.locator('.toast')).to_contain_text('התשובה נשמרה')
    saved = next(t for t in query('/tickets') if t['id'] == ticket['id'])
    assert saved['status'] == 'resolved' and saved['participation'] == 350
    assert any('השתתפות אישית אושרה' in m['text'] for m in query('/history/' + employee['id']))
    assert query('/admin/configuration')['revision'] == revision
    nav('ניהול המערכת')
    expect(prompt).to_have_value(original + '\nטיוטה לבדיקה בלבד')
    prompt.fill(original)
    nav('מרכז הנציגים')
    query('/tickets', {'employee': employee, 'target': 'CAR-06',
                       'question': 'פנייה חדשה שמתעדכנת ללא פרופיל עובד'})
    expect(page.locator('.ticket-card').filter(has_text='פנייה חדשה שמתעדכנת ללא פרופיל עובד')).to_be_visible(timeout=10000)
    expect(page.locator('.sidebar nav .count-badge')).to_have_text('1')
    expect(page.locator('.admin-employee-choice select')).to_have_value('')
    expect(page.get_by_role('button', name='לבחירת רכב ובדיקת השתתפות', exact=False)).to_have_count(0)
    page.screenshot(path=str(screenshots / 'admin-representative-center.png'), full_page=True)
    page.set_viewport_size({'width': 390, 'height': 844})
    page.get_by_role('button', name='פתיחת תפריט', exact=True).click()
    nav('ניהול המערכת')
    expect(prompt).to_be_visible()
    page.get_by_role('button', name='פתיחת תפריט', exact=True).click()
    nav('מרכז הנציגים')
    expect(card).to_be_visible()
    assert not page.evaluate('document.documentElement.scrollWidth > innerWidth')
    assert not errors, errors
    (screenshots / 'admin-agent.json').write_text(json.dumps({
        'passed': True, 'adminWithoutEmployee': True, 'realTicketResolved': True,
        'employeeHistoryUpdated': True, 'draftPreserved': True,
        'livePendingCount': True, 'mobileNavigation': True, 'browserErrors': errors}, indent=2) + '\n')
    print('PASS: admin without employee profile, real representative answer and employee continuation, live pending tickets, preserved admin draft and mobile navigation.', flush=True)
