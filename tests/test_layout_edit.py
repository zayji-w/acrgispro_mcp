from unittest.mock import patch
import unittest
from types import SimpleNamespace
from gis_agent.app import create_server
from gis_agent.client import BridgeClient
from gis_agent.registry import matches_type
from contracts import validate_args,BridgeError

class LayoutEditTests(unittest.TestCase):
 def setUp(self):
  self.bridge=BridgeClient()
  self.tools=SimpleNamespace(**create_server(self.bridge).functions)
 def test_single_element_uses_batch_engine(self):
  with patch.object(self.bridge,'request',return_value={}) as send:
   self.tools.pro_update_layout_element('p','l','图例',x_cm=10.5,width_cm=3)
   op,args=send.call_args.args
   self.assertEqual(op,'update_layout_elements');self.assertEqual(args['edits'],[{'element_name':'图例','x_cm':10.5,'width_cm':3}]);validate_args(op,args)
 def test_batch_rejects_duplicate_and_empty_targets(self):
  base={'project_id':'p','layout_id':'l'}
  for edits in [[],[{'element_name':'图例'}],[{'element_name':'x','scale':2},{'element_name':'x','x_cm':2}]]:
   with self.subTest(edits=edits),self.assertRaises(BridgeError):validate_args('update_layout_elements',base|{'edits':edits})
 def test_nested_edits_reject_bad_types_unknown_keys(self):
  for change in [{'x_cm':True},{'width_cm':-1},{'rotation':float('nan')},{'scale':2,'height_cm':3},{'font_size':200},{'delete':True},{'expected_type':3}]:
   with self.subTest(change=change),self.assertRaises(BridgeError):validate_args('update_layout_elements',{'project_id':'p','layout_id':'l','edits':[{'element_name':'x'}|change]})
 def test_unicode_city_requires_exactly_one_position_per_character(self):
  base={'project_id':'p','layout_id':'l','name':'Hefei','text':'合肥市','positions':[{'x_cm':1,'y_cm':2},{'x_cm':2,'y_cm':2,'rotation':15},{'x_cm':3,'y_cm':2}]}
  validate_args('place_city_name',base)
  for change in [{'positions':[{'x_cm':1,'y_cm':2}]},{'positions':[{'x_cm':1,'y_cm':True}]*3},{'positions':[{'x_cm':1,'y_cm':2,'code':'pass'}]*3}]:
   with self.assertRaises(BridgeError):validate_args('place_city_name',base|change)
 def test_unicode_supplementary_character_counts_once(self):
  validate_args('place_city_name',{'project_id':'p','layout_id':'l','name':'Unicode','text':'𠮷','positions':[{'x_cm':1,'y_cm':1}]})
 def test_paragraph_box_requires_both_dimensions(self):
  base={'project_id':'p','layout_id':'l','name':'说明','text':'文字说明','x_cm':1,'y_cm':1}
  validate_args('add_layout_text',base|{'width_cm':4,'height_cm':2})
  with self.assertRaises(BridgeError):validate_args('add_layout_text',base|{'width_cm':4})
