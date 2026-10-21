"""Browser checks for local previews, file drops and camera with OCR unavailable."""
import base64
from pathlib import Path
from playwright.sync_api import expect

def check(page,nav,pdf,png,jpeg,multipage,screenshots):
 posts=[]
 def capture(request):
  if request.method=='POST' and '/payslip/extract' in request.url:posts.append(request.url)
 page.on('request',capture)
 def unavailable(route):route.fulfill(json={'enabled':False,'code':'sandbox_unavailable','message':'מנוע החילוץ אינו זמין — בדיקת ממשק'})
 page.route('**/api/payslip/status',unavailable)
 try:
  page.reload();page.wait_for_selector('.vehicle-card');nav('הנתונים שלי')
  expect(page.locator('.payslip-panel .form-info')).to_contain_text('בדיקת ממשק')
  expect(page.get_by_role('button',name='צילום תלוש',exact=True)).to_be_enabled()
  page.evaluate("""() => {window.revokedPayslipUrls=[];const original=URL.revokeObjectURL.bind(URL);
   URL.revokeObjectURL=url=>{window.revokedPayslipUrls.push(url);original(url)};}""")
  zone=page.get_by_label('אזור גרירת תלוש',exact=True)
  def drop(files):
   zone.evaluate("""(element,files)=>{const transfer=new DataTransfer();for(const file of files){
    const bytes=Uint8Array.from(atob(file.data),c=>c.charCodeAt(0));transfer.items.add(new File([bytes],file.name,{type:file.type}));}
    for(const type of ['dragenter','dragover','drop'])element.dispatchEvent(new DragEvent(type,{bubbles:true,cancelable:true,dataTransfer:transfer}));} """,
    [{'name':name,'type':kind,'data':base64.b64encode(data).decode()} for name,kind,data in files])
  drop([('multipage-test.pdf','application/pdf',multipage)])
  expect(page.get_by_role('button',name='העמוד הבא',exact=True)).to_be_enabled(timeout=30000)
  expect(page.get_by_text('עמוד 1 מתוך 4',exact=True)).to_be_visible()
  assert page.locator('.payslip-preview canvas').evaluate('(canvas)=>canvas.width>500 && canvas.height>500')
  page.get_by_role('button',name='העמוד הבא',exact=True).click()
  expect(page.get_by_text('עמוד 2 מתוך 4',exact=True)).to_be_visible()
  page.get_by_role('button',name='הגדלת תצוגת התלוש').click()
  assert page.locator('.payslip-preview canvas').evaluate('(canvas)=>canvas.style.width==="125%"')
  expect(page.get_by_role('button',name='חילוץ נתונים מהתלוש',exact=True)).to_be_disabled()
  page.screenshot(path=str(screenshots/'payslip-drop-pdf-preview.png'),full_page=True)
  # Embedded Hebrew fonts and a dense table must render locally as well.
  complex_pdf=Path(__file__).resolve().parent.parent/'examples/payslips/complex/01-complex-monthly.pdf'
  page.get_by_label('העלאת תלוש שכר').set_input_files(str(complex_pdf))
  expect(page.get_by_text('עמוד 1 מתוך 1',exact=True)).to_be_visible()
  expect(page.locator('.payslip-preview').get_by_role('status')).to_have_count(0,timeout=20000)
  assert page.locator('.payslip-preview canvas').evaluate('(canvas)=>{const data=canvas.getContext("2d").getImageData(0,0,canvas.width,canvas.height).data;let ink=0;for(let i=0;i<data.length;i+=4)if(data[i]<180)ink++;return ink>1000}')
  page.screenshot(path=str(screenshots/'payslip-complex-local-preview.png'),full_page=True)
  page.get_by_label('העלאת תלוש שכר').set_input_files({'name':'broken.pdf','mimeType':'application/pdf','buffer':b'%PDF-1.7 INVALID SYNTHETIC PDF'})
  expect(page.locator('.payslip-preview [role=alert]')).to_contain_text('לא ניתן להציג',timeout=20000)

  page.get_by_label('העלאת תלוש שכר').set_input_files({'name':'image-test.png','mimeType':'image/png','buffer':png})
  preview=page.get_by_alt_text('תצוגה מקדימה של התלוש שנבחר')
  expect(preview).to_be_visible();assert preview.evaluate('(image)=>image.complete && image.naturalWidth>0')
  expect(page.locator('.payslip-preview canvas')).to_have_count(0)
  old=preview.get_attribute('src');page.get_by_role('button',name='הסרת התלוש',exact=True).click()
  expect(page.get_by_label('תצוגה מקדימה של התלוש',exact=True)).to_have_count(0)
  assert page.evaluate('(url)=>window.revokedPayslipUrls.includes(url)',old)
  page.get_by_label('העלאת תלוש שכר').set_input_files({'name':'fake.pdf','mimeType':'application/pdf','buffer':b'NOT A REAL DOCUMENT'})
  expect(page.locator('.payslip-panel [role=alert]')).to_contain_text('תקינים בלבד')
  drop([('first.pdf','application/pdf',pdf),('second.pdf','application/pdf',pdf)])
  expect(page.locator('.payslip-panel [role=alert]')).to_contain_text('תלוש אחד')
  page.get_by_label('העלאת תלוש שכר').set_input_files({'name':'too-large.pdf','mimeType':'application/pdf','buffer':b'%PDF-'+b'0'*(8*1024*1024)})
  expect(page.locator('.payslip-panel [role=alert]')).to_contain_text('8MB')
  # The phone capture picker remains available even when live camera access fails.
  page.evaluate("() => {navigator.mediaDevices.getUserMedia=async()=>{throw new DOMException('Denied','NotAllowedError')};}")
  page.get_by_role('button',name='צילום תלוש',exact=True).click()
  expect(page.get_by_role('dialog')).to_contain_text('לא ניתנה הרשאה למצלמה')
  page.get_by_label('צילום תלוש דרך המכשיר').set_input_files({'name':'phone-photo.jpg','mimeType':'image/jpeg','buffer':jpeg})
  expect(page.get_by_role('dialog')).to_have_count(0)
  expect(page.get_by_alt_text('תצוגה מקדימה של התלוש שנבחר')).to_be_visible()
  page.set_viewport_size({'width':390,'height':844})
  page.wait_for_function("document.querySelector('.sidebar').getBoundingClientRect().left >= innerWidth - 1")
  assert not page.evaluate('document.documentElement.scrollWidth>innerWidth')
  page.screenshot(path=str(screenshots/'payslip-image-preview-mobile.png'),full_page=True)
  assert not posts,posts
  old=page.get_by_alt_text('תצוגה מקדימה של התלוש שנבחר').get_attribute('src')
  page.set_viewport_size({'width':1440,'height':1100})
  nav('הסקירה שלי');expect(page.get_by_label('תצוגה מקדימה של התלוש',exact=True)).to_have_count(0)
  assert page.evaluate('(url)=>window.revokedPayslipUrls.includes(url)',old)
 finally:
  page.remove_listener('request',capture);page.unroute('**/api/payslip/status',unavailable)
  page.set_viewport_size({'width':1440,'height':1100});page.reload();page.wait_for_selector('.vehicle-card')
 print('PASS: camera visible without OCR; PDF drop and local page rendering/navigation/zoom; image/phone preview; invalid, oversized and multiple files rejected; no extraction POST before consent; mobile layout and preview URL cleanup.')
