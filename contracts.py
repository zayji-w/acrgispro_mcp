"""Strict wire rules, independent of GIS and MCP."""
import json
import math
import re
import secrets
import time

LEGACY={'version','map_state','add_demo','remove_demo','stop'}
REQUEST_MAX_BYTES = 64 * 1024
RESPONSE_MAX_BYTES = 4 * 1024 * 1024
PAGING = {'offset': int, 'limit': int}
RASTER_JOB = {'project_id': str, 'map_id': str, 'idempotency_key': str}
RASTER_OUTPUT = {'add_to_map': bool, 'output_name': str}
CELL_SIZE = {'cell_size': (int, float)}
RASTER_INPUT = RASTER_JOB | {'input_path': str}
RESAMPLING = RASTER_OUTPUT | {'resampling': str}
LAYER = {'project_id': str, 'map_id': str, 'layer_id': str}
THEME_OPTIONS = {'colors': list, 'show_other': bool, 'other_label': str}
SCHEMAS={
 'map_state':({},PAGING | {'project_id':str,'map_id':str}),
 'describe_dataset':({'path':str},{'include_count':bool}),
 'read_table':({'path':str},PAGING | {'fields':list,'order_by':str,'direction':str,'include_count':bool}),
 'describe_raster': ({'path': str}, {}),
 'add_raster_layer': ({'project_id': str, 'map_id': str, 'path': str}, {'display_name': str}),
 'raster_clip': (RASTER_INPUT, RASTER_OUTPUT | {'mask_path': str, 'bbox': list}),
 'raster_project': (RASTER_INPUT | CELL_SIZE | {'target_wkid': int}, RESAMPLING | {'transform_method': str, 'snap_path': str}),
 'raster_resample': (RASTER_INPUT | CELL_SIZE, RESAMPLING),
 'raster_index': (RASTER_JOB | {'index': str, 'positive_path': str, 'negative_path': str}, RASTER_OUTPUT | {'positive_band': int, 'negative_band': int}),
 'raster_zonal_statistics': (RASTER_JOB | {'zone_path': str, 'zone_field': str, 'value_path': str}, {'statistics': str, 'ignore_nodata': bool, 'output_name': str}),
 'add_layer':({'project_id':str,'map_id':str,'path':str},{'display_name':str}),
 'set_layer_visibility':({'project_id':str,'map_id':str,'layer_id':str,'visible':bool},{}),
 'zoom_to_layer':({'project_id':str,'map_id':str,'layer_id':str},{}),
 'list_gp_tools':({},{}),
 'run_gp':({'project_id':str,'map_id':str,'tool':str,'parameters':dict,'idempotency_key':str},{}),
 'job_status':({'job_id':str},{}),'cancel_job':({'job_id':str},{}),
 'set_simple_renderer':({'project_id':str,'map_id':str,'layer_id':str,'color':str},{'outline_color':str,'width':(int,float),'size':(int,float),'outline_width':(int,float)}),
 'set_unique_value_renderer': (LAYER | {'field': str}, THEME_OPTIONS | {'category_limit': int}),
 'set_graduated_colors_renderer': (LAYER | {'field': str}, THEME_OPTIONS | {'method': str, 'class_count': int, 'breaks': list}),
 'get_thematic_renderer': (LAYER, {}),
 'set_labels':({'project_id':str,'map_id':str,'layer_id':str,'field':str},{'visible':bool,'font_size':(int,float),'color':str,'font_family':str,'class_name':str}),
 'list_layouts':({},PAGING | {'project_id':str}),
 'list_layout_elements':({'project_id':str,'layout_id':str},PAGING),
 'update_layout_elements':({'project_id':str,'layout_id':str,'edits':list},{'allow_locked':bool}),
 'add_layout_text':({'project_id':str,'layout_id':str,'name':str,'text':str,'x_cm':(int,float),'y_cm':(int,float)},{'width_cm':(int,float),'height_cm':(int,float),'font_size':(int,float),'font_family':str,'color':str,'rotation':(int,float)}),
 'place_city_name':({'project_id':str,'layout_id':str,'name':str,'text':str,'positions':list},{'font_size':(int,float),'font_family':str,'color':str}),
 'layer_presentation':({'project_id':str,'map_id':str,'layer_id':str},{}),
 'create_layout':({'project_id':str,'map_id':str,'extent_layer_id':str,'name':str,'title':str},{'orientation':str,'page_size':str,'legend':bool,'scale_bar':bool,'north_arrow':bool}),
 'open_layout':({'project_id':str,'layout_id':str},{}),
 'export_layout':({'project_id':str,'layout_id':str,'idempotency_key':str},{'format':str,'dpi':(int,float),'output_name':str}),
 'export_map':({'project_id':str,'map_id':str,'idempotency_key':str},{'dpi':(int,float),'width':(int,float),'height':(int,float),'output_name':str})}
