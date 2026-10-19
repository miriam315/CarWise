"""Shared assertions for isolated PostgreSQL and PostgREST integration tests."""
import copy, json, urllib.error, subprocess, datetime

def check_admin(query,dbname=None):
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
 assert query('/admin/usage')['items']==[]
 assert query('/admin/usage/conversation/E-001')['calls']==0
 assert 'source' in query('/admin/exchange-rate')
 if dbname:
  for index in range(2):
   record={'id':f'usage:20261019000000000000:fixture{index}','startedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'completedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),'conversationId':'E-001','actionId':'same-test-action',
    'operation':'extract_employee_intent' if index==0 else 'narrate_comparison','provider':'openai','host':'api.openai.com','model':'test-model',
    'configurationRevision':start+2,'pricing':None,'exchangeRate':None,'status':'completed','tokens':{'input':100,'cachedInput':0,'cacheWrite':0,'output':10},
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
