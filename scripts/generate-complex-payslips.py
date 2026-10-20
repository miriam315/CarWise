"""Generate synthetic, dense Israeli-style payroll fixtures (never real payroll data).
Requires reportlab, PyMuPDF and Pillow. Output stays in .local until explicitly packaged.
"""
from pathlib import Path
import json, re
from decimal import Decimal, ROUND_HALF_UP
from reportlab.pdfgen import canvas
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.lib.colors import HexColor, white
import fitz
from PIL import Image, ImageEnhance, ImageFilter

ROOT=Path(__file__).resolve().parent.parent
OUT=ROOT/'.local/complex-payslips'
OUT.mkdir(parents=True,exist_ok=True)
FONT=Path('/usr/share/fonts/truetype/dejavu')
for name,file in [('Heb','DejaVuSansCondensed.ttf'),('HebBold','DejaVuSansCondensed-Bold.ttf')]:
 pdfmetrics.registerFont(TTFont(name,str(FONT/file)))
W,H=595.28,841.89
INK=HexColor('#17202b');GRAY=HexColor('#e6e9ed');BORDER=HexColor('#9ba3ac')
def visual(s):
 s=str(s)
 if not re.search('[א-ת]',s):return s
 s=s[::-1].translate(str.maketrans({'(':')',')':'(','[':']',']':'['}))
 return re.sub(r'[A-Za-z0-9%.,:/+\-]+',lambda m:m.group()[::-1],s)
def money(n):return f'{float(n):,.2f}'
def text(c,s,x,y,size=8,bold=False,align='right'):
 c.setFont('HebBold' if bold else 'Heb',size);c.setFillColor(INK)
 s=visual(s)
 {'right':c.drawRightString,'left':c.drawString,'center':c.drawCentredString}[align](x,y,s)
def box(c,x,y,w,h):
 c.setLineWidth(.4);c.setStrokeColor(BORDER);c.rect(x,y,w,h,stroke=1,fill=0)
def table(c,x,top,w,title,headers,rows,ratios,rh=14,size=7.2):
 titleh,headerh=19,17
 c.setFillColor(GRAY);c.rect(x,top-titleh,w,titleh,fill=1,stroke=0)
 text(c,title,x+w-5,top-13,9,True)
 c.setFillColor(HexColor('#f2f3f5'));c.rect(x,top-titleh-headerh,w,headerh,fill=1,stroke=0)
 widths=[w*r/sum(ratios) for r in ratios]
 bottom=top-titleh-headerh-rh*len(rows)
 box(c,x,bottom,w,top-bottom)
 right=x+w
 for i,(head,width) in enumerate(zip(headers,widths)):
  text(c,head,right-width/2,top-titleh-11,7.0,True,'center')
  if i:c.line(right,bottom,right,top-titleh)
  right-=width
 for ri,row in enumerate(rows):
  baseline=top-titleh-headerh-(ri+1)*rh+4
  right=x+w
  for value,width in zip(row,widths):
   text(c,value,right-width/2,baseline,size,False,'center');right-=width
  c.setStrokeColor(BORDER);c.line(x,baseline-4,x+w,baseline-4)
 return bottom

