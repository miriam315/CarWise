"""One-time import from the challenge attachments. Runtime needs only stdlib."""
from pathlib import Path
from openpyxl import load_workbook
import csv, json, shutil
ROOT=Path(__file__).resolve().parent
attachments=Path('/workspace/attachments')
rules=json.loads(next(attachments.rglob('*.json')).read_text())
(ROOT/'rules.json').write_text(json.dumps(rules,ensure_ascii=False,indent=2))
w=load_workbook(next(attachments.rglob('*.xlsx')),data_only=True)
fields=['id','group','brand','makerCode','model','modelCode','description','year','drive','price','value','cost','participation']
cars=[dict(zip(fields,r)) for r in list(w['קטלוג רכבים'].values)[1:]]
fields=['occupation','group','mandatory','waiver','maintenance']
occupations=[dict(zip(fields,r)) for r in list(w['עיסוקים'].values)[1:]]
for o in occupations:
 o['mandatory']=o['mandatory']=='כן';o['waiver']=o['waiver']=='כן'
fields=['id','name','identity','occupation','group','salary','commission','bonus','pension','fund','credits','children','currentModel','participation','question']
employees=[dict(zip(fields,r)) for r in list(w['עובדים לדוגמה'].values)[1:]]
for e in employees:
 del e['identity']
 e['currentCar']=next((c['id'] for c in cars if c['model']==e['currentModel']),None)
refs=[dict(zip(['id','name','current','alternative','eligible','reason','netBefore','netAfter','delta','taxBefore','taxAfter','insuranceBefore','insuranceAfter','electricGross','familyGap','explanation'],r)) for r in list(w['חישובים'].values)[1:]]
for name,value in [('cars',cars),('occupations',occupations),('employees',employees),('references',refs)]:
 (ROOT/f'{name}.json').write_text(json.dumps(value,ensure_ascii=False,indent=2))
# Preserve all 77,988 official rows, normalize headers only; codes stay strings.
p=next(attachments.rglob('*.csv'))
with p.open(encoding='cp1255',newline='') as f, (ROOT/'tax-authority-2026.csv').open('w',encoding='utf-8',newline='') as out:
 reader=csv.reader(f);writer=csv.writer(out);writer.writerow([x.strip() for x in next(reader)])
 count=0
 for row in reader: writer.writerow([x.strip() for x in row]);count+=1
print('Imported',len(cars),'cars,',len(employees),'employees,',count,'official records')
