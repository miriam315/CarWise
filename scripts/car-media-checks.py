"""Synthetic test pictures only; never seeds them into the real catalog."""
import json
import struct
import urllib.request
import urllib.error
import zlib
from playwright.sync_api import expect


def png(color):
    def chunk(kind, value):
        return struct.pack('>I', len(value)) + kind + value + struct.pack('>I', zlib.crc32(kind + value))
    width, height = 32, 24
    rows = b''.join(b'\0' + bytes(color) * width for _ in range(height))
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b'')


def check(query, page, nav, screenshots):
    nav('ניהול המערכת')
    page.get_by_role('tab', name='קטלוג רכבים', exact=True).click()
    # Import several vehicles together; unmatched names require an explicit mapping.
    bulk=page.locator('.car-media-batch')
    bulk.get_by_label('תמונות לייבוא מרוכז',exact=True).set_input_files([
        {'name':'CAR-02__exterior__01.png','mimeType':'image/png','buffer':png((20,90,140))},
        {'name':'CAR-03__interior__01.png','mimeType':'image/png','buffer':png((30,100,150))},
        {'name':'unmatched.png','mimeType':'image/png','buffer':png((40,110,160))}])
    expect(bulk.get_by_label('שיוך רכב לתמונה 1',exact=True)).to_have_value('CAR-02')
    expect(bulk.get_by_label('סוג תמונה בייבוא 2',exact=True)).to_have_value('interior')
    bulk.get_by_label('קרדיט לייבוא תמונות מרוכז',exact=True).fill('SYNTHETIC BULK TEST IMAGES')
    bulk.get_by_label('הרשאת שימוש בייבוא מרוכז',exact=True).check()
    expect(bulk.get_by_role('button',name='העלאת התמונות המשויכות',exact=True)).to_be_disabled()
    bulk.get_by_label('שיוך רכב לתמונה 3',exact=True).select_option('CAR-04')
    bulk.get_by_role('button',name='העלאת התמונות המשויכות',exact=True).click()
    expect(bulk.locator('[role=status]')).to_contain_text('3 תמונות עובדו וצורפו לטיוטה',timeout=30000)
    page.get_by_role('button', name='עריכת רכב PICANTO', exact=True).click()
    picker = page.get_by_label('העלאת תמונות לרכב', exact=True)
    expect(picker).to_be_disabled()
    page.get_by_label('מקור וקרדיט לתמונות', exact=True).fill('SYNTHETIC TEST IMAGES — isolated database only')
    page.get_by_label('אני מאשר/ת שיש לארגון הרשאה לפרסם את התמונות.', exact=True).check()

    def add(view, colors):
        page.get_by_label('סוג תמונות להעלאה', exact=True).select_option(view)
        picker.set_input_files([{'name': f'frame-{index:02}.png', 'mimeType': 'image/png', 'buffer': png(color)} for index, color in enumerate(colors)])
        expect(page.locator('.car-media-editor [role=status]')).to_contain_text('התמונות צורפו לטיוטה', timeout=30000)

    # Controlled Commons responses: no third-party traffic or external photos in tests.
    page.route('**/api/admin/car-images/commons?*',lambda route:route.fulfill(status=503,json={'error':'Wikimedia אינו נגיש כרגע — בדיקת כשל מקור'}))
    page.get_by_role('button',name='חיפוש תמונות בויקימדיה',exact=True).click()
    expect(page.locator('.commons-car-picker [role=alert]')).to_contain_text('בדיקת כשל מקור')
    page.unroute('**/api/admin/car-images/commons?*')
    item={'pageId':123,'title':'File:Synthetic test vehicle.png','credit':'SYNTHETIC TEST IMAGES — isolated database only','source':'https://commons.wikimedia.org/wiki/File:Synthetic_test_vehicle.png','license':'CC0','mime':'image/png'}
    page.route('**/api/admin/car-images/commons?*',lambda route:route.fulfill(json=[item]))
    page.route('**/api/admin/car-images/commons/123',lambda route:route.fulfill(content_type='image/png',body=png((30,100,170))))
    page.get_by_role('button',name='חיפוש תמונות בויקימדיה',exact=True).click()
    page.locator('.commons-car-results button').click()
    expect(page.locator('.commons-car-preview img')).to_be_visible()
    page.get_by_role('button',name='הוספת התמונה לרכב',exact=True).click()
    expect(page.locator('.car-media-editor [role=status]')).to_contain_text('התמונות צורפו לטיוטה',timeout=30000)
    page.unroute('**/api/admin/car-images/commons?*')
    page.unroute('**/api/admin/car-images/commons/123')
    # A genuinely empty search and a metadata rejection must have different messages.
    page.route('**/api/admin/car-images/commons?*',lambda route:route.fulfill(json=[]))
    page.get_by_role('button',name='חיפוש תמונות בויקימדיה',exact=True).click()
    expect(page.locator('.commons-car-picker [role=status]')).to_contain_text('לא נמצאו תוצאות לחיפוש הזה')
    expect(page.locator('.commons-car-results button')).to_have_count(0)
    page.unroute('**/api/admin/car-images/commons?*')
    page.route('**/api/admin/car-images/commons?*',lambda route:route.fulfill(status=400,json={'error':'נמצאו 24 תוצאות אך לא ניתן לייבא אותן — בדיקת סינון'}))
    page.get_by_role('button',name='חיפוש תמונות בויקימדיה',exact=True).click()
    expect(page.locator('.commons-car-picker [role=alert]')).to_contain_text('בדיקת סינון')
    expect(page.locator('.commons-car-picker [role=status]')).to_have_count(0)
    page.unroute('**/api/admin/car-images/commons?*')
    add('interior', [(180, 100, 30)])
    add('spin', [(i * 17, 60, 110) for i in range(11)])
    page.get_by_role('button', name='הגדלת תמונות PICANTO', exact=True).click()
    viewer = page.get_by_role('dialog', name='תמונות PICANTO', exact=True)
    expect(viewer.get_by_role('tab', name='360°', exact=True)).to_be_disabled()
    viewer.get_by_role('button', name='סגירת גלריית הרכב', exact=True).click()
    add('spin', [(220, 60, 110)])
    page.get_by_role('button', name='סיום עריכה בטיוטה', exact=False).click()
    expect(page.get_by_label('סיבת שינוי הגדרות',exact=True)).to_have_count(0)
    before_save=query('/admin/configuration')
    page.get_by_role('button', name='שמירת נתונים', exact=True).click()
    expect(page.get_by_text('הנתונים נשמרו. גרסת המדיניות לא השתנתה.', exact=True)).to_be_visible()
    after_save=query('/admin/configuration')
    assert after_save['policyRevision']==before_save['policyRevision'] and after_save['history']==before_save['history']
    car = next(c for c in query('/data')['cars'] if c['id'] == 'CAR-01')
    assert len(car['photos']) == 14
    assert len({p['id'] for p in car['photos'] if p['view'] == 'spin'}) == 12

    nav('קטלוג הרכבים')
    # Employee views contain only available media, never upload/setup instructions.
    second=next(c for c in query('/data')['cars'] if c['id']=='CAR-02')
    assert len(second['photos'])==1 and second['photos'][0]['view']=='exterior'
    assert len(next(c for c in query('/data')['cars'] if c['id']=='CAR-03')['photos'])==1
    assert len(next(c for c in query('/data')['cars'] if c['id']=='CAR-04')['photos'])==1
    second_card=page.locator('.catalog-card').filter(has=page.get_by_role('heading',name=second['model'],exact=True))
    second_card.get_by_role('button',name='הגדלת תמונות '+second['model'],exact=True).click()
    second_viewer=page.get_by_role('dialog',name='תמונות '+second['model'],exact=True)
    expect(second_viewer.get_by_role('tab',name='360°',exact=True)).to_have_count(0)
    expect(second_viewer.get_by_role('tab',name='פנים הרכב',exact=True)).to_have_count(0)
    expect(second_viewer).not_to_contain_text('לפחות 12')
    second_viewer.get_by_role('button',name='סגירת גלריית הרכב',exact=True).click()
    assert 'כללי האתגר' not in page.locator('main').inner_text() and 'רכבי האתגר' not in page.locator('main').inner_text()
    card = page.locator('.catalog-card').filter(has=page.get_by_role('heading', name='PICANTO', exact=True))
    expect(card.locator('.car-photo')).to_be_visible()
    card.get_by_role('button', name='הגדלת תמונות PICANTO', exact=True).click()
    viewer = page.get_by_role('dialog', name='תמונות PICANTO', exact=True)
    viewer.get_by_role('tab', name='פנים הרכב', exact=True).click()
    expect(viewer.locator('.car-gallery-stage img')).to_have_attribute('alt', 'PICANTO — פנים הרכב, תמונה 1')
    viewer.get_by_label('הגדלת תמונת הרכב', exact=True).fill('200')
    assert viewer.locator('.car-gallery-stage img').evaluate('(image)=>image.style.height==="200%"')
    viewer.get_by_role('tab', name='360°', exact=True).click()
    viewer.get_by_label('זווית צפייה ברכב', exact=True).fill('6')
    expect(viewer.locator('.car-gallery-controls')).to_contain_text('180°')
    stage = viewer.locator('.car-gallery-stage').bounding_box()
    page.mouse.move(stage['x'] + 90, stage['y'] + 90)
    page.mouse.down()
    page.mouse.move(stage['x'] + 117, stage['y'] + 90, steps=4)
    page.mouse.up()
    expect(viewer.get_by_label('זווית צפייה ברכב', exact=True)).to_have_value('9')
    expect(viewer.locator('.car-photo-credit')).to_contain_text('SYNTHETIC TEST IMAGES')
    page.set_viewport_size({'width': 390, 'height': 844})
    page.wait_for_function("document.querySelector('.sidebar').getBoundingClientRect().left>=innerWidth-1")
    overflow=page.evaluate('document.documentElement.scrollWidth>innerWidth')
    if overflow:
        print(page.evaluate('Array.from(document.querySelectorAll("body *")).filter(e=>!e.closest(".sidebar")).map(e=>({tag:e.tagName,cls:e.className,left:e.getBoundingClientRect().left,right:e.getBoundingClientRect().right,width:e.getBoundingClientRect().width})).filter(e=>e.right>innerWidth+1||e.left< -1).slice(0,25)'),flush=True)
    assert not overflow
    page.screenshot(path=str(screenshots / 'vehicle-gallery-mobile.png'), full_page=True)
    page.keyboard.press('Escape')
    expect(viewer).to_have_count(0)
    page.reload()
    page.set_viewport_size({'width': 1440, 'height': 1100})
    page.wait_for_selector('.vehicle-card')
    assert next(c for c in query('/data')['cars'] if c['id'] == 'CAR-01')['photos'] == car['photos']

    base = page.url.rstrip('/')
    with urllib.request.urlopen(base + '/media/cars/' + car['photos'][0]['id'] + '.jpg') as response:
        assert response.headers['Content-Type'] == 'image/jpeg'
        assert response.headers['X-Content-Type-Options'] == 'nosniff'
        assert 'immutable' in response.headers['Cache-Control']
        assert response.read().startswith(b'\xff\xd8')
    for suffix, payload in [('?rightsConfirmed=false', b'not an image'), ('?rightsConfirmed=true', b'<svg onload="alert(1)"/>')]:
        try:
            urllib.request.urlopen(urllib.request.Request(base + '/api/admin/car-images' + suffix, data=payload, headers={'Content-Type': 'application/octet-stream'}))
            raise AssertionError('Unsafe upload accepted')
        except urllib.error.HTTPError as error:
            assert error.code == 400
    # Adding an employee is an operational save, with no reason or policy version.
    nav('ניהול המערכת');page.get_by_role('tab',name='עובדים',exact=True).click()
    before_employee=query('/admin/configuration')
    page.get_by_role('button',name='הוספת עובד',exact=True).click()
    employee_dialog=page.get_by_role('dialog',name='עריכת עובד',exact=True)
    employee_dialog.get_by_label('שם',exact=True).fill('עובד בדיקה תפעולי')
    employee_dialog.get_by_role('button',name='סיום עריכה בטיוטה',exact=False).click()
    expect(page.get_by_label('סיבת שינוי הגדרות',exact=True)).to_have_count(0)
    page.get_by_role('button',name='שמירת נתונים',exact=True).click()
    expect(page.get_by_text('הנתונים נשמרו. גרסת המדיניות לא השתנתה.',exact=True)).to_be_visible()
    after_employee=query('/admin/configuration')
    assert after_employee['policyRevision']==before_employee['policyRevision']
    assert after_employee['history']==before_employee['history']
    assert len(after_employee['configuration']['employees'])==len(before_employee['configuration']['employees'])+1
    assert after_employee['operations'][0]['employeeIds']
    page.reload();page.wait_for_selector('.vehicle-card')
    assert len(query('/data')['employees'])==len(after_employee['configuration']['employees'])
    print('PASS: admin image normalization/upload, persisted exterior/interior galleries, minimum 360 frames, zoom, drag, mobile, metadata attribution and unsafe-upload rejection.', flush=True)
