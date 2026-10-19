"""Shared assertions for isolated PostgreSQL and PostgREST integration tests."""
import copy, json, urllib.error, subprocess, datetime, csv, io
from pathlib import Path

def check_admin(query,dbname=None,upload=None):
 original=query('/admin/configuration')
 start=original['revision'];config=copy.deepcopy(original['configuration'])
 config['ai']['enabled']=False;config['ai']['dailyExchangeRateEnabled']=False
 config['rules']['electric']['netBonus']=600
 payload={'expectedRevision':start,'configuration':config,'note':'isolated admin integration test',
  'apiKeys':{'extraction':'TEST-ONLY-NOT-A-REAL-KEY','narration':'TEST-ONLY-NOT-A-REAL-KEY'}}
 saved=query('/admin/configuration',payload)
 assert saved['revision']==start+1 and saved['keyStatus']['extraction'] and saved['keyStatus']['narration']
 assert 'TEST-ONLY-NOT-A-REAL-KEY' not in json.dumps(saved)
 employee=query('/data')['employees'][0]
 sim=query('/simulate',{'employee':employee});electric=next(r for r in sim['results'] if r['id']=='CAR-10')
 assert abs(electric['electricNet']-600)<.0001
 try:query('/admin/configuration',payload);raise AssertionError('Stale revision must fail')
 except urllib.error.HTTPError as error:assert error.code==409
 invalid=copy.deepcopy(saved['configuration']);invalid['cars'][0]['value']+=1
 try:query('/admin/configuration',{'expectedRevision':start+1,'configuration':invalid,'note':'invalid official value'});raise AssertionError('Forged official value must fail')
 except urllib.error.HTTPError as error:assert error.code==400
 restored=query('/admin/restore',{'expectedRevision':start+1,'revision':start,'note':'restore original test configuration'})
 assert restored['revision']==start+2
 sim=query('/simulate',{'employee':employee});assert abs(next(r for r in sim['results'] if r['id']=='CAR-10')['electricNet']-250)<.0001
 assert restored['configuration']==original['configuration']
 assert len(restored['history'])>=2
 if upload:
  root=Path(__file__).resolve().parent.parent
  keys={(str(c['year']),c['makerCode'],c['modelCode']) for c in config['cars']}
  with (root/'data/tax-authority-2026.csv').open(encoding='utf-8') as source:
   reader=csv.DictReader(source);headers=reader.fieldnames;docs=[r for r in reader if (r['שנת רישום'],r['קוד תוצר'],r['קוד דגם']) in keys and r['קוד סוג רכב']=='1']
  assert len(docs)==len(keys)
  for r in docs:r['שנת מס']='2027';r['שווי שימוש']=str(float(r['שווי שימוש'])+100)
  def encode(rows):
   out=io.StringIO();writer=csv.DictWriter(out,fieldnames=headers);writer.writeheader();writer.writerows(rows);return out.getvalue().encode('utf-8')
  content=encode(docs);fixtures=root/'.local/test-fixtures';fixtures.mkdir(parents=True,exist_ok=True);(fixtures/'official-2027.csv').write_bytes(content)
  version=upload('/admin/official/upload?fileName=official-2027.csv',content)
  assert version['taxYear']==2027 and query('/data')['rules']['valueOfUse'].get('taxYear',2026)==2026
  preview=query('/admin/official/versions/'+version['id']+'/preview');assert not preview['missing'] and len(preview['matches'])==20
  activated=query('/admin/official/activate',{'versionId':version['id'],'expectedRevision':restored['revision'],'note':'test activate 2027'})
  updated=query('/data');assert updated['rules']['valueOfUse']['taxYear']==2027 and updated['officialCount']==20
  original_values={c['id']:c['value'] for c in original['configuration']['cars']}
  assert all(c['value']==original_values[c['id']]+100 for c in updated['cars'])
  after=query('/simulate',{'employee':updated['employees'][0]});assert after['current']['value']==original_values[updated['employees'][0]['currentCar']]+100
  try:query('/admin/official/activate',{'versionId':version['id'],'expectedRevision':restored['revision'],'note':'stale import'});raise AssertionError('Concurrent activation must fail')
  except urllib.error.HTTPError as e:assert e.code==409
  restored=query('/admin/restore',{'expectedRevision':activated['revision'],'revision':restored['revision'],'note':'restore baseline after 2027 test'})
  assert query('/data')['officialCount']==77988
  incomplete=upload('/admin/official/upload?fileName=incomplete.csv',encode(docs[:1]))
  assert query('/admin/official/versions/'+incomplete['id']+'/preview')['missing']
  try:query('/admin/official/activate',{'versionId':incomplete['id'],'expectedRevision':restored['revision'],'note':'reject missing cars'});raise AssertionError('Missing catalogue matches must fail')
  except urllib.error.HTTPError as e:assert e.code==400
  # Exercise the endpoint-specific body limit above the global 2MB JSON limit.
  big=[]
  for i in range(1100):
   row=dict(docs[0]);row['קוד דגם']=str(90000000+i);row['הערות']='x'*1900;big.append(row)
  out=io.StringIO();writer=csv.DictWriter(out,fieldnames=headers+['הערות']);writer.writeheader();writer.writerows(big);large=out.getvalue().encode('utf-8');assert len(large)>2_000_000
  large_version=upload('/admin/official/upload?fileName=large-validation.csv',large);assert large_version['rowCount']==1100
  try:upload('/payslip/extract?employee=E-001',b'%PDF-1.7 TEST ONLY DOCUMENT');raise AssertionError('Payslip consent must be required')
  except urllib.error.HTTPError as e:assert e.code==400
  print('PASS: official CSV upload, immutable version metadata/chunks, new-year activation, reference refresh, optimistic conflict, rollback and incomplete-catalogue rejection.')
 assert query('/admin/usage')['items']==[]
 assert query('/admin/usage/conversation/E-001')['calls']==0
 assert 'source' in query('/admin/exchange-rate')
 if dbname:
  for index in range(2):
   record={'id':f'usage:20261019000000000000:fixture{index}','startedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'completedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),'conversationId':'E-001','actionId':'same-test-action',
    'operation':'extract_employee_intent' if index==0 else 'narrate_comparison','provider':'openai','host':'api.openai.com','model':'test-model',
    'configurationRevision':restored['revision'],'pricing':None,'exchangeRate':None,'status':'completed','tokens':{'input':100,'cachedInput':0,'cacheWrite':0,'output':10},
    'estimatedUsd':.01 if index==0 else None,'estimatedIls':.037 if index==0 else None,'errorCode':None}
   document=json.dumps(record).replace("'","''")
   sql="INSERT INTO carwise_rules(id,document) VALUES('"+record['id']+"','"+document+"'::jsonb)"
   subprocess.run(['docker','exec','carwise-postgres','psql','-U','carwise','-d',dbname,'-c',sql],check=True,stdout=subprocess.DEVNULL)
  page=query('/admin/usage?limit=1');assert len(page['items'])==1 and page['nextCursor']
  nextpage=query('/admin/usage?limit=1&after='+page['nextCursor']);assert len(nextpage['items'])==1 and nextpage['items'][0]['id']!=page['items'][0]['id']
  totals=query('/admin/usage/conversation/E-001');assert totals['calls']==2 and totals['knownUsd']==.01 and totals['knownIls']==.037 and totals['unknownUsd']==1 and totals['unknownIls']==1
  assert query('/admin/usage/conversation/E-002')['calls']==0
 print('PASS: admin policy persistence, live calculations, encrypted write-only keys, atomic revision conflict, official-value protection, version rollback, usage and exchange-rate endpoints.')
 return restored['revision']
