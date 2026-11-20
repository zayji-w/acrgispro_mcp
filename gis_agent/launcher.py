"""Start ArcGIS Pro from the interactive Windows session hosting MCP."""
import os
from pathlib import Path
import re
import subprocess
import time
from urllib.parse import unquote, urlsplit
import winreg

from mcp.server.mcpserver.exceptions import ToolError
from pywintypes import com_error, error as WindowsError
import pythoncom
import win32com.client
import win32con
import win32event
import win32ts

from bridge_common import config


LAUNCH_MUTEX = r'Local\GIS-Agent.ArcGISPro.Launch'
SWC_DESKTOP = 8
SWFO_NEEDDISPATCH = 1


def _local_file(value, suffix, label):
    if not isinstance(value, str) or not re.match(r'^[A-Za-z]:[\\/]', value) or ':' in value[2:]:
        raise ToolError(f'INVALID_ARGUMENT: {label} must be a local absolute {suffix} path')
    try:
        path = Path(value).resolve(strict=True)
    except (OSError, ValueError) as exc:
        raise ToolError(f'FILE_NOT_FOUND: {label} is missing or unreadable') from exc
    if not re.match(r'^[A-Za-z]:[\\/]', str(path)) or path.suffix.lower() != suffix or not path.is_file():
        raise ToolError(f'INVALID_ARGUMENT: {label} must be an existing local {suffix} file')
    return path


def _pro_executable():
    override = config().get('arcgis_pro_exe')
    if override:
        exe = _local_file(override, '.exe', 'ArcGIS Pro executable')
        if exe.name.casefold() != 'arcgispro.exe':
            raise ToolError('INVALID_ARGUMENT: arcgis_pro_exe must point to ArcGISPro.exe')
        return exe

    roots = []
    for hive in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
        try:
            with winreg.OpenKey(hive, r'SOFTWARE\ESRI\ArcGISPro', 0,
                               winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as key:
                root, _ = winreg.QueryValueEx(key, 'InstallDir')
                if root:
                    roots.append(Path(root))
        except OSError:
            continue
    program_files = os.environ.get('ProgramW6432') or os.environ.get('ProgramFiles')
    if program_files:
        roots.append(Path(program_files) / 'ArcGIS/Pro')
    if os.environ.get('LOCALAPPDATA'):
        roots.append(Path(os.environ['LOCALAPPDATA']) / 'Programs/ArcGIS/Pro')
    for root in roots:
        candidate = root / 'bin/ArcGISPro.exe'
        if candidate.is_file():
            return _local_file(str(candidate), '.exe', 'ArcGIS Pro executable')
    raise ToolError('FILE_NOT_FOUND: ArcGIS Pro was not found; set arcgis_pro_exe in bridge.config.json')


def _pro_pids(session):
    return {pid for session_id, pid, name, _ in win32ts.WTSEnumerateProcesses(None)
            if session_id == session and name.casefold() == 'arcgispro.exe'}


def _project_key(value):
    if not value:
        return None
    if value.lower().startswith('file:'):
        uri = urlsplit(value)
        if uri.netloc and uri.netloc.lower() != 'localhost':
            return None
        value = unquote(uri.path).lstrip('/')
    return os.path.normcase(os.path.normpath(value))


def _result(state, target, pid=None, started=False, bridge=None, project=None, **extra):
    project = project or {}
    return {
        'state': state,
        'ready': state == 'ready',
        'started': started,
        'pid': pid,
        'bridge_connected': bridge is not None,
        'bridge_version': bridge.get('bridge_version') if bridge else None,
        'project_path': project.get('project_path'),
        'project_id': project.get('project_id'),
        'active_map_id': project.get('active_map_id'),
        'requested_project_path': str(target) if target else None,
        **extra,
    }


def _shell_launch(exe, target):
    # The desktop shell supplies the normal Windows environment and owns the
    # application, so closing the stdio client's process job cannot kill Pro.
    pythoncom.CoInitializeEx(pythoncom.COINIT_APARTMENTTHREADED)
    try:
        windows = win32com.client.Dispatch('Shell.Application').Windows()
        hwnd = win32com.client.VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)
        desktop = windows.FindWindowSW(0, pythoncom.Empty, SWC_DESKTOP, hwnd, SWFO_NEEDDISPATCH)
        if desktop is None:
            raise ToolError('NO_DESKTOP_SESSION: The Windows Explorer desktop is unavailable')
        args = subprocess.list2cmdline([str(target)]) if target else ''
        desktop.Document.Application.ShellExecute(str(exe), args, str(exe.parent), 'open', win32con.SW_SHOWNORMAL)
    finally:
        pythoncom.CoUninitialize()


