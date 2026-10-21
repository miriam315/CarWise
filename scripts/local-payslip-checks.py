"""Synthetic local OCR integration checks. Never uses a real payslip or paid AI API."""
from io import BytesIO
from zipfile import ZipFile
from copy import deepcopy
import json
from PIL import Image, ImageDraw, ImageFont
from reportlab.pdfgen import canvas
from reportlab.lib.utils import ImageReader
from playwright.sync_api import expect

def fixtures():
 rows=['FICTIONAL PERSON - DO NOT STORE','ID: 999123456','Bank account: 999887776',
       'BASE SALARY: 19000.25','Commission: 1000','Bonus: 0','Employee pension: 6%',
       'Employee study fund: 2.5%','Tax credit points: 2.25','Employee car participation: 350']
 pdf=BytesIO();c=canvas.Canvas(pdf,pagesize=(700,900));c.setFont('Helvetica',20)
 for index,row in enumerate(rows):c.drawString(45,850-index*55,row)
 c.save()
 image=Image.new('RGB',(2000,1900),'white');draw=ImageDraw.Draw(image)
 font=ImageFont.truetype('/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf',48)
 for index,row in enumerate(rows):draw.text((60,60+index*140),row,font=font,fill='black')
 hebrew=Image.new('RGB',(1800,1500),'white');hebrew_draw=ImageDraw.Draw(hebrew)
 hebrew_font=ImageFont.truetype('/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf',60)
 for index,row in enumerate(['שכר בסיס: 19000.25','עמלות: 1000','בונוס: 1200','פנסיית עובד: 6%','נקודות זיכוי: 2.25']):
  hebrew_draw.text((1700,60+index*170),row,font=hebrew_font,fill='black',direction='rtl',anchor='ra')
 png=BytesIO();hebrew.save(png,format='PNG')
 jpeg=BytesIO();image.save(jpeg,format='JPEG',quality=95)
 scanned=BytesIO();c=canvas.Canvas(scanned,pagesize=(700,900));c.drawImage(ImageReader(image),0,0,width=700,height=900);c.save()
 large=BytesIO();c=canvas.Canvas(large)
 for _ in range(4):c.drawString(40,700,'Too many pages - synthetic');c.showPage()
 c.save()
 return pdf.getvalue(),png.getvalue(),scanned.getvalue(),jpeg.getvalue(),large.getvalue()

