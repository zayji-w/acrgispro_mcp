from unittest.mock import patch
import unittest
from types import SimpleNamespace
from gis_agent.app import create_server
from gis_agent.client import BridgeClient
from gis_agent.registry import matches_type
from contracts import BridgeError

class AdapterTests(unittest.TestCase):
 def setUp(self):
  self.bridge=BridgeClient()
  self.tools=SimpleNamespace(**create_server(self.bridge).functions)
 def test_clip_uses_one_gp_engine(self):
  with patch.object(self.bridge,'request',return_value={'ok':True}) as call:
   self.tools.pro_clip('p','m','D:\\in.shp','D:\\mask.shp','key')
   op,args=call.call_args.args
   self.assertEqual(op,'run_gp');self.assertEqual(args['tool'],'analysis.Clip')
   self.assertEqual(args['parameters'],{'input_path':'D:\\in.shp','clip_path':'D:\\mask.shp'})
 def test_buffer_maps_named_parameters(self):
  with patch.object(self.bridge,'request',return_value={'ok':True}) as call:
   self.tools.pro_buffer('p','m','D:\\roads.shp',2,'key',unit='Kilometers',dissolve_option='ALL',output_name='buf')
   op,args=call.call_args.args
   self.assertEqual(op,'run_gp');self.assertEqual(args['tool'],'analysis.Buffer')
   self.assertEqual(args['parameters']['distance'],2)
   self.assertEqual(args['parameters']['unit'],'Kilometers')
   self.assertEqual(args['parameters']['dissolve_option'],'ALL')
   self.assertEqual(args['parameters']['output_name'],'buf')
   self.assertNotIn('dissolve_field',args['parameters'])
 def test_project_and_intersect_wrappers(self):
  with patch.object(self.bridge,'request',return_value={'ok':True}) as call:
   self.tools.pro_project('p','m','D:\\roads.shp',4326,'key2')
   _,args=call.call_args.args
   self.assertEqual(args['tool'],'management.Project')
   self.assertEqual(args['parameters'],{'input_path':'D:\\roads.shp','target_wkid':4326})
   self.tools.pro_intersect('p','m',['D:\\a.shp','D:\\b.shp'],'key3')
   _,args=call.call_args.args
   self.assertEqual(args['tool'],'analysis.Intersect')
   self.assertEqual(args['parameters']['input_paths'],['D:\\a.shp','D:\\b.shp'])
 def test_selection_wrappers(self):
  with patch.object(self.bridge,'request',return_value={'ok':True}) as call:
   self.tools.pro_select_by_attribute('p','m','CIMPATH=map/x.json','key',where_clause='test_id = 1')
   _,args=call.call_args.args
   self.assertEqual(args['tool'],'management.SelectLayerByAttribute')
   self.assertEqual(args['parameters']['where_clause'],'test_id = 1')
 def test_type_matching_supports_lists_and_unions(self):
  self.assertTrue(matches_type(['a','b'],list[str]))
  self.assertFalse(matches_type(['a',1],list[str]))
  self.assertTrue(matches_type(None,str|None))
  self.assertTrue(matches_type('x',str|None))
  self.assertFalse(matches_type(1,str|None))
  self.assertTrue(matches_type({'a':1},dict))
  self.assertTrue(matches_type(2,float))
  self.assertFalse(matches_type(True,float))
 def test_native_errors_are_not_swallowed(self):
  with patch.object(self.bridge,'request',side_effect=BridgeError('NO_PROJECT','Open a project')):
   with self.assertRaisesRegex(BridgeError,'NO_PROJECT'):self.tools.pro_add_layer('p','m','D:\\x.shp')