OPS=LEGACY|SCHEMAS.keys()
RASTER_OPS = {'describe_raster', 'add_raster_layer'} | {name for name in SCHEMAS if name.startswith('raster_')}
THEMATIC_OPS = {'set_unique_value_renderer', 'set_graduated_colors_renderer', 'get_thematic_renderer'}

class BridgeError(RuntimeError):
 def __init__(self,code,message,details=None):
  self.code,self.details=code,details or {}
  super().__init__(f'{code}: {message}')

def _pairs(items):
 result={}
 for key,value in items:
  if key in result:raise BridgeError('INVALID_ARGUMENT','Duplicate JSON key')
  result[key]=value
 return result

def loads(text, *, response=False):
 limit = RESPONSE_MAX_BYTES if response else REQUEST_MAX_BYTES
 if len(text.encode('utf-8')) > limit:
  if response:raise BridgeError('RESPONSE_TOO_LARGE','Response exceeds 4 MiB. Use a paged read where available.')
  raise BridgeError('INVALID_ARGUMENT','Request exceeds 64 KiB')
 error_code = 'INVALID_RESPONSE' if response else 'INVALID_ARGUMENT'
 max_depth = 32 if response else 16
 def pairs(items):
  try:return _pairs(items)
  except BridgeError as exc:raise BridgeError(error_code,'Duplicate JSON key') from exc
 def invalid(value):raise BridgeError(error_code,'Non-finite JSON value')
 try:result=json.loads(text,object_pairs_hook=pairs,parse_constant=invalid)
 except (ValueError,RecursionError) as exc:raise BridgeError(error_code,'Invalid JSON') from exc
 def depth(x,level=0):
  if level>max_depth:raise BridgeError(error_code,'JSON too deep')
  if isinstance(x,dict):
   for v in x.values():depth(v,level+1)
  elif isinstance(x,list):
   for v in x:depth(v,level+1)
 depth(result)
 return result

def validate_args(op,args):
 if op not in OPS:raise BridgeError('UNKNOWN_OPERATION','Operation is not registered')
 required,optional=SCHEMAS.get(op,({},{}))
 if type(args) is not dict or not set(required)<=args.keys() or args.keys()-(required.keys()|optional.keys()):raise BridgeError('INVALID_ARGUMENT','Missing or unexpected arguments')
 for key,value in args.items():
  expected=(required|optional)[key]
  if type(value) not in (expected if isinstance(expected,tuple) else (expected,)) or (isinstance(value,str) and (not value.strip() or len(value)>4096)):raise BridgeError('INVALID_ARGUMENT',f'Invalid argument: {key}')
 if op in {'job_status','cancel_job'} and not re.fullmatch('[0-9a-f]{32}',args['job_id']):raise BridgeError('INVALID_ARGUMENT','Invalid job ID')
 if op=='run_gp' and len(args['idempotency_key'])>128:raise BridgeError('INVALID_ARGUMENT','Idempotency key exceeds 128 characters')
 if 'offset' in args and not 0<=args['offset']<=2147483647:raise BridgeError('INVALID_ARGUMENT','offset must be a nonnegative 32-bit integer')
 if 'limit' in args and not 1<=args['limit']<=100:raise BridgeError('INVALID_ARGUMENT','limit must be between 1 and 100')
 if op=='map_state' and 'map_id' in args and 'project_id' not in args:raise BridgeError('INVALID_ARGUMENT','map_id requires project_id')
 if op=='read_table':
  if args.get('direction','asc') not in ('asc','desc'):raise BridgeError('INVALID_ARGUMENT','direction must be asc or desc')
  if len(args.get('order_by',''))>128:raise BridgeError('INVALID_ARGUMENT','order_by must be one field name')
  fields=args.get('fields')
  if fields is not None:
   if not 1<=len(fields)<=32 or any(type(name) is not str or not name.strip() or len(name)>128 for name in fields):
    raise BridgeError('INVALID_ARGUMENT','fields must contain 1–32 field names')
   if len({name.casefold() for name in fields})!=len(fields):
    raise BridgeError('INVALID_ARGUMENT','fields contains duplicate names')
 _validate_presentation(op,args)
 _validate_layout_edit(op,args)
 if op in RASTER_OPS:
  _validate_raster(op, args)
 if op in THEMATIC_OPS:
  _validate_thematic(op, args)

