"""Saved payroll intake answers through the real UI and disposable PostgreSQL."""
from copy import deepcopy
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
    query('/admin/configuration', {'expectedRevision': config['revision'], 'configuration': draft, 'note': ''})
    for profile, text in zip(profiles, ['14200 ש"ח', 'השכר שלי 14200 ש"ח']):
        page.reload()
        expect(page.get_by_label('בחירת עובד לדוגמה', exact=True)).to_be_visible()
        page.get_by_label('בחירת עובד לדוגמה', exact=True).select_option(profile['id'])
        page.get_by_role('button', name='השלמת נתונים בשיחה', exact=True).click()
        expect(page.locator('.message.assistant').last).to_contain_text('מה שכר הבסיס החודשי')
        page.get_by_label('הודעה ליועץ', exact=True).fill(text)
        page.get_by_role('button', name='שליחת הודעה', exact=True).click()
        expect(page.locator('.message.assistant').last).to_contain_text('מה סכום העמלות')
        saved = query('/conversation/' + profile['id'])['employee']
        assert saved['salary'] == 14200 and 'salary' in saved['knownFields']
        assert saved['advisorState']['awaitingField'] == 'commission'
        # A fresh browser state and a stale original employee payload must use saved DB knowledge.
        page.reload()
        expect(page.get_by_label('בחירת עובד לדוגמה', exact=True)).to_be_visible()
        page.get_by_role('button', name='השלמת נתונים בשיחה', exact=True).click()
        expect(page.locator('.message.assistant').last).to_contain_text('מה סכום העמלות')
        reply = query('/chat', {'employee': profile, 'text': 'העמלות שלי הן 0 ש"ח'})
        assert reply['employee']['salary'] == 14200 and reply['employee']['commission'] == 0
        assert reply['awaitingField'] == 'bonus' and reply['mode'] == 'local-rules'
    print('PASS: real chat accepts ASCII-quoted currency and labelled salary answers, confirms only the answered field, persists across reloads and advances intake using saved DB context.', flush=True)
