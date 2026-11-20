from unittest.mock import patch
import unittest
from types import SimpleNamespace
from gis_agent.app import create_server
from gis_agent.client import BridgeClient
from gis_agent.registry import matches_type
from contracts import validate_args, BridgeError

class PresentationTests(unittest.TestCase):
 def setUp(self):
  self.bridge=BridgeClient()
  self.tools=SimpleNamespace(**create_server(self.bridge).functions)
 def test_auto_layout_preserves_explicit_study_layer(self):
  with patch.object(self.bridge,'request',return_value={'ok':True}) as send:
   self.tools.pro_create_layout('p','m','study','新布局','研究区')
   op,a=send.call_args.args
   self.assertEqual(op,'create_layout');self.assertEqual(a['orientation'],'auto')
   self.assertEqual(a['extent_layer_id'],'study');validate_args(op,a)
 def test_exports_share_existing_task_status(self):
  with patch.object(self.bridge,'request',return_value={'job_id':'x'}) as send:
   self.tools.pro_export_layout('p','layout','key',format='PDF',dpi=600)
   op,a=send.call_args.args
   self.assertEqual(op,'export_layout');self.assertEqual(a['idempotency_key'],'key');validate_args(op,a)
   self.tools.pro_export_map('p','m','key2',width=2000,height=1200)
   op,a=send.call_args.args;self.assertEqual(op,'export_map');validate_args(op,a)
 def test_reject_export_path_escape_and_excessive_pixels(self):
  base={'project_id':'p','map_id':'m','idempotency_key':'x'}
  for change in [{'output_name':'../research'},{'output_name':'D:\\map.png'},{'width':12000,'height':12000},{'dpi':True},{'dpi':150.5},{'dpi':9999}]:
   with self.subTest(change=change),self.assertRaises(BridgeError):validate_args('export_map',base|change)
 def test_reject_bad_colors_fields_and_page_options(self):
  b={'project_id':'p','map_id':'m','layer_id':'l','color':'#00CC66'}
  validate_args('set_simple_renderer',b|{'width':2})
  for change in [{'color':'red'},{'width':float('nan')},{'size':0},{'width':False}]:
   with self.subTest(change=change),self.assertRaises(BridgeError):validate_args('set_simple_renderer',b|change)
  with self.assertRaises(BridgeError):validate_args('create_layout',{'project_id':'p','map_id':'m','extent_layer_id':'l','name':'n','title':'t','orientation':'wide'})
 def test_labels_do_not_accept_expression_or_save(self):
  b={'project_id':'p','map_id':'m','layer_id':'l','field':'中文名称'}
  validate_args('set_labels',b)
  for change in [{'expression':'arbitrary code'},{'save_project':True}]:
   with self.assertRaises(BridgeError):validate_args('set_labels',b|change)