def check(page,query,upload,nav,screenshots):
 baseline=query('/admin/configuration');config=deepcopy(baseline['configuration'])
 config['ai'].update(enabled=False,payslipEnabled=True,payslipOcr={'languages':'heb+eng','maxPages':3,'timeoutSeconds':90,'minimumConfidence':75})
 saved=query('/admin/configuration',{'expectedRevision':baseline['revision'],'configuration':config,'note':'synthetic local OCR verification, no external AI'})
 assert query('/payslip/status')['enabled'],query('/payslip/status')
 pdf,png,scanned,jpeg,large=fixtures();usage_before=query('/admin/usage')['items']
 from importlib.machinery import SourceFileLoader
 SourceFileLoader('payslip_upload_ui_checks',str(screenshots.parent.parent/'scripts/payslip-upload-ui-checks.py')).load_module().check(page,nav,pdf,png,jpeg,large,screenshots)
 def extract(data):
  result=upload('/payslip/extract?employee=E-001&consent=true',data)
  serialized=json.dumps(result,ensure_ascii=False)
  assert all(marker not in serialized for marker in ['FICTIONAL PERSON','999123456','999887776','Bank account'])
  assert set(result['fields'])=={'salary','commission','bonus','pension','fund','credits','participation'}
  assert result['fields']['salary']['value']==19000.25,result
  return result
 try:
  digital=extract(pdf);assert digital['source']=='local-pdf-text'
  picture=extract(png);assert picture['source']=='local-ocr';assert picture['fields']['bonus']['value']==1200,picture
  raster=extract(scanned);assert raster['source']=='local-ocr'
  # Fourth native request verifies phone-style JPEG input without a mock provider.
  assert extract(jpeg)['source']=='local-ocr'
  assert len(query('/admin/usage')['items'])==len(usage_before)
  before=query('/conversation/E-001')['employee']
  page.reload();page.wait_for_selector('.vehicle-card');nav('הנתונים שלי')
  with ZipFile(screenshots.parent.parent/'examples/payslips/complex/CarWise-complex-demo-payslips.zip') as archive:
   dense=archive.read(next(name for name in archive.namelist() if name.endswith('/01-complex-monthly.pdf') or name=='01-complex-monthly.pdf'))
  page.get_by_label('העלאת תלוש שכר').set_input_files({'name':'synthetic-dense-payslip.pdf','mimeType':'application/pdf','buffer':dense})
  page.get_by_label('מיקום עיבוד התלוש').select_option('server')
  page.locator('.payslip-consent input').check();page.get_by_role('button',name='חילוץ נתונים מהתלוש',exact=True).click()
  expect(page.get_by_label('חילוץ שכר בסיס חודשי (₪)',exact=True)).to_have_value('19000.25',timeout=30000)
  expect(page.locator('.payslip-review [role=status]')).to_contain_text('זוהו 7 מתוך 7 שדות')
  for label,value in [('עמלות חודשיות (₪)','1000'),('בונוס חודשי (₪)','0'),('פנסיית עובד (%)','6'),('קרן השתלמות עובד (%)','2.5'),('נקודות זיכוי','2.25'),('השתתפות עובד ברכב (₪)','350')]:
   expect(page.get_by_label('חילוץ '+label,exact=True)).to_have_value(value)
  expect(page.get_by_label('תצוגה מקדימה של התלוש',exact=True)).to_be_visible()
  expect(page.get_by_role('button',name='העמוד הבא',exact=True)).to_be_disabled()
  assert query('/conversation/E-001')['employee']['salary']==before['salary'] # Extraction is read-only.
  expect(page.get_by_label('אישור שכר בסיס חודשי (₪)',exact=True)).not_to_be_checked()
  page.get_by_role('button',name='אישור והעברת השדות המסומנים לטופס').click()
  expect(page.locator('.payslip-panel [role=alert]')).to_contain_text('סמנו לפחות שדה אחד')
  page.get_by_label('חילוץ שכר בסיס חודשי (₪)',exact=True).fill('19000.55')
  expect(page.locator('.payslip-review')).to_contain_text('תוקן ידנית. הערך שהוצע: 19000.25')
  page.get_by_label('אישור שכר בסיס חודשי (₪)',exact=True).check()
  page.screenshot(path=str(screenshots/'payslip-local-review.png'),full_page=True)
  page.get_by_role('button',name='אישור והעברת השדות המסומנים לטופס').click()
  expect(page.get_by_label('שכר בסיס חודשי (₪)',exact=True)).to_have_value('19000.55')
  page.get_by_role('button',name='שמירה וחישוב מחדש',exact=True).click();page.wait_for_selector('.vehicle-card')
  persisted=query('/conversation/E-001')['employee'];assert persisted['salary']==19000.55
  assert persisted['pension']==before['pension'] and persisted['credits']==before['credits']
  page.reload();page.wait_for_selector('.vehicle-card');nav('הנתונים שלי')
  expect(page.get_by_label('שכר בסיס חודשי (₪)',exact=True)).to_have_value('19000.55')
  assert '999123456' not in json.dumps(persisted)
  # Camera denial must offer upload fallback without sending anything.
  page.set_viewport_size({'width':390,'height':844})
  page.evaluate("() => {navigator.mediaDevices.getUserMedia=async()=>{throw new DOMException('Denied','NotAllowedError')};}")
  page.get_by_role('button',name='צילום תלוש',exact=True).click()
  expect(page.get_by_role('dialog')).to_contain_text('לא ניתנה הרשאה למצלמה')
  expect(page.get_by_label('צילום תלוש דרך המכשיר')).to_have_attribute('capture','environment')
  page.get_by_role('button',name='סגירת המצלמה').click();expect(page.get_by_role('dialog')).to_have_count(0)
  # A synthetic canvas stream verifies in-browser capture, preview, retake and stream shutdown.
  page.evaluate("""() => {window.cameraTracks=[];navigator.mediaDevices.getUserMedia=async()=>{
   const canvas=document.createElement('canvas');canvas.width=1000;canvas.height=1400;
   const ctx=canvas.getContext('2d');ctx.fillStyle='white';ctx.fillRect(0,0,1000,1400);ctx.fillStyle='black';ctx.font='40px sans-serif';ctx.fillText('SYNTHETIC CAMERA PAYSLIP',50,100);
   const stream=canvas.captureStream(5);window.cameraTracks.push(...stream.getTracks());return stream;};} """)
  page.get_by_role('button',name='צילום תלוש',exact=True).click()
  expect(page.get_by_role('button',name='צילום',exact=True)).to_be_enabled(timeout=10000)
  page.get_by_role('button',name='צילום',exact=True).click()
  expect(page.get_by_alt_text('תצוגה מקדימה של התלוש שצולם')).to_be_visible()
  assert not page.evaluate('document.documentElement.scrollWidth > innerWidth')
  page.screenshot(path=str(screenshots/'payslip-camera-mobile.png'),full_page=True)
  page.get_by_role('button',name='צילום מחדש',exact=True).click()
  expect(page.get_by_role('button',name='צילום',exact=True)).to_be_visible()
  page.get_by_role('button',name='צילום',exact=True).click();page.get_by_role('button',name='שימוש בצילום',exact=True).click()
  expect(page.get_by_role('dialog')).to_have_count(0);expect(page.get_by_text('payslip-camera.jpg',exact=True)).to_be_visible()
  assert page.evaluate("cameraTracks.every(track=>track.readyState==='ended')")
  expect(page.get_by_role('button',name='חילוץ נתונים מהתלוש',exact=True)).to_be_disabled()
  page.set_viewport_size({'width':1440,'height':1100})
  assert len(query('/admin/usage')['items'])==len(usage_before)
  query('/profile',{'employee':before,'overrides':{}})
 finally:
  current=query('/admin/configuration')
  restored=query('/admin/restore',{'expectedRevision':current['revision'],'revision':baseline['revision'],'note':'restore after synthetic local OCR verification'})
 page.reload();page.wait_for_selector('.vehicle-card')
 print('PASS: real local digital PDF, scanned PDF, PNG and JPEG; strict fields without personal excerpts, no AI usage; dense seven-field PDF review, visible coverage and correction, decimal DB persistence and reload; camera capture/retake/denial/track cleanup.')
 return restored['revision']