def _validate_thematic(op, args):
 def invalid(name):
  raise BridgeError('INVALID_ARGUMENT', 'Invalid ' + name)

 for key, maximum in (('field', 128), ('other_label', 120)):
  if len(args.get(key, '')) > maximum:
   invalid(key)
 for key, low, high in (('category_limit', 1, 128), ('class_count', 2, 12)):
  if key in args and not low <= args[key] <= high:
   invalid(key)
 colors = args.get('colors')
 if colors is not None and (not 1 <= len(colors) <= 128 or
     any(type(color) is not str or not re.fullmatch(r'#[0-9a-fA-F]{6}', color) for color in colors)):
  invalid('colors')
 if op != 'set_graduated_colors_renderer':
  return
 edges = args.get('breaks')
 method = args.get('method', 'manual' if edges is not None else 'natural_breaks')
 if method not in ('natural_breaks', 'equal_interval', 'quantile', 'manual'):
  invalid('method')
 if (method == 'manual') != (edges is not None) or (edges is not None and 'class_count' in args):
  invalid('manual breaks or class_count')
 if edges is not None and (not 3 <= len(edges) <= 13 or
     any(type(x) not in (int, float) or not -float('inf') < x < float('inf') for x in edges)
     or any(a >= b for a, b in zip(edges, edges[1:]))):
  invalid('breaks')

def _validate_raster(op, args):
 def invalid(field):
  raise BridgeError('INVALID_ARGUMENT', 'Invalid ' + field)

 if len(args.get('idempotency_key', '')) > 128:
  invalid('idempotency_key')
 for key in ('positive_band', 'negative_band', 'target_wkid'):
  if key in args and not 1 <= args[key] <= 2_147_483_647:
   invalid(key)
 if 'cell_size' in args and not 0 < args['cell_size'] < float('inf'):
  invalid('cell_size')
 if op == 'raster_clip':
  if ('mask_path' in args) == ('bbox' in args):
   raise BridgeError('INVALID_ARGUMENT', 'Provide exactly one of mask_path and bbox')
  bbox = args.get('bbox')
  if bbox is not None and (len(bbox) != 4 or
      any(type(v) not in (int, float) or not -float('inf') < v < float('inf') for v in bbox) or
      not (bbox[0] < bbox[2] and bbox[1] < bbox[3])):
   invalid('bbox')

def _validate_layout_edit(op,a):
 def fail(message):raise BridgeError('INVALID_ARGUMENT',message)
 def num(row,key,lo,hi):
  if key in row and (type(row[key]) not in (int,float) or not math.isfinite(row[key]) or not lo<=row[key]<=hi):fail('Invalid '+key)
 def position(row):
  if type(row) is not dict or not {'x_cm','y_cm'}<=row.keys() or row.keys()-{'x_cm','y_cm','rotation','font_size'}:fail('Invalid character position')
  num(row,'x_cm',-1000,1000);num(row,'y_cm',-1000,1000);num(row,'rotation',-360,360);num(row,'font_size',4,144)
 if op=='update_layout_elements':
  if not 1<=len(a['edits'])<=100:fail('Provide 1-100 edits')
  names=set()
  for e in a['edits']:
   allowed={'element_name','expected_type','x_cm','y_cm','width_cm','height_cm','rotation','scale','font_size','text','visible'}
   if type(e) is not dict or not isinstance(e.get('element_name'),str) or not e['element_name'].strip() or e.keys()-allowed or not e.keys()-{'element_name','expected_type'}:fail('Invalid edit')
   if e['element_name'] in names:fail('Duplicate target in batch')
   names.add(e['element_name'])
   for key in ('x_cm','y_cm'):num(e,key,-1000,1000)
   for key in ('width_cm','height_cm'):num(e,key,.001,1000)
   num(e,'rotation',-360,360);num(e,'scale',.05,20);num(e,'font_size',4,144)
   if 'scale' in e and ('width_cm' in e or 'height_cm' in e):fail('Use scale or dimensions')
   if 'text' in e and (type(e['text']) is not str or len(e['text'])>4096):fail('Invalid text')
   if 'expected_type' in e and (type(e['expected_type']) is not str or not e['expected_type'].strip()):fail('Invalid expected_type')
   if 'visible' in e and type(e['visible']) is not bool:fail('Invalid visible')
 if op in ('add_layout_text','place_city_name'):
  if len(a['name'])>100 or len(a['text'])>4096:fail('Name/text too long')
  num(a,'font_size',4,144)
  if not re.fullmatch('#[0-9a-fA-F]{6}',a.get('color','#222222')):fail('Invalid color')
  if op=='add_layout_text':
   for key in ('x_cm','y_cm'):num(a,key,-1000,1000)
   for key in ('width_cm','height_cm'):num(a,key,.001,1000)
   num(a,'rotation',-360,360)
   if ('width_cm' in a)!=('height_cm' in a):fail('Paragraph requires width and height')
  else:
   if not 1<=len(a['text'])<=64 or len(a['positions'])!=len(a['text']):fail('Provide one position per character')
   for row in a['positions']:position(row)

