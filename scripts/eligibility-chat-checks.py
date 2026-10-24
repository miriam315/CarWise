"""Missing-group chat, durable source evidence, real representative continuation and reply UX."""
from copy import deepcopy
from playwright.sync_api import expect


def check(query,page,nav,screenshots):
    current=query('/admin/configuration');draft=deepcopy(current['configuration']);draft['ai']['enabled']=False
    base=deepcopy(draft['employees'][0]);first={**base,'id':'E-flow-group','name':'בדיקת קבוצה בשיחה','occupation':'','group':'','knownFields':[]}
    second={**base,'id':'E-flow-agent','name':'בדיקת קבוצת נציג','occupation':'','group':'','knownFields':None}
    draft['employees'].extend([first,second])
    query('/admin/configuration',{'expectedRevision':current['revision'],'configuration':draft,'note':''})
    page.emulate_media(reduced_motion='reduce');page.reload();page.wait_for_selector('.vehicle-card')
    errors=[];page.on('pageerror',lambda error:errors.append(str(error)))
    page.get_by_label('בחירת עובד לדוגמה',exact=True).select_option(first['id'])
    page.get_by_role('button',name='פתיחת היועץ החכם',exact=True).click()
    def say(text):
        page.get_by_label('הודעה ליועץ',exact=True).fill(text)
        with page.expect_response(lambda r:r.request.method=='POST' and r.url.endswith('/api/chat')) as sent:
            page.get_by_role('button',name='שליחת הודעה',exact=True).click()
        assert sent.value.ok,(sent.value.status,sent.value.text())
        result=sent.value.json()
        expect(page.locator('.chat-progress')).to_have_count(0)
        expect(page.locator('.chat-messages .message.assistant').last).to_contain_text(result['reply'])
        return result
    reply=say('ארצה להשוות את ATTO 3');assert reply['awaitingField']=='salary'
    for text,field in [('14200','commission'),('1000','bonus'),('אין','pension'),('6','fund'),('2.5','credits'),('2.25','children'),('אין ילדים','eligibility')]:
        reply=say(text);assert reply['awaitingField']==field,(text,reply)
    reply=say('אני בקבוצה ג');assert reply['employee']['group']=='ג' and reply['employee']['eligibilitySource']=='employee' and reply['awaitingField']=='currentCar'
    reply=say('קורולה קרוס');assert reply['awaitingField']=='participation'
    reply=say('300');assert 'הנטו' in reply['reply'] and reply['target']=='CAR-10'
    restored=query('/conversation/'+first['id'])['employee']
    sim=query('/simulate',{'employee':{**restored,'group':'ד','eligibilitySource':'representative'}})
    assert sim['complete'] and sim['employee']['group']=='ג' and sim['employee']['eligibilitySource']=='employee'
    page.reload();page.get_by_role('button',name='פתיחת היועץ החכם',exact=True).click()
    expect(page.locator('.chat-messages')).to_contain_text('הנטו')
    page.get_by_role('button',name='סגירת היועץ',exact=True).click()
    page.get_by_label('בחירת עובד לדוגמה',exact=True).select_option(second['id'])
    page.get_by_role('button',name='פתיחת היועץ החכם',exact=True).click()
    reply=say('ארצה להשוות את ATTO 3');assert reply['awaitingField']=='eligibility'
    reply=say('לא יודעת');assert reply['status']=='needs_representative'
    say('לא יודע')
    tickets=[t for t in query('/tickets') if t['target']=='eligibility:group' and second['id'] in t['employee']]
    assert len(tickets)==1
    nav('מרכז הנציגים');card=page.locator('.ticket-card').filter(has_text=second['name'])
    expect(card).to_contain_text('בירור קבוצת זכאות')
    expect(card.get_by_label('השתתפות עובד מאושרת (₪)',exact=True)).to_have_count(0)
    card.get_by_label('קבוצת זכאות מאושרת',exact=True).select_option('ג')
    card.get_by_label('תשובת הנציג',exact=True).fill('הקבוצה נבדקה ואושרה')
    with page.expect_response(lambda r:r.request.method=='POST' and '/api/tickets/' in r.url) as answered:
        card.get_by_role('button',name='שליחת תשובה והמשך השיחה',exact=True).click()
    assert answered.value.ok,answered.value.text()
    saved=query('/conversation/'+second['id'])
    assert saved['employee']['group']=='ג' and saved['employee']['eligibilitySource']=='representative'
    assert saved['target']=='CAR-10' and saved['employee']['salary']==second['salary']
    nav('הסקירה שלי');page.get_by_role('button',name='פתיחת היועץ החכם',exact=True).click()
    expect(page.locator('.chat-messages')).to_contain_text('הנציג ענה')
    expect(page.locator('.chat-messages .message.assistant').last).to_contain_text('הנטו')
    assert 'שכר הבסיס' not in query('/history/'+second['id'])[-1]['text']
    # A resolved ticket cannot overwrite evidence or append another continuation on retry.
    before=query('/history/'+second['id']);query('/tickets/'+tickets[0]['id'],{'participation':0,'answer':'duplicate','group':'א'})
    assert query('/conversation/'+second['id'])['employee']['group']=='ג' and query('/history/'+second['id'])==before
    page.screenshot(path=str(screenshots/'eligibility-agent-resumed.png'),full_page=True)
    reply=say('אני בקבוצה ד');assert reply['employee']['group']=='ג' and 'לא אשנה' in reply['reply']
    # Controlled latency exercises waiting and progressive display, without a paid model call.
    page.emulate_media(reduced_motion='no-preference');held=[]
    def pending(route):held.append(route)
    page.route('**/api/chat',pending)
    page.get_by_label('הודעה ליועץ',exact=True).fill('בדיקת חיווי')
    page.get_by_role('button',name='שליחת הודעה',exact=True).click()
    progress=page.locator('.chat-progress');expect(progress).to_contain_text('היועץ מכין תשובה')
    expect(progress).to_contain_text('חלפו',timeout=4000)
    page.set_viewport_size({'width':390,'height':844})
    expect(progress).to_be_visible();assert page.evaluate('document.documentElement.scrollWidth<=innerWidth+1')
    page.screenshot(path=str(screenshots/'chat-thinking-mobile.png'),full_page=True)
    text='זו בדיקת תצוגה מבוקרת של תשובה שמופיעה בהדרגה. '*30
    held[0].fulfill(json={'reply':text,'employee':saved['employee'],'candidates':[],'target':None})
    expect(progress).to_have_count(0)
    controls=page.locator('.chat-reply-controls');expect(controls).to_be_visible()
    shown=page.locator('.chat-messages .message.assistant').last.locator('.chat-reply-text')
    expect(shown).not_to_have_text('')
    assert 0<len(shown.inner_text())<len(text)
    page.screenshot(path=str(screenshots/'chat-typing-mobile.png'),full_page=True)
    controls.get_by_role('button',name='הצג הכול',exact=True).click()
    expect(shown).to_have_text(text);expect(controls).to_have_count(0)
    page.unroute('**/api/chat',pending)
    page.emulate_media(reduced_motion='reduce')
    page.route('**/api/chat',lambda route:route.fulfill(json={'reply':'תשובה מלאה ללא אנימציה','employee':saved['employee'],'candidates':[],'target':None}))
    say('בדיקת נגישות');expect(page.locator('.chat-reply-controls')).to_have_count(0)
    page.unroute('**/api/chat')
    page.route('**/api/chat',lambda route:route.fulfill(status=503,json={'error':'הבקשה לא הושלמה. אפשר לנסות שוב.'}))
    page.get_by_label('הודעה ליועץ',exact=True).fill('בדיקת תקלה')
    page.get_by_role('button',name='שליחת הודעה',exact=True).click()
    expect(page.locator('.chat-error')).to_contain_text('אפשר לנסות שוב')
    expect(page.locator('.chat-progress')).to_have_count(0)
    page.get_by_label('הודעה ליועץ',exact=True).fill('אפשר לנסות שוב')
    expect(page.get_by_role('button',name='שליחת הודעה',exact=True)).to_be_enabled()
    page.unroute('**/api/chat')
    assert not errors,errors
    print('PASS: salary-first missing inputs, reported eligibility with provenance, durable server-only evidence, no group spoofing, one real referral, representative priority and idempotent same-goal continuation; waiting, progressive reply, reveal-all, reduced motion and mobile.',flush=True)
