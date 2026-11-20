import json
from pathlib import Path
import unittest

CATALOG=(Path(__file__).parents[1]/'gp_catalog.json')
REQUIRED_TOOLS={
 'analysis.Clip','analysis.PairwiseClip','analysis.Buffer','analysis.PairwiseBuffer',
 'analysis.MultipleRingBuffer','management.Dissolve','analysis.PairwiseDissolve',
 'analysis.Intersect','analysis.PairwiseIntersect','analysis.Union','analysis.Erase',
 'analysis.PairwiseErase','analysis.Identity','analysis.SymDiff','analysis.SpatialJoin',
 'management.MinimumBoundingGeometry','management.FeatureToPoint','management.PolygonToLine',
 'management.FeatureToLine','management.MultipartToSinglepart','management.Project',
 'management.Merge','management.CopyFeatures','conversion.FeatureClassToGeodatabase',
 'analysis.Statistics','management.SelectLayerByAttribute','management.SelectLayerByLocation'}
EXCLUDED_TOOLS={'management.CalculateField','management.Append','management.AddField','analysis.Near'}

class CatalogTests(unittest.TestCase):
 def setUp(self):
  self.catalog=json.loads(CATALOG.read_text(encoding='utf-8'))
  self.tools=self.catalog['tools']
 def test_version_and_coverage(self):
  self.assertEqual(self.catalog['catalog_version'],2)
  self.assertTrue(REQUIRED_TOOLS<=set(self.tools),sorted(REQUIRED_TOOLS-set(self.tools)))
  self.assertFalse(EXCLUDED_TOOLS&set(self.tools),'In-place mutation tools must stay unregistered')
 def test_argument_contracts(self):
  for name,tool in self.tools.items():
   params=tool['params'];args=tool['arguments'];kind=tool['output']['kind']
   self.assertTrue(args,name)
   generated=sum(1 for a in args if a.get('generated_output'))
   generated_gdb=sum(1 for a in args if a.get('generated_gdb'))
   if kind in('feature_class','table'):
    self.assertEqual(generated,1,name);self.assertEqual(generated_gdb,0,name)
    self.assertIn(tool['output']['name_param'],params)
    self.assertEqual(params[tool['output']['name_param']]['type'],'output_name')
   elif kind=='feature_classes':
    self.assertEqual(generated_gdb,1,name);self.assertEqual(generated,0,name)
   else:
    self.assertEqual(generated+generated_gdb,0,name)
   for argument in args:
    sources=[key for key in('param','literal','generated_output','generated_gdb','distance_unit','spatial_reference') if key in argument]
    self.assertEqual(len(sources),1,(name,argument))
    if 'param' in argument:self.assertIn(argument['param'],params,name)
    if 'spatial_reference' in argument:
     self.assertEqual(params[argument['spatial_reference']['param']]['type'],'wkid',name)
   defaults=[value.get('default') for value in params.values() if 'default' in value]
   self.assertTrue(all(value is None or isinstance(value,(str,int,float,bool)) for value in defaults),name)
   self.assertEqual(tool['risk'] in('write_new_output','read_only'),True,name)
 def test_known_argument_orders(self):
  def order(name):return [argument.get('param') for argument in self.tools[name]['arguments']]
  self.assertEqual(order('analysis.Clip'),['input_path','clip_path',None])
  self.assertEqual(order('analysis.Buffer'),['input_path',None,None,'line_side','line_end_type','dissolve_option','dissolve_field','method'])
  self.assertEqual(order('management.Project'),['input_path',None,None,'transform_method',None,'preserve_shape',None,None])
  self.assertEqual(order('management.SelectLayerByAttribute'),['layer_id','selection_type','where_clause','invert_where_clause'])
  self.assertEqual(self.tools['analysis.Buffer']['params']['distance']['type'],'distance')
  self.assertEqual(self.tools['analysis.Buffer']['params']['dissolve_field']['required_if'],{'dissolve_option':['LIST']})
 def test_clip_mapping(self):
  clip=self.tools['analysis.Clip']
  self.assertEqual(clip['output']['kind'],'feature_class')
  self.assertEqual(clip['output']['default_name'],'clip')
  self.assertTrue(clip['requires_same_gcs'])
  self.assertEqual(clip['params']['clip_path']['geometry'],['Polygon'])
 def test_selection_tools_are_read_only(self):
  for name in('management.SelectLayerByAttribute','management.SelectLayerByLocation'):
   self.assertEqual(self.tools[name]['risk'],'read_only')
   self.assertEqual(self.tools[name]['output']['kind'],'selection')
