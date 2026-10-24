"""Employee occupation/access onboarding and immediate custom-model deletion; synthetic data only."""
from playwright.sync_api import expect

def check(query,page,nav,screenshots):
    errors=[];page.on('pageerror',lambda error:errors.append(str(error)))
    baseline=query('/admin/configuration')
    nav('ניהול המערכת');page.get_by_role('tab',name='עובדים',exact=True).click()
    page.get_by_role('button',name='הוספת עובד',exact=True).click()
    editor=page.get_by_role('dialog',name='עריכת עובד',exact=True)
    expect(editor.get_by_label('עיסוק העובד',exact=True)).to_have_value('')
    expect(editor).to_contain_text('קבוצת זכאות טרם נקבעה')
    editor.get_by_label('שם',exact=True).fill('Synthetic representative')
    editor.get_by_label('הרשאה בפרופיל עובד',exact=True).select_option('agent')
    editor.get_by_role('button',name='סיום עריכה בטיוטה',exact=True).click()
    expect(page.get_by_label('סיבת שינוי הגדרות',exact=True)).to_have_count(0)
    page.get_by_role('button',name='שמירת נתונים',exact=True).click()
    expect(page.locator('.admin-success')).to_contain_text('הנתונים נשמרו')
    saved=query('/admin/configuration');assert saved['policyRevision']==baseline['policyRevision']
    representative=next(e for e in saved['configuration']['employees'] if e['name']=='Synthetic representative')
    assert representative['group']=='' and representative['occupation']=='' and representative['currentCar'] is None
    incomplete=query('/simulate',{'employee':{**representative,'group':'ד'}})
    assert incomplete['complete'] is False and incomplete['missingFields'][0]=='eligibility' and incomplete['current'] is None and incomplete['results']==[]
    reply=query('/chat',{'employee':representative,'text':'אני בקבוצה ד, איזה רכב חשמלי מתאים לי?'})
    assert reply['status']=='needs_eligibility' and reply['employee']['group']=='' and reply['candidates']==[]
    assert 'טרם נקבעה' in reply['reply'] and '₪' not in reply['reply']
    assert saved['configuration']['employeeAccountRoles'][representative['id']]=='agent'
    # Account UI uses saved assignments, never an arbitrary occupation->role guess.
    accounts=[];created=[]
    def account_route(route):
        if route.request.method=='GET':route.fulfill(json={'items':accounts});return
        body=route.request.post_data_json;created.append(body)
        accounts.append({'id':'synthetic-account','email':body['email'],'role':body['role'],'employeeId':body['employeeId']})
        route.fulfill(json=accounts[-1])
    page.route('**/api/admin/accounts',account_route)
    page.get_by_role('tab',name='חשבונות והרשאות',exact=True).click()
    profile=page.get_by_label('פרופיל לחשבון חדש',exact=True)
    profile.select_option(representative['id'])
    role=page.get_by_label('הרשאת חשבון חדש',exact=True);expect(role).to_have_value('agent');expect(role).to_be_disabled()
    profile.select_option(baseline['configuration']['employees'][0]['id']);expect(role).to_have_value('employee')
    profile.select_option(representative['id']);expect(role).to_have_value('agent')
    page.get_by_label('דוא״ל לחשבון חדש',exact=True).fill('synthetic-agent@example.invalid')
    page.get_by_label('סיסמה לחשבון חדש',exact=True).fill('SYNTHETIC-PASSWORD-123')
    page.get_by_role('button',name='יצירת חשבון',exact=True).click()
    expect(page.locator('.admin-success').filter(has_text='החשבון נוצר')).to_be_visible()
    assert created[0]['role']=='agent' and created[0]['employeeId']==representative['id']
    # Personal missing fields belong in the employee workspace, not the representative center.
    page.get_by_label('בחירת עובד לדוגמה',exact=True).select_option(representative['id'])
    nav('הסקירה שלי')
    missing=page.get_by_role('heading',name='נשלים רק את מה שחסר.',exact=True)
    expect(missing).to_be_visible()
    expect(page.get_by_role('status').filter(has_text='קבוצת הזכאות טרם נקבעה')).to_be_visible()
    assert 'NaN' not in page.locator('body').inner_text()
    nav('מרכז הנציגים');expect(missing).to_have_count(0)
    expect(page.locator('.agent-stats>.panel')).to_have_count(2)
    expect(page.locator('.agent-stats')).not_to_contain_text('Supabase')
    expect(page.locator('.agent-stats')).not_to_contain_text('הפניות והתשובות נשמרות בשרת')
    nav('הסקירה שלי');expect(missing).to_be_visible()
    page.get_by_label('בחירת עובד לדוגמה',exact=True).select_option(baseline['configuration']['employees'][0]['id'])
    nav('ניהול המערכת')
    # A free-text occupation must include explicit eligibility, not inferred financial rules.
    page.get_by_role('tab',name='עובדים',exact=True).click()
    page.get_by_role('button',name='הוספת עובד',exact=True).click()
    editor.get_by_label('שם',exact=True).fill('Synthetic free occupation')
    editor.get_by_label('עיסוק העובד',exact=True).select_option('__new__')
    editor.get_by_label('שם העיסוק החדש',exact=True).fill('עיסוק חדש סינתטי')
    expect(editor.get_by_role('button',name='סיום עריכה בטיוטה',exact=True)).to_be_disabled()
    editor.get_by_label('קבוצת זכאות לעיסוק חדש',exact=True).select_option('א')
    editor.get_by_label('מותר לוותר על רכב בעיסוק החדש',exact=True).check()
    editor.get_by_label('אחזקת רכב לעיסוק חדש',exact=True).fill('321')
    editor.get_by_role('button',name='הוספת העיסוק ושיוכו לעובד',exact=True).click()
    expect(editor.get_by_label('עיסוק העובד',exact=True)).to_have_value('עיסוק חדש סינתטי')
    editor.get_by_role('button',name='סיום עריכה בטיוטה',exact=True).click()
    page.get_by_label('סיבת שינוי הגדרות',exact=True).fill('Approved synthetic occupation eligibility')
    page.get_by_role('button',name='שמירה והפעלה',exact=True).click()
    expect(page.locator('.admin-success')).to_contain_text('נשמרו והופעלו')
    after=query('/admin/configuration')
    occupation=next(o for o in after['configuration']['occupations'] if o['occupation']=='עיסוק חדש סינתטי')
    assert occupation['group']=='א' and occupation['waiver'] and occupation['maintenance']==321
    assert next(e for e in after['configuration']['employees'] if e['name']=='Synthetic free occupation')['occupation']==occupation['occupation']
    # A named occupation can also remain unclassified, without assigning group A by default.
    page.get_by_role('button',name='הוספת עובד',exact=True).click()
    editor.get_by_label('שם',exact=True).fill('Synthetic pending occupation')
    editor.get_by_label('עיסוק העובד',exact=True).select_option('__new__')
    editor.get_by_label('שם העיסוק החדש',exact=True).fill('עיסוק ממתין סינתטי')
    expect(editor.get_by_label('קבוצת זכאות לעיסוק חדש',exact=True)).to_have_value('')
    editor.get_by_role('button',name='הוספת העיסוק ושיוכו לעובד',exact=True).click()
    editor.get_by_role('button',name='סיום עריכה בטיוטה',exact=True).click()
    page.get_by_label('סיבת שינוי הגדרות',exact=True).fill('Pending role has no group yet')
    page.get_by_role('button',name='שמירה והפעלה',exact=True).click()
    expect(page.locator('.admin-success')).to_contain_text('נשמרו והופעלו')
    pending_saved=query('/admin/configuration')
    pending_employee=next(e for e in pending_saved['configuration']['employees'] if e['name']=='Synthetic pending occupation')
    assert pending_employee['occupation']=='עיסוק ממתין סינתטי' and pending_employee['group']==''
    pending_role=next(o for o in pending_saved['configuration']['occupations'] if o['occupation']==pending_employee['occupation'])
    assert pending_role['group']==''
    # A later administrator assignment resolves the saved conversation instead of keeping the old empty role.
    latest=query('/admin/configuration');config=latest['configuration']
    configured=next(e for e in config['employees'] if e['id']==representative['id'])
    known_role=next(o for o in config['occupations'] if o['waiver'] and o['group'])
    configured.update(occupation=known_role['occupation'])
    query('/admin/configuration',{'expectedRevision':latest['revision'],'configuration':config,'note':''})
    resolved=query('/conversation/'+representative['id'])['employee']
    assert resolved['occupation']==known_role['occupation'] and resolved['group']==known_role['group']
    assert 'eligibility' not in query('/simulate',{'employee':resolved})['missingFields']
    after=query('/admin/configuration')
    page.reload();page.wait_for_selector('.vehicle-card');nav('ניהול המערכת')
    # Delete both an incomplete draft custom model and a persisted custom model without refreshing.
    page.get_by_role('tab',name='ספקי AI',exact=True).click()
    def add_other(name):
        page.get_by_role('button',name='הוספת מודל',exact=True).click()
        page.get_by_label('שם המודל בקטלוג',exact=True).fill(name)
        page.locator('select[aria-label^="ספק "]').select_option('other')
    def deleted(name):
        expect(page.locator('.ai-model-list button').filter(has_text=name)).to_have_count(0)
        expect(page.get_by_label('שם המודל בקטלוג',exact=True)).not_to_have_value(name)
        expect(page.get_by_label('תאימות API לספק אחר',exact=True)).to_have_count(0)
    add_other('Unsaved other supplier')
    page.get_by_role('button',name='הסרת מודל',exact=True).click();deleted('Unsaved other supplier')
    add_other('Saved other supplier')
    page.get_by_label('שם ספק לתצוגה',exact=True).fill('Synthetic supplier')
    page.locator('input[aria-label^="מודל "]').fill('synthetic-model')
    page.locator('input[aria-label^="כתובת ספק "]').fill('https://api.custom-supplier.com/v1/')
    page.get_by_label('אישור כתובת ספק ידני',exact=True).check()
    page.get_by_role('button',name='שמירת הגדרות',exact=True).click()
    expect(page.locator('.admin-success')).to_contain_text('הגדרות ה־AI נשמרו')
    page.get_by_role('button',name='הסרת מודל',exact=True).click();deleted('Saved other supplier')
    page.get_by_role('button',name='שמירת הגדרות',exact=True).click()
    expect(page.locator('.admin-success')).to_contain_text('הגדרות ה־AI נשמרו')
    assert all(m['name']!='Saved other supplier' for m in query('/admin/configuration')['configuration']['ai']['models'])
    assert query('/admin/configuration')['policyRevision']==after['policyRevision']
    page.reload();page.wait_for_selector('.vehicle-card');nav('ניהול המערכת')
    assert query('/admin/configuration')['configuration']['employeeAccountRoles'][representative['id']]=='agent'
    page.get_by_role('tab',name='עובדים',exact=True).click()
    page.screenshot(path=str(screenshots/'employee-setup.png'),full_page=True)
    assert not errors,errors
    print('PASS: explicit employee access assignment, automatically matched account permission, free-text occupation with approved eligibility, persistence and immediate deletion of unsaved/persisted Other AI models.',flush=True)