def _validate_presentation(op,a):
 def fail(message):raise BridgeError('INVALID_ARGUMENT',message)
 def number(key,default,lo,hi):
  value=a.get(key,default)
  if not math.isfinite(value) or not lo<=value<=hi:fail('Invalid '+key)
  return value
 if op in {'set_simple_renderer','set_labels'}:
  for key in ('color','outline_color'):
   if key in a and not re.fullmatch('#[0-9a-fA-F]{6}',a[key]):fail('Color must be #RRGGBB')
  if op=='set_simple_renderer':
   number('width',1.5,.1,20);number('size',8,1,72);number('outline_width',.5,0,10)
  else:
   number('font_size',10,4,72)
   if len(a['field'])>128:fail('Field name too long')
 if op=='create_layout':
  if a.get('orientation','auto') not in ('auto','landscape','portrait') or a.get('page_size','A4') not in ('A4','A3'):fail('Invalid page specification')
  if len(a['name'])>100 or len(a['title'])>200:fail('Layout name/title too long')
 if op in ('export_layout','export_map'):
  dpi=number('dpi',300,72,600)
  if int(dpi)!=dpi:fail('dpi must be integer')
  if len(a['idempotency_key'])>128:fail('Idempotency key too long')
  if not re.fullmatch('[A-Za-z][A-Za-z0-9_]{0,63}',a.get('output_name','map')):fail('Invalid output name')
  if op=='export_layout' and a.get('format','PNG') not in ('PNG','PDF'):fail('Unsupported export format')
  if op=='export_map':
   w=number('width',2400,256,12000);h=number('height',1800,256,12000)
   if int(w)!=w or int(h)!=h or w*h>40000000:fail('Invalid pixel dimensions')

def validate(req,token,request_id=None,now=None):
 if type(req) is not dict:raise BridgeError('INVALID_ARGUMENT','Request must be object')
 v2='protocol_version' in req
 if set(req)!={'id','token','op','expires'}|({'protocol_version','args'} if v2 else set()):raise BridgeError('INVALID_ARGUMENT','Unexpected request fields')
 if not isinstance(req['id'],str) or not re.fullmatch('[0-9a-f]{32}',req['id']) or (request_id and req['id']!=request_id):raise BridgeError('INVALID_ARGUMENT','Invalid request ID')
 if not isinstance(req['token'],str) or not secrets.compare_digest(req['token'],token):raise BridgeError('UNAUTHORIZED','Invalid session token')
 current=time.time() if now is None else now
 expires=req['expires']
 if type(expires) not in (int,float) or not math.isfinite(expires) or not current<=expires<=current+60:raise BridgeError('REQUEST_EXPIRED','Invalid request lifetime')
 if v2:
  if type(req['protocol_version']) is not int or req['protocol_version']!=2:raise BridgeError('PROTOCOL_MISMATCH','Protocol 2 required')
  validate_args(req['op'],req['args'])
 elif req['op'] not in LEGACY:raise BridgeError('UNKNOWN_OPERATION','Legacy operation is not registered')
 return req
