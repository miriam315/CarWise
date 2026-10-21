"""Backend invariants for both PostgreSQL and Supabase/PostgREST smoke tests."""
from copy import deepcopy
import urllib.error

def check(query):
 config=query('/admin/configuration');employee=deepcopy(query('/data')['employees'][0]);employee.update(id='E-DECISION-ACCEPTANCE',name='Synthetic decision test')
 query('/profile',{'employee':employee})
 def reject(path,body):
  try:query(path,body);raise AssertionError('invalid request accepted: '+path)
  except urllib.error.HTTPError as error:assert error.code==400,(path,error.code)
 state=query('/decision/state/'+employee['id']);assert state['revision']==0
 fields={k:employee[k] for k in ['salary','commission','bonus','pension','fund','credits','participation']};fields['bonus']=1500
 request={'employee':employee,'expectedRevision':0,'month':{'period':'2026-09','fields':fields,'bonusKind':'oneOff'}}
 reject('/decision/month',{**request,'month':{**request['month'],'fields':{**fields,'unexpected':1}}})
 reject('/decision/month',{**request,'month':{**request['month'],'bonusKind':'none'}})
 saved=query('/decision/month',request);assert saved['revision']==1
 reject('/decision/month',request) # stale caller cannot overwrite another review
 state=query('/decision/state/'+employee['id']);assert state['months'][0]['actual']['employee']['bonus']==1500
 assert state['months'][0]['regular']['employee']['bonus']==0
 assert query('/conversation/'+employee['id'])['employee']['bonus']==employee['bonus']
 known=query('/decision/analyze',{'employee':employee,'target':'CAR-10'})
 reject('/decision/analyze',{'employee':employee,'target':'CAR-10','axis':'untrusted'})
 reject('/decision/analyze',{'employee':employee,'target':'CAR-10','hypotheticalParticipation':-1})
 reject('/decision/analyze',{'employee':employee,'target':'CAR-10','axis':'participation','rangeMaximum':20001})
 assert known['outcome'] and len(known['points'])==11
 saved=query('/decision/choice',{'employee':employee,'target':'CAR-10','expectedRevision':state['revision']})
 assert saved['choice']['employee']['name']==''
 try:
  current=query('/admin/configuration');changed=deepcopy(current['configuration']);changed['rules']['electric']['netBonus']+=100
  query('/admin/configuration',{'expectedRevision':current['revision'],'configuration':changed,'note':'approved policy change for decision acceptance'})
  after=query('/decision/state/'+employee['id']);assert after['change']['policyChanged']
  assert abs(after['change']['impact']-100)<.000001,after['change']
  official=next(v for v in query('/admin/official/versions')['items'] if v['fileName']=='official-2027.csv')
  current=query('/admin/configuration');query('/admin/official/activate',{'versionId':official['id'],'expectedRevision':current['revision'],'note':'approved official update for saved choice'})
  official_change=query('/decision/state/'+employee['id'])
  assert official_change['change']['officialVersionChanged'] and official_change['change']['status']=='ready'
  simulated=query('/simulate',{'employee':employee});net=next(r['net'] for r in simulated['results'] if r['id']=='CAR-10')
  assert abs(official_change['change']['newNet']-net)<.000001
  assert any(c['label']=='שווי שימוש' for c in official_change['change']['changes'])
  for car in changed['cars']:
   if car['id']=='CAR-10':car['participationKnown']=False
  current=query('/admin/configuration');query('/admin/configuration',{'expectedRevision':current['revision'],'configuration':changed,'note':'unknown amount acceptance'})
  after=query('/decision/state/'+employee['id']);assert after['change']['status']=='pending' and after['change']['newNet'] is None and after['change']['changes']==[]
  conditional=query('/decision/analyze',{'employee':employee,'target':'CAR-10','axis':'participation','rangeMaximum':2000})
  assert conditional['conditional'] and conditional['outcome'] is None and conditional['boundary']
  reject('/decision/choice',{'employee':employee,'target':'CAR-10','expectedRevision':after['revision']})
  identity=deepcopy(config['configuration']);replacement=next(c for c in identity['cars'] if c['id']=='CAR-06');selected=next(c for c in identity['cars'] if c['id']=='CAR-10')
  for key in ['year','makerCode','modelCode','drive','value','officialValue','officialKey','model','brand']:selected[key]=deepcopy(replacement[key])
  current=query('/admin/configuration');query('/admin/configuration',{'expectedRevision':current['revision'],'configuration':identity,'note':'catalogue identity changed without changing slot id'})
  identity_change=query('/decision/state/'+employee['id']);assert identity_change['change']['status']=='review' and 'זהות הרכב' in identity_change['change']['message']
  erased=query('/decision/month/remove',{'employee':employee,'period':'2026-09','expectedRevision':after['revision']})
  assert erased['months']==[] and erased['choice'] is not None
 finally:
  current=query('/admin/configuration');query('/admin/restore',{'expectedRevision':current['revision'],'revision':config['revision'],'note':'restore after decision acceptance'})
 print('PASS: decision API through real database: reviewed-month validation, CAS conflicts, one-off classification, immutable original profile, choice snapshot, approved-policy and official-registry deltas, pending nulls, vehicle-identity changes and conditional boundary, month removal.')
