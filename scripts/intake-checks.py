"""Saved payroll intake answers through the real UI and disposable PostgreSQL."""
from copy import deepcopy
import json
from playwright.sync_api import expect


def check(query, page, nav, screenshots):
    config = query('/admin/configuration')
    draft = deepcopy(config['configuration'])
    draft['ai']['enabled'] = False  # Reproduce the reported local-rules path, without paid APIs.
    profiles = []
    for index in range(2):
        profile = {**draft['employees'][0], 'id': f'E-INTAKE-{index}', 'name': f'בדיקת שכר {index}',
                   'knownFields': [], 'advisorState': None, 'salary': 0}
        draft['employees'].append(profile)
        profiles.append(profile)
    alternatives_profile = {**draft['employees'][0], 'id': 'E-ALTERNATIVES', 'name': 'בדיקת חלופה נוספת',
                            'occupation': 'מפקח/ת איכות במפעל', 'group': 'א', 'currentCar': 'CAR-01',
                            'participation': 150, 'knownFields': None, 'advisorState': None}
    draft['employees'].append(alternatives_profile)
    offered_profile = {**alternatives_profile, 'id': 'E-OFFERED-ONLY', 'name': 'בדיקת הצעה שנדחתה'}
    draft['employees'].append(offered_profile)
    query('/admin/configuration', {'expectedRevision': config['revision'], 'configuration': draft, 'note': ''})
    for profile, text in zip(profiles, ['14200 ש"ח', 'השכר שלי 14200 ש"ח']):
        page.reload()
        expect(page.get_by_label('בחירת עובד לדוגמה', exact=True)).to_be_visible()
        page.get_by_label('בחירת עובד לדוגמה', exact=True).select_option(profile['id'])
        page.get_by_role('button', name='השלמת נתונים בשיחה', exact=True).click()
        expect(page.locator('.message.assistant').last).to_contain_text('מה שכר הבסיס החודשי')
        page.get_by_label('הודעה ליועץ', exact=True).fill(text)
        with page.expect_response(lambda response: response.request.method == 'POST' and response.url.endswith('/api/chat')) as sending:
            page.get_by_role('button', name='שליחת הודעה', exact=True).click()
        response = sending.value
        assert response.ok and response.json()['awaitingField'] == 'commission'
        expect(page.locator('.message.assistant').last).to_contain_text('מה לגבי עמלות')
        saved = query('/conversation/' + profile['id'])['employee']
        assert saved['salary'] == 14200 and 'salary' in saved['knownFields']
        assert saved['advisorState']['awaitingField'] == 'commission'
        # A fresh browser state and a stale original employee payload must use saved DB knowledge.
        page.reload()
        expect(page.get_by_label('בחירת עובד לדוגמה', exact=True)).to_be_visible()
        page.get_by_role('button', name='השלמת נתונים בשיחה', exact=True).click()
        expect(page.locator('.message.assistant').last).to_contain_text('מה לגבי עמלות')
        reply = query('/chat', {'employee': profile, 'text': 'העמלות שלי הן 0 ש"ח'})
        assert reply['employee']['salary'] == 14200 and reply['employee']['commission'] == 0
        assert reply['awaitingField'] == 'bonus' and reply['mode'] == 'local-rules'
        assert 'כתבו 0' not in reply['reply']
        reply = query('/chat', {'employee': profile, 'text': 'אין לי בונוס'})
        assert reply['employee']['bonus'] == 0 and 'bonus' in reply['employee']['knownFields']
        assert reply['awaitingField'] == 'pension' and reply['employee']['salary'] == 14200
    query('/chat', {'employee': alternatives_profile, 'text': 'בחרתי להשוות', 'target': 'CAR-03'})
    reply = query('/chat', {'employee': alternatives_profile, 'text': 'לבחור דגם אחר של רכב חשמלי'})
    assert reply['candidates'] == [] and reply['target'] is None and reply['awaitingField'] == 'alternativeCatalogue'
    assert 'רוצה שאפתח פנייה לנציג' in reply['reply']
    assert not [ticket for ticket in query('/tickets') if json.loads(ticket['employee'])['id'] == alternatives_profile['id']]
    reply = query('/chat', {'employee': alternatives_profile, 'text': 'כן בבקשה'})
    tickets = [ticket for ticket in query('/tickets') if json.loads(ticket['employee'])['id'] == alternatives_profile['id']]
    assert len(tickets) == 1 and 'חשמלית' in tickets[0]['question'] and tickets[0]['status'] == 'pending'
    assert 'פתחתי פנייה' in reply['reply'] and reply['target'] is None
    # Reject an OFFERED vehicle, without comparing it first; a status detour must retain the offer.
    reply = query('/chat', {'employee': offered_profile, 'text': 'איזה רכב חשמלי מתאים לי?'})
    assert reply['candidates'] == ['CAR-03'] and reply['target'] is None
    reply = query('/chat', {'employee': offered_profile, 'text': 'יש נתונים חסרים בשכר שלי?'})
    assert 'לא חסר כרגע נתון' in reply['reply'] and str(offered_profile['salary']) not in reply['reply']
    for text in ['אני רוצה רכב אחר', 'אבל אני רוצה רכב אחר']:
        reply = query('/chat', {'employee': offered_profile, 'text': text})
        assert reply['candidates'] == [] and 'אין כרגע רכב חשמלי נוסף' in reply['reply']
        assert 'רוצה שאפתח פנייה לנציג' in reply['reply']
    reply = query('/chat', {'employee': offered_profile, 'text': 'כן בבקשה'})
    assert 'פתחתי פנייה' in reply['reply']
    assert len([ticket for ticket in query('/tickets') if json.loads(ticket['employee'])['id'] == offered_profile['id']]) == 1
    # Free-text names use exact catalogue rows; a thank-you must not run another search.
    employee = draft['employees'][0]
    reply = query('/chat', {'employee': employee, 'text': employee['question']})
    assert reply['target'] == 'CAR-10' and 'הנטו המשוער' in reply['reply']
    reply = query('/chat', {'employee': employee, 'text': 'תודה רבה!'})
    assert 'בשמחה' in reply['reply'] and reply['candidates'] == []
    assert not query('/history/' + employee['id'])[-1].get('candidates')
    # New chat is persisted on the server, keeps approved data/tickets and survives reload.
    page.reload()
    page.get_by_label('בחירת עובד לדוגמה', exact=True).select_option(alternatives_profile['id'])
    page.get_by_role('button', name='פתיחת היועץ החכם', exact=True).click()
    expect(page.locator('.chat-messages')).to_contain_text('פתחתי פנייה')
    before = query('/conversation/' + alternatives_profile['id'])['employee']
    with page.expect_response(lambda response: response.request.method == 'POST' and response.url.endswith('/api/chat/new')) as resetting:
        page.get_by_role('button', name='שיחה חדשה', exact=True).click()
    assert resetting.value.ok
    expect(page.locator('.chat-messages .message.user')).to_have_count(0)
    assert query('/history/' + alternatives_profile['id']) == []
    saved = query('/conversation/' + alternatives_profile['id'])['employee']
    assert saved['salary'] == before['salary'] and saved['chatHistoryStart'] > 0 and saved['advisorState'] is None
    assert len([ticket for ticket in query('/tickets') if json.loads(ticket['employee'])['id'] == alternatives_profile['id']]) == 1
    query('/profile', {'employee': alternatives_profile})  # A stale form cannot restore old context.
    assert query('/history/' + alternatives_profile['id']) == []
    page.reload()
    page.get_by_role('button', name='פתיחת היועץ החכם', exact=True).click()
    expect(page.locator('.chat-messages .message.user')).to_have_count(0)
    expect(page.locator('.chat-messages')).not_to_contain_text('פתחתי פנייה')
    print('PASS: real chat accepts ASCII-quoted currency and labelled salary answers, confirms only the answered field, persists across reloads and advances intake using saved DB context.', flush=True)