def _find_or_start(bridge, target, session, deadline):
    """Serialize process lookup and launch across MCP clients in this desktop session."""
    mutex = win32event.CreateMutex(None, False, LAUNCH_MUTEX)
    try:
        outcome = win32event.WaitForSingleObject(mutex, 1000)
        if outcome == win32event.WAIT_TIMEOUT:
            return None, False, _result('launch_in_progress', target)
        if outcome not in (win32event.WAIT_OBJECT_0, win32event.WAIT_ABANDONED_0):
            raise ToolError('PRO_LAUNCH_FAILED: Could not acquire the Windows launch lock')
        try:
            pids = _pro_pids(session)
            status = bridge.status()
            if status.get('connected'):
                pid = status.get('pid')
                if pid not in pids:
                    return None, False, _result('bridge_conflict', target, detail='An active heartbeat belongs to another session or a recently exited process. Check bridge_status before retrying.')
                return pid, False, None
            if len(pids) > 1:
                return None, False, _result('multiple_instances', target, running_pids=sorted(pids))
            if pids:
                return next(iter(pids)), False, None
            exe = _pro_executable()
            _shell_launch(exe, target)
            # Keep the mutex until the shell's process is visible to another client.
            visible_deadline = min(deadline, time.monotonic() + 2)
            pids = _pro_pids(session)
            while not pids and time.monotonic() < visible_deadline:
                time.sleep(.05)
                pids = _pro_pids(session)
            if len(pids) == 1:
                return next(iter(pids)), True, None
            if pids:
                return None, False, _result('multiple_instances', target, running_pids=sorted(pids))
            return None, False, _result('launch_unobserved', target, detail='Windows accepted the launch but no ArcGIS Pro process was observed. Check for a startup error before retrying.')
        finally:
            win32event.ReleaseMutex(mutex)
    finally:
        mutex.Close()


def launch(bridge, project_path=None, wait_seconds=25):
    if not 1 <= wait_seconds <= 30:
        raise ToolError('INVALID_ARGUMENT: wait_seconds must be between 1 and 30')
    deadline = time.monotonic() + wait_seconds
    target = _local_file(project_path, '.aprx', 'project_path') if project_path is not None else None
    try:
        session = win32ts.ProcessIdToSessionId(os.getpid())
        if session == 0:
            raise ToolError('NO_DESKTOP_SESSION: Run the MCP client in the signed-in Windows desktop session')
        pid, started, result = _find_or_start(bridge, target, session, deadline)
        if result is not None:
            return result
        while True:
            if pid not in _pro_pids(session):
                return _result('process_exited', target, pid, started)

            status = bridge.status()
            connected = (status.get('connected') and status.get('pid') == pid
                         and status.get('backend') == 'native-sdk' and status.get('protocol_version') == 2)
            current = None
            detail = {}
            phase = 'starting' if started else 'bridge_unavailable'
            remaining = deadline - time.monotonic()
            if connected:
                phase = 'project_unverified'
                if remaining >= .2:
                    try:
                        current = bridge.request('map_state', timeout=min(2, remaining))['result']
                    except (ToolError, KeyError, OSError) as exc:
                        detail = {'detail': str(exc)}
                    after = bridge.status()
                    connected = after.get('connected') and after.get('pid') == pid and after.get('session_id') == status.get('session_id')
                    if current is not None and connected:
                        current_path = current.get('project_path')
                        if target is None or _project_key(current_path) == _project_key(str(target)):
                            return _result('ready', target, pid, started, status, current)
                        if current_path:
                            return _result('project_mismatch', target, pid, started, status, current)
                        phase = 'waiting_for_project'

            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return _result(phase, target, pid, started, status if connected else None,
                               current if connected else None, **detail)
            time.sleep(min(.25, remaining))
    except (OSError, WindowsError, com_error, ValueError) as exc:
        raise ToolError(f'PRO_LAUNCH_FAILED: {exc}') from exc