def generate(case):
 slug=case['id'];values=case['fields'];monthly=case.get('hourly') is None
 path=OUT/(slug+'.pdf');c=canvas.Canvas(str(path),pagesize=(W,H));c.setTitle('SYNTHETIC DENSE PAYSLIP / '+slug);c.setAuthor('CarWise test data')
 c.setFillColor(HexColor('#fff1d3'));c.rect(17,805,W-34,23,fill=1,stroke=0)
 text(c,'מסמך פיקטיבי לבדיקת תוכנה בלבד — אינו תלוש לתשלום',W/2,813,10,True,'center')
 text(c,'מפעלי הדגמה ובדיקות בע״מ',W-18,783,15,True)
 text(c,'תלוש שכר לחודש ספטמבר',W-18,764,10,True)
 text(c,'09/2026',18,784,13,True,'left');text(c,'חברה: 000000000 | תיק ניכויים: דמה',18,765,8,False,'left')
 table(c,17,749,W-34,'פרטי העובד וההעסקה', ['שם עובד','מספר עובד','מחלקה','מעמד','תחילת עבודה'],[
  ['עובד/ת הדגמה '+case['person'],case['number'],'תפעול ובדיקות','חודשי' if monthly else 'שעתי','01/01/2025'],
  ['ת״ז לא הוזנה','בנק לא הוזן','משרה מלאה','תושב/ת ישראל','נתוני דמה בלבד']],[2.5,1.8,2,1.7,1.6],rh=16,size=8)
 # The credit number is embedded in a realistic multi-cell metadata band, rather
 # than a specially crafted one-number line designed for the current extractor.
 table(c,17,675,W-34,'נתוני עבודה ומיסוי', ['ימי עבודה','שעות עבודה','אחוז משרה','נקודות זיכוי','שווי נקודה'],[
  ['22.00',money(case.get('hours',182)), '100.00%',str(values['credits']),'242.00']],[1,1,1,1,1],rh=17,size=8)
 salary=Decimal(str(values['salary']));commission=Decimal(str(values['commission']));bonus=Decimal(str(values['bonus']))
 overtime=Decimal(str(case.get('overtime',620.50)));travel=Decimal('260.00');meal=Decimal('180.00')
 gross=salary+commission+bonus+overtime+travel+meal
 # Amounts and rates are illustrative. Do not claim to reproduce Israeli tax law.
 pension=(salary*Decimal(str(values['pension']))/100).quantize(Decimal('.01'),rounding=ROUND_HALF_UP)
 fund=(salary*Decimal(str(values['fund']))/100).quantize(Decimal('.01'),rounding=ROUND_HALF_UP)
 tax=(gross*Decimal('.12')).quantize(Decimal('.01'),rounding=ROUND_HALF_UP)
 national=(gross*Decimal('.037')).quantize(Decimal('.01'),rounding=ROUND_HALF_UP)
 health=(gross*Decimal('.049')).quantize(Decimal('.01'),rounding=ROUND_HALF_UP)
 participation=Decimal(str(values['participation']));loan=Decimal('200.00')
 deductions=pension+fund+tax+national+health+participation+loan
 earnings=[
  ['001','שכר בסיס' if monthly else 'שכר יסוד', '1.00' if monthly else money(case['hours']), money(salary if monthly else case['hourly']),money(salary)],
  ['021','עמלות','1.00',money(commission),money(commission)],
  ['030','בונוס','1.00',money(bonus),money(bonus)],
  ['101','שעות נוספות 125%','4.00',f'{overtime/4:.4f}',money(overtime)],
  ['120','נסיעות','20.00','13.00',money(travel)],
  ['140','ארוחות חייבות מס','20.00','9.00',money(meal)],
  ['170','הבראה','0.00','418.00','0.00'],
  ['210','היעדרות ללא תשלום','0.00','0.00','0.00'],
  ['250','זקיפת שווי רכב','1.00','3,290.00','3,290.00'],
  ['251','התאמת שווי — מידע','0.00','0.00','0.00'],
  ['910','סה״כ ברוטו לתשלום','','',money(gross)],
  ['920','ברוטו למס incl. זקיפות','','',money(gross+3290)],
  ['930','ברוטו לפנסיה','','',money(salary)],
  ['940','ברוטו לקרן השתלמות','','',money(salary)],
 ]
 swap=case.get('swap',False);ex=230 if swap else 17;dx=17 if swap else 376
 table(c,ex,615,348,'פירוט תשלומים ורכיבי שכר',['קוד','תיאור רכיב','כמות','תעריף','סכום'],earnings,[.65,2.9,1,1.3,1.35],rh=14.5,size=7.0)
 deduction_rows=[['101','מס הכנסה',money(tax)],['102','ביטוח לאומי',money(national)],['103','ביטוח בריאות',money(health)],['210','ניכוי פנסיה עובד',money(pension)],['220','ניכוי קרן השתלמות',money(fund)],['301','השתתפות עובד ברכב',money(participation)],['350','החזר הלוואה',money(loan)],['900','סך ניכויים',money(deductions)]]
 table(c,dx,615,202,'ניכויי חובה ורשות',['קוד','תיאור','סכום'],deduction_rows,[.6,2.9,1.5],rh=15,size=6.9)
 table(c,dx,445,202,'יתרות היעדרות — ימים',['סוג','קודמת','תוספת','ניצול','יתרה'],[
 ['חופשה','8.50','1.50','2.00','8.00'],['מחלה','21.00','1.50','0.00','22.50'],['הבראה','4.00','0.50','0.00','4.50']],[1.4,1,1,1,1],rh=14,size=7)
 table(c,17,351,W-34,'קופות גמל והשתלמות — הפרשות חודשיות ומצטברות',['קופה','בסיס','אחוז עובד','סכום עובד','אחוז מעסיק','סכום מעסיק','מצטבר עובד'],[
 ['פנסיה — קרן דמה',money(salary),str(values['pension'])+'%',money(pension),'6.50%',money(salary*Decimal('.065')),money(pension*9)],
 ['פיצויים — קופת דמה',money(salary),'0.00%','0.00','8.33%',money(salary*Decimal('.0833')),'0.00'],
 ['קרן השתלמות — דמה',money(salary),str(values['fund'])+'%',money(fund),'7.50%',money(salary*Decimal('.075')),money(fund*9)]],[2.3,1.3,1.1,1.2,1.1,1.2,1.3],rh=17,size=7.1)
 table(c,17,251,274,'נתונים מצטברים מתחילת השנה',['רכיב','מצטבר'],[
 ['ברוטו למס',money((gross+3290)*9)],['מס הכנסה',money(tax*9)],['ביטוח לאומי',money(national*9)],['ביטוח בריאות',money(health*9)],['שכר בסיס מצטבר',money(salary*9)]],[2.2,1.3],rh=14,size=7.6)
 table(c,304,251,274,'סיכום תשלום — סכומי המחשה בלבד',['רכיב','חודש נוכחי'],[
 ['ברוטו לתשלום',money(gross)],['ניכויי חובה',money(tax+national+health)],['קופות ורשות',money(pension+fund+participation+loan)],['סך הניכויים',money(deductions)],['נטו להעברה',money(gross-deductions)]],[2.2,1.3],rh=14,size=7.6)
 table(c,17,133,W-34,'נתוני נוכחות ומעקב',['תקן שעות','נוכחות','נוספות','חופשה','מחלה','היעדרות','ימי חודש','ימי מס'],[
 ['182.00',money(case.get('hours',182)),'4.00','2.00','0.00','0.00','30.00','30.00']],[1,1,1,1,1,1,1,1],rh=14,size=7.3)
 text(c,'סכומי מס, נטו ומצטברים להמחשה בלבד; אינם חישוב שכר מאומת.',W-18,64,8,True)
 text(c,'הנתונים מלאכותיים. אין במסמך פרטי עובד, בנק או מזהים אמיתיים.',W-18,48,8)
 text(c,'SYNTHETIC TEST DATA / NOT VALID FOR PAYMENT / PAGE 1 OF 1',W/2,25,7,True,'center')
 c.save()
 doc=fitz.open(path);pix=doc[0].get_pixmap(matrix=fitz.Matrix(3.2,3.2),alpha=False)
 img=Image.frombytes('RGB',[pix.width,pix.height],pix.samples)
 img.save(OUT/(slug+'.png'));img.save(OUT/(slug+'.jpg'),quality=91)
 # A mild skew, grayscale and lower contrast create a plausible scanning challenge.
 scan=ImageEnhance.Contrast(img.convert('L')).enhance(.86).rotate(.55,resample=Image.Resampling.BICUBIC,expand=False,fillcolor=255).filter(ImageFilter.GaussianBlur(.15))
 from io import BytesIO
 stream=BytesIO();scan.save(stream,format='JPEG',quality=86)
 scanned=fitz.open();page=scanned.new_page(width=W,height=H);page.insert_image(page.rect,stream=stream.getvalue());scanned.save(OUT/(slug+'-scanned.pdf'),deflate=True)

cases=[
 {'id':'01-complex-monthly','person':'א','number':'DEMO-A','fields':{'salary':19000.25,'commission':1000,'bonus':0,'pension':6,'fund':2.5,'credits':2.25,'participation':350}},
 {'id':'02-complex-sales','person':'ב','number':'DEMO-B','swap':True,'overtime':850,'fields':{'salary':12500,'commission':3250.75,'bonus':1500,'pension':6.5,'fund':2.5,'credits':2.75,'participation':0}},
 {'id':'03-complex-hourly','person':'ג','number':'DEMO-C','hourly':85.5,'hours':168,'overtime':427.5,'fields':{'salary':14364,'commission':0,'bonus':750,'pension':6,'fund':2.5,'credits':3.25,'participation':650}},
]
for case in cases:generate(case)
(OUT/'expected-values.json').write_text(json.dumps({c['id']:c['fields'] for c in cases},ensure_ascii=False,indent=2)+'\n')
print('Generated 12 dense payroll documents in',OUT)
