import unittest
from copy import deepcopy
from engine import *
class PayrollTests(unittest.TestCase):
 def test_six_reference_net_pairs(self):
  targets=['CAR-10','CAR-19','CAR-06','CAR-11','waiver','CAR-14']
  for e,ref,target in zip(EMPLOYEES,load('references'),targets):
   with self.subTest(employee=e['name']):
    simulation=simulate(e);result=next(r for r in simulation['results'] if r['id']==target)
    self.assertEqual(rounded(D(simulation['current']['net'])),ref['netBefore'])
    self.assertEqual(rounded(D(result['net'])),ref['netAfter'])
    self.assertEqual(rounded(D(simulation['current']['tax'])),ref['taxBefore'])
    self.assertEqual(rounded(D(result['tax'])),ref['taxAfter'])
    self.assertEqual(rounded(D(result['insurance'])),ref['insuranceAfter'])
    # Excel deltas subtract rounded nets; implementation subtracts before rounding.
    self.assertEqual(rounded(D(result['net']))-rounded(D(simulation['current']['net'])),ref['delta'])
 def test_official_catalogue_keys_values_and_drive(self):
  self.assertEqual(len(OFFICIAL),77988)
  for c in CARS:
   self.assertEqual(int(official(c)['שווי שימוש']),c['value'])
   self.assertEqual(RULES['valueOfUse']['driveField'][official(c)['1-חשמלי.2-היברידי.3-פלג-אין']],c['drive'])
 def test_exact_electric_bonus_and_precision(self):
  for e in EMPLOYEES:
   for c in CARS:
    if c['drive']=='חשמלי': self.assertLess(abs(calculate(e,c)['electricNet']-250),D('1e-20'))
 def test_tax_bracket_and_insurance_boundary_continuity(self):
  e=deepcopy(EMPLOYEES[0]);e.update(credits=0,commission=0,bonus=0,pension=0,fund=0)
  maintenance=occupation(e)['maintenance']
  for threshold in [7010,7522,10060,16150,22440,46690,50695,60130]:
   e['salary']=threshold-maintenance-D('.01');before=calculate(e,None)
   e['salary']=threshold-maintenance+D('.01');after=calculate(e,None)
   self.assertGreater(after['net'],before['net']);self.assertLessEqual(after['net']-before['net'],D('.02'))
 def test_family_and_waiver_restrictions(self):
  e=deepcopy(EMPLOYEES[0]);family=CARS[18]
  self.assertFalse(eligibility(e,family)[0]);e['children']=4;self.assertTrue(eligibility(e,family)[0])
  self.assertEqual(calculate(e,family)['familyGap'],D(750))
  self.assertEqual(calculate(e,CARS[19])['familyGap'],D(550))
  self.assertFalse(eligibility(EMPLOYEES[1],None)[0])
  self.assertFalse(eligibility(EMPLOYEES[0],CARS[13])[0])
 def test_participation_is_net_only_and_maintenance_not_pensionable(self):
  e=EMPLOYEES[0];c=CARS[9];a=calculate(e,c,300);b=calculate(e,c,350)
  self.assertEqual(a['tax'],b['tax']);self.assertEqual(a['value'],b['value']);self.assertEqual(a['net']-b['net'],50)
  waiver=calculate(e,None);self.assertEqual(waiver['pension'],a['pension']);self.assertEqual(waiver['fund'],a['fund'])
 def test_reject_unknown_official_key(self):
  c=deepcopy(CARS[0]);c['modelCode']='99999'
  with self.assertRaises(ValueError): official(c)
  c=deepcopy(CARS[0]);c['year']=2030
  with self.assertRaises(ValueError): official(c)
 def test_invalid_inputs(self):
  for k,v in [('salary',-1),('pension',101),('children',1.5),('credits','NaN'),('occupation','לא קיים'),('currentCar','unknown')]:
   e=deepcopy(EMPLOYEES[0]);e[k]=v
   with self.subTest(k=k):
    with self.assertRaises(ValueError): simulate(e)
 def test_conversation_extracts_only_explicit_fields(self):
  self.assertEqual(extract_updates('שכר בסיס 18,500, עמלות 1,000, פנסיה 6%, קרן השתלמות 2.5%, נקודות זיכוי 2.75, יש לי 4 ילדים',EMPLOYEES[0]),{'salary':18500,'commission':1000,'pension':6,'fund':2.5,'credits':2.75,'children':4})
  self.assertEqual(extract_updates('אני מתלבטת בין 3 רכבים',EMPLOYEES[0]),{})
  self.assertEqual(extract_updates('יש לי 5',EMPLOYEES[0]),{})
 def test_raw_delta_rounding_is_explicit(self):
  for index,target,expected in [(2,'CAR-06',-1939),(5,'CAR-14',722)]:
   result=next(r for r in simulate(EMPLOYEES[index])['results'] if r['id']==target)
   self.assertEqual(rounded(D(result['delta'])),expected)
if __name__=='__main__': unittest.main()
