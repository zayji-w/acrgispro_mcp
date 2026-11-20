"""Atomic local transport. Map/data mutations are never automatically retried."""
import json
import os
from pathlib import Path
import time
import uuid
from contracts import BridgeError,LEGACY,RASTER_OPS,THEMATIC_OPS,REQUEST_MAX_BYTES,RESPONSE_MAX_BYTES,loads,validate,validate_args
ROOT=Path(__file__).resolve().parent
CONFIG_PATH=Path(os.environ.get('GIS_AGENT_CONFIG',str(Path(os.environ.get('LOCALAPPDATA',''))/'GIS-Agent'/'bridge.config.json')))

def config():
 if CONFIG_PATH.exists():return json.loads(CONFIG_PATH.read_text(encoding='utf-8-sig'))
 return {'runtime_root':str(ROOT),'state_root':str(ROOT/'state'),'gp_catalog_path':str(ROOT/'gp_catalog.json')}

def write_json(path,value):
 path.parent.mkdir(parents=True,exist_ok=True)
 temp=path.with_name(path.name+'.'+uuid.uuid4().hex+'.tmp')
 temp.write_text(json.dumps(value,ensure_ascii=False,allow_nan=False),encoding='utf-8')
 os.replace(temp,path)


def read_response(path):
 with path.open('rb') as stream:
  raw = stream.read(RESPONSE_MAX_BYTES + 1)
 try:
  if len(raw) > RESPONSE_MAX_BYTES:
   raise BridgeError('RESPONSE_TOO_LARGE','Response exceeds 4 MiB. Use a paged read where available.')
  try:
   payload = loads(raw.decode('utf-8-sig'), response=True)
  except UnicodeDecodeError as exc:
   raise BridgeError('INVALID_RESPONSE','Response is not valid UTF-8') from exc
  if not isinstance(payload, dict) or type(payload.get('ok')) is not bool:
   raise BridgeError('INVALID_RESPONSE','Response is missing its status')
  return payload
 finally:
  try:
   path.unlink(missing_ok=True)
  except PermissionError:
   # A reader can temporarily prevent deletion on Windows. The response was already read.
   pass

def status():
 try:
  state=Path(config()['state_root']);session=loads((state/'session.json').read_text(encoding='utf-8-sig'));sid=session['id']
  if not isinstance(sid,str) or len(sid)!=32 or uuid.UUID(hex=sid).hex!=sid:raise ValueError('Invalid session')
  hb=loads((state/sid/'heartbeat.json').read_text(encoding='utf-8-sig'));age=time.time()-hb['time'];connected=bool(hb['active'] and 0<=age<5)
  return {'connected':connected,'reason':'ready' if connected else 'stale_or_stopped','heartbeat_age_seconds':round(age,2),'pid':hb['pid'],'session_id':sid,'backend':hb.get('backend','python-window'),'ui_nonblocking':hb.get('ui_nonblocking',False),'protocol_version':hb.get('protocol_version',1),'bridge_version':hb.get('version'),'capabilities':hb.get('capabilities',sorted(LEGACY)),'transport':'local files + stdio'}
 except (OSError,ValueError,KeyError,TypeError,BridgeError) as exc:return {'connected':False,'reason':'not_connected','protocol_version':None,'capabilities':[],'detail':type(exc).__name__}

def request(op,args=None,timeout=25):
 args=args or {};validate_args(op,args);info=status()
 if not info['connected']:raise BridgeError('NOT_CONNECTED','Open Pro, then Add-In > Safe GIS Bridge > Start bridge')
 v2=info['protocol_version']==2
 if not v2 and op not in LEGACY:raise BridgeError('CAPABILITY_UNAVAILABLE','Installed plugin does not support this tool')
 if op=='read_table' and op not in info.get('capabilities',[]):
  raise BridgeError('CAPABILITY_UNAVAILABLE','Attribute reads require SafeBridge 0.6.4 or later')
 if op in RASTER_OPS and op not in info.get('capabilities', []):
  raise BridgeError('CAPABILITY_UNAVAILABLE', 'Raster tools require SafeBridge 0.6.5 or later')
 if op in THEMATIC_OPS and op not in info.get('capabilities', []):
  raise BridgeError('CAPABILITY_UNAVAILABLE', 'Thematic renderers require SafeBridge 0.6.6 or later')
 if op=='map_state' and args and not v2:raise BridgeError('CAPABILITY_UNAVAILABLE','Map pagination requires the native bridge')
 if op=='set_labels' or {'offset','limit'} & args.keys() or (op in {'map_state','list_layouts'} and args):
  try:native_version=tuple(map(int,(info.get('bridge_version') or '').split('.')))
  except ValueError:native_version=()
  if not v2 or len(native_version)!=3 or native_version<(0,6,3):
   raise BridgeError('CAPABILITY_UNAVAILABLE','This operation requires SafeBridge 0.6.3 or later')
 state=Path(config()['state_root']);session=loads((state/'session.json').read_text(encoding='utf-8-sig'))
 if session['id']!=info['session_id']:raise BridgeError('STALE_CONTEXT','Bridge session changed')
 req={'id':uuid.uuid4().hex,'token':session['token'],'op':op,'expires':time.time()+timeout}
 if v2:req.update(protocol_version=2,args=args)
 validate(req,session['token'])
 if len(json.dumps(req,ensure_ascii=False,allow_nan=False).encode('utf-8'))>REQUEST_MAX_BYTES:raise BridgeError('INVALID_ARGUMENT','Request exceeds 64 KiB')
 folder=state/session['id'];command=folder/('cmd_'+req['id']+'.json');result=folder/('result_'+req['id']+'.json');write_json(command,req);deadline=time.monotonic()+timeout
 while time.monotonic()<deadline:
  try:
   payload=read_response(result)
  except (FileNotFoundError, PermissionError):
   # Wait for this exact response if the file has not arrived or is briefly locked.
   time.sleep(.1)
   continue
  if not payload.get('ok'):
   err=payload.get('error') or {}
   if isinstance(err,str):err={'code':'GP_FAILED','message':err}
   raise BridgeError(err.get('code','GP_FAILED'),err.get('message','Operation failed'),err.get('details'))
  if op in LEGACY:return {'ok':True,'result':payload.get('data',payload.get('result'))}
  return payload
 try:
  command.unlink(missing_ok=True)
 except PermissionError:
  pass
 raise BridgeError('REQUEST_OUTCOME_UNKNOWN','Request timed out. Reuse the same idempotency key for GP; do not blindly rerun.',{'request_id':req['id']})
