"""Documented catalogue defaults, personal reports, approvals and out-of-catalogue referrals."""
from playwright.sync_api import expect

def check(query,page,nav,screenshots):
    errors=[];page.on('pageerror',lambda error:errors.append(str(error)))
    data=query('/data');employee=data['employees'][0];car=next(c for c in data['cars'] if c['id']=='CAR-10')
    def chat(text,target=None):
        return query('/chat',{'employee':employee,'text':text,'target':target})
    def simulate():return query('/simulate',{'employee':employee})
    def alternative():return next(r for r in simulate()['results'] if r['id']==car['id'])
    first=chat('בחרתי',car['id']);assert 'ברירת מחדל בקטלוג' in first['reply']
    assert not query('/tickets')
    assert alternative()['participation']==car['participation']
    assert simulate()['current']['participation']==employee['participation']
    for amount in [777,0]:
        chat(f'השתתפות ברכב החדש {amount}',car['id'])
        result=alternative();assert result['participation']==amount and result['participationSource']=='employee'
    # The persisted selected alternative supplies context for a natural-language dispute.
    disputed=chat('אצלי סוכם אחרת ולא יודע כמה, אבקש אישור נציג')
    assert disputed['awaitingField']=='targetParticipation'
    pending=alternative();assert pending['pending'] and all(pending[k] is None for k in ['net','delta','annual'])
    assert any(not r['pending'] and r['net'] is not None for r in simulate()['results'] if r['eligible'] and r['id']!=car['id'])
    ticket=query('/tickets')[0]
    page.reload();page.wait_for_selector('.vehicle-card');nav('מרכז הנציגים')
    card=page.locator('.ticket-card').filter(has_text='העובד מבקש אישור השתתפות אישית')
    card.get_by_label('השתתפות עובד מאושרת (₪)',exact=True).fill('444')
    card.get_by_label('תשובת הנציג',exact=True).fill('השתתפות אישית מאושרת לצורך בדיקה סינתטית')
    card.get_by_role('button',name='שליחת תשובה והמשך השיחה',exact=True).click()
    expect(card).to_contain_text('444')
    result=alternative();assert not result['pending'] and result['participation']==444 and result['participationSource']=='representative'
    chat('השתתפות ברכב החדש 999',car['id'])
    assert alternative()['participation']==444 # The representative decision has precedence.
    assert simulate()['current']['participation']==employee['participation']
    outside=chat('אני מבקש רכב Synthetic XYZ שלא מופיע בקטלוג')
    assert 'לא אנחש' in outside['reply'] and outside['target'] is None and not outside['complete']
    general=next(t for t in query('/tickets') if t['target'].startswith('catalogue:'))
    page.reload();page.wait_for_selector('.vehicle-card');nav('מרכז הנציגים')
    general_card=page.locator('.ticket-card').filter(has_text='Synthetic XYZ')
    expect(general_card).to_be_visible();expect(general_card.locator('input[type=number]')).to_have_count(0)
    general_card.get_by_label('תשובת הנציג',exact=True).fill('נדרשת השלמת פרטי הרכב ממקור רשמי לפני הוספתו לקטלוג')
    general_card.get_by_role('button',name='שליחת תשובה והמשך השיחה',exact=True).click()
    expect(general_card).to_contain_text('השיחה עודכנה')
    assert next(t for t in query('/tickets') if t['id']==general['id'])['status']=='resolved'
    assert alternative()['participation']==444
    nav('קטלוג הרכבים')
    vehicle=page.locator('.catalog-card').filter(has=page.get_by_role('heading',name=car['model'],exact=True))
    expect(vehicle).to_contain_text('אישור נציג');expect(vehicle).to_contain_text('444')
    page.screenshot(path=str(screenshots/'participation-sources.png'))
    page.reload();page.wait_for_selector('.vehicle-card');assert alternative()['participationSource']=='representative'
    # Unrelated questions must not inherit an earlier electric intent or remembered car choices.
    chat('מה יקרה לנטו אם אעבור לרכב חשמלי?')
    for unrelated in ['איזה מחשב כדאי לקנות?', 'מה כדאי לבשל?']:
        redirected=chat(unrelated)
        assert 'אני כאן לייעוץ' in redirected['reply'] and redirected['candidates']==[]
        assert 'הנה רכבים חשמליים' not in redirected['reply'] and 'בחרו את הדגם המדויק' not in redirected['reply']
    assert alternative()['participation']==444
    assert not errors,errors
    print('PASS: catalogue defaults with provenance, separate actual current contribution, personal alternative amount including zero, disputed comparison withholding, representative precedence, safe out-of-catalogue referral, nonfinancial representative answer and persistence.',flush=True)
