"""Redaction privacy/browser integration. Synthetic documents, controlled AI response only."""
import json
from copy import deepcopy
import fitz
from playwright.sync_api import expect
from redacted_payslip_fixture import configure,prepare,extract,mock


def check(query,page,nav,screenshots):
    current=query('/admin/configuration');draft=deepcopy(current['configuration']);keys=configure(draft)
    query('/admin/configuration',{'expectedRevision':current['revision'],'configuration':draft,'apiKeys':keys,'note':'Synthetic redaction UI test; no paid provider'})
    page.reload();page.wait_for_selector('.vehicle-card');nav('הנתונים שלי')
    errors=[];requests=[];uploads=[]
    page.on('pageerror',lambda error:errors.append(error.stack or str(error)))
    page.on('request',lambda request:requests.append(request))
    expect(page.get_by_label('מיקום עיבוד התלוש')).to_have_count(0)
    values={'salary':18000,'commission':None,'bonus':0,'pension':6,'fund':None,'credits':2.75,'participation':350}
    handler=mock(page,values,uploads)
    pdf=fitz.open();pdf.set_metadata({'title':'PRIVATE-METADATA','author':'PRIVATE-AUTHOR'})
    for i in range(2):
        p=pdf.new_page(width=612,height=792)
        p.insert_text((60,65),'PRIVATE-ID-123456789',fontsize=12)
        p.insert_text((60,200),'Base salary 18,000',fontsize=14)
    original=pdf.tobytes();pdf.close()
    picker=page.get_by_label('העלאת תלוש שכר',exact=True)
    picker.set_input_files({'name':'PRIVATE-ORIGINAL-NAME.pdf','mimeType':'application/pdf','buffer':original})
    check1=page.get_by_label('בדקתי את עמוד 1',exact=True);expect(check1).to_be_visible()
    send=page.get_by_role('button',name='חילוץ נתונים מהתלוש',exact=True)
    expect(send).to_be_disabled();expect(page.locator('.payslip-consent input')).to_be_disabled()
    create=page.get_by_role('button',name='יצירת עותק בדוק לשליחה',exact=True)
    check1.check();expect(create).to_be_disabled() # page two must also be reviewed
    check1.uncheck()
    canvas=page.get_by_label('סימון אזורים להשחרה בתלוש',exact=True)
    def mask():
        canvas.evaluate("e=>{e.scrollIntoView({block:'start'});window.scrollBy(0,-150)}");box=canvas.bounding_box()
        page.mouse.move(box['x']+box['width']*.04,box['y']+box['height']*.025)
        page.mouse.down();page.mouse.move(box['x']+box['width']*.65,box['y']+box['height']*.13,steps=8);page.mouse.up()
        expect(page.get_by_role('button',name='ביטול השחרה אחרונה',exact=True)).to_be_enabled()
    mask();prepare(page,pages=2)
    # Editing after preparation discards the prepared document and consent.
    page.locator('.payslip-redactor').get_by_role('button',name='העמוד הקודם',exact=True).click()
    page.get_by_role('button',name='ביטול השחרה אחרונה',exact=True).click()
    expect(page.locator('.payslip-consent input')).to_be_disabled();expect(send).to_be_disabled()
    mask();prepare(page,pages=2)
    # The file-selection/redaction/preview workflow must not upload anything.
    assert not [r for r in requests if r.method=='POST' and '/payslip/extract' in r.url]
    extract(page);assert len(uploads)==1
    output=fitz.open(stream=uploads[0],filetype='pdf')
    assert output.page_count==2
    assert all(not p.get_text().strip() for p in output)
    assert not output.metadata.get('author') and not output.metadata.get('title')
    assert b'PRIVATE-ID' not in uploads[0] and b'PRIVATE-METADATA' not in uploads[0] and original!=uploads[0]
    pix=output[0].get_pixmap(alpha=False)
    assert max(pix.pixel(int(pix.width*.3),int(pix.height*.075)))<10 # burned black pixels
    assert min(pix.pixel(int(pix.width*.8),int(pix.height*.3)))>240 # preserved white page area
    output.close()
    expect(page.get_by_label('חילוץ עמלות חודשיות (₪)',exact=True)).to_have_value('')
    page.get_by_label('בחר הכול — אישור שדות התלוש',exact=True).check()
    expect(page.get_by_label('אישור עמלות חודשיות (₪)',exact=True)).not_to_be_checked()
    expect(page.get_by_label('אישור שכר בסיס חודשי (₪)',exact=True)).to_be_checked()
    page.screenshot(path=str(screenshots/'payslip-redacted-copy.png'),full_page=True)
    # PNG and JPEG share the exact same mandatory review/export path, including touch selection.
    source=fitz.open(stream=original,filetype='pdf');pix=source[0].get_pixmap()
    for kind,extension in [('png','png'),('jpeg','jpg')]:
        picker.set_input_files({'name':'PRIVATE-PHOTO.'+extension,'mimeType':'image/'+kind,'buffer':pix.tobytes(kind)})
        expect(page.get_by_label('בדקתי את עמוד 1',exact=True)).to_be_visible()
        # Pointer events use normalized coordinates, including on a narrow display.
        if kind=='jpeg':
            page.set_viewport_size({'width':390,'height':844});page.wait_for_timeout(250)
            assert not page.evaluate('document.documentElement.scrollWidth>innerWidth')
        mask();prepare(page);extract(page)
        result=fitz.open(stream=uploads[-1],filetype='pdf');assert result.page_count==1 and not result[0].get_text().strip();result.close()
    source.close();page.set_viewport_size({'width':1440,'height':1100})
    assert len(uploads)==3
    page.unroute('**/api/payslip/extract?**',handler)
    # Provider failure returns an explicit error; no local fallback or field modification.
    picker.set_input_files({'name':'failure.png','mimeType':'image/png','buffer':pix.tobytes('png')});prepare(page)
    failed=[]
    def failure(route):
        failed.append(route.request.url);route.fulfill(status=503,content_type='application/json',body=json.dumps({'error':'לא הצלחנו לחלץ את הנתונים כרגע. אפשר לנסות שוב או להזין ידנית.','details':'SYNTHETIC ADMIN DIAGNOSTIC','code':'insufficient_quota'}))
    page.route('**/api/payslip/extract?**',failure);send.click()
    expect(page.locator('.payslip-panel .error-banner')).to_contain_text('לא הצלחנו לחלץ')
    expect(page.locator('.payslip-panel .error-banner')).not_to_contain_text('SYNTHETIC ADMIN DIAGNOSTIC')
    expect(page.locator('.payslip-review')).to_have_count(0);assert len(failed)==1
    page.unroute('**/api/payslip/extract?**',failure)
    # Server itself rejects missing consent, even if a caller bypasses the UI.
    response=page.request.post(page.url.split('/')[0]+'//'+page.url.split('/')[2]+'/api/payslip/extract?employee='+query('/data')['employees'][0]['id'],data=b'%PDF-1.4 test')
    assert response.status==400
    # Oversized page-count documents cannot be exported or sent.
    many=fitz.open()
    for _ in range(7):many.new_page()
    picker.set_input_files({'name':'too-many-pages.pdf','mimeType':'application/pdf','buffer':many.tobytes()});many.close()
    expect(page.locator('.payslip-redactor [role=alert]')).to_contain_text('6 עמודים')
    expect(send).to_be_disabled()
    # The administrator can choose the document model and persist the actual prompt used by the server.
    nav('ניהול המערכת');page.get_by_role('tab',name='ספקי AI',exact=True).click()
    expect(page.get_by_label('מודל לשירות payslip',exact=True)).to_have_value('document-fixture')
    expect(page.get_by_label('תמיכת המודל במסמכים',exact=True)).to_be_checked()
    page.get_by_role('tab',name='פרומפטים ותלושים',exact=True).click()
    prompt='חלץ נתונים חודשיים בלבד — הנחיה סינתטית לבדיקת שמירה'
    page.get_by_label('הנחיות לחילוץ תלושים',exact=True).fill(prompt)
    page.get_by_label('סיבת שינוי הגדרות',exact=True).fill('Synthetic payslip prompt UI persistence')
    page.get_by_role('button',name='שמירה והפעלה',exact=True).click()
    expect(page.get_by_label('סיבת שינוי הגדרות',exact=True)).to_have_count(0)
    assert query('/admin/configuration')['configuration']['ai']['payslipPrompt']==prompt
    assert not errors,errors
    external=[r.url for r in requests if r.url.startswith('http') and not r.url.startswith(page.url.split('/')[0]+'//'+page.url.split('/')[2])]
    assert not external,external
    report={'passed':True,'sourceStayedLocalBeforeApproval':True,'redactionsBurnedIntoPixels':True,'noOriginalPdfTextOrMetadata':True,
            'allPagesReviewRequired':True,'editingRevokesConsent':True,'formats':['PDF','PNG','JPEG'],'controlledExtractions':3,
            'provider':'mocked UI response; adapters tested in .NET; no paid AI accuracy claim','browserErrors':errors}
    (screenshots/'payslip-redaction.json').write_text(json.dumps(report,indent=2)+'\n')
    print('PASS: all-page review, burned redaction, image-only PDF without original text/metadata, no upload before approval, consent revocation, PDF/PNG/JPEG/mobile, partial fields and explicit provider failure.',flush=True)
