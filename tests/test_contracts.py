import json
from pathlib import Path
import unittest
from contracts import BridgeError,loads,validate

class ContractTests(unittest.TestCase):
 def test_shared_cases(self):
  cases=json.loads((Path(__file__).parent/'fixtures/contracts.json').read_text(encoding='utf-8'))
  for case in cases:
   with self.subTest(case=case['name']):
    try:
     validate(case['request'],'test-token','0123456789abcdef0123456789abcdef',1000)
    except BridgeError as exc:
     self.assertFalse(case['ok']);self.assertEqual(exc.code,case['code'])
    else:self.assertTrue(case['ok'])
 def test_duplicate_keys(self):
  with self.assertRaises(BridgeError):loads('{"op":"version","op":"run_gp"}')
 def test_size_depth_and_nonfinite(self):
  for text in ['{"x":"'+'a'*65536+'"}','['*20+'0'+']'*20,'{"expires":NaN}']:
   with self.subTest(text=text[:30]),self.assertRaises(BridgeError):loads(text)
