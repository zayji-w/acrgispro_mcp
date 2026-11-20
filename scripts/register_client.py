"""Update only arcgispro. Discover tools from the same registry used by stdio."""
import argparse
import asyncio
import json
import os
from pathlib import Path
import re
import sys
from tempfile import NamedTemporaryFile
import tomllib


def exported_tool_names(root):
    sys.path.insert(0, str(root))
    from gis_agent.app import create_server
    tools = asyncio.run(create_server().list_tools())
    names = [tool.name for tool in tools]
    if not names or len(names) != len(set(names)):
        raise RuntimeError('Invalid exported tools; configuration left unchanged')
    return names


def update_client_text(text, command, tools, server_script=None):
    before = tomllib.loads(text)
    servers = before.get('mcp_servers', {})
    if not isinstance(servers, dict):
        raise ValueError('mcp_servers must be a TOML table')
    installed = 'arcgispro' in servers
    service = dict(servers.get('arcgispro', {}))
    if set(service.get('enabled_tools', [])) != set(tools):
        service['enabled_tools'] = list(tools)
    service['command'] = str(command)
    fields = ['command', 'enabled_tools']
    if server_script is not None:
        service['args'] = ['-B', str(server_script)]
        fields.append('args')
    if not installed:
        newline = '\r\n' if '\r\n' in text else '\n'
        separator = (newline if text.endswith(newline) else newline * 2) if text else ''
        lines = ['[mcp_servers.arcgispro]', *(f'{key} = {json.dumps(service[key])}' for key in fields), '']
        updated = text + separator + newline.join(lines)
    else:
        match = re.search(
            r'(?m)^[ \t]*\[\s*mcp_servers\s*\.\s*(?:arcgispro|"arcgispro"|\'arcgispro\')'
            r'\s*\][ \t]*(?:#[^\r\n]*)?(?:\r?\n|$)(?P<body>[\s\S]*?)(?=^[ \t]*\[|\Z)', text)
        if not match:
            raise ValueError('Put arcgispro in its own [mcp_servers.arcgispro] table before updating')
        newline = '\r\n' if '\r\n' in text else '\n'
        body = match['body']
        prefix = text[:match.start('body')]
        if not prefix.endswith(('\r', '\n')):
            prefix += newline
        for key in fields:
            pattern = r'(?m)^[ \t]*command[ \t]*=[^\r\n]*' if key == 'command' else (
                r'(?ms)^[ \t]*' + key + r'[ \t]*=\s*\[(?:[^"\'\]]|"(?:\\.|[^"\\])*"|\'[^\']*\')*\]')
            replacement = key + ' = ' + json.dumps(service[key])
            if re.search(pattern, body):
                body = re.sub(pattern, lambda _: replacement, body, count=1)
            else:
                body = replacement + newline + body
        updated = prefix + body + text[match.end('body'):]
    after = tomllib.loads(updated)
    if after['mcp_servers']['arcgispro'] != service:
        raise RuntimeError('Service update validation failed')
    if installed:
        before['mcp_servers'].pop('arcgispro')
    after['mcp_servers'].pop('arcgispro')
    if 'mcp_servers' not in before and not after['mcp_servers']:
        after.pop('mcp_servers')
    if before != after:
        raise RuntimeError('Unrelated client settings changed')
    return updated


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--config', type=Path, default=Path.home() / '.codex/config.toml')
    parser.add_argument('--runtime-root', type=Path)
    parser.add_argument('--python-exe', type=Path, default=Path(sys.executable))
    parser.add_argument('--dry-run', action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    runtime = args.runtime_root
    if runtime is None:
        default_config = Path(os.environ.get('LOCALAPPDATA', '')) / 'GIS-Agent/bridge.config.json'
        bridge_config = Path(os.environ.get('GIS_AGENT_CONFIG', str(default_config)))
        runtime = Path(json.loads(bridge_config.read_text(encoding='utf-8-sig'))['runtime_root'])
    runtime = runtime.resolve()
    server_script = runtime / 'server.py'
    if not server_script.is_file():
        parser.error(f'No deployed server.py in {runtime}')
    python = args.python_exe.resolve()
    if not python.is_file():
        parser.error(f'Python executable not found: {python}')
    tools = exported_tool_names(runtime)
    exists = args.config.is_file()
    text = args.config.read_bytes().decode('utf-8') if exists else ''
    try:
        updated = update_client_text(text, python, tools, server_script)
    except (ValueError, TypeError, KeyError) as exc:
        parser.error(str(exc))
    if not args.dry_run and updated != text:
        if exists:
            backup = root / 'artifacts/backups/client-before.toml'
            backup.parent.mkdir(parents=True, exist_ok=True)
            if not backup.exists():
                backup.write_bytes(args.config.read_bytes())
        args.config.parent.mkdir(parents=True, exist_ok=True)
        with NamedTemporaryFile(dir=args.config.parent, delete=False) as writer:
            temporary = Path(writer.name)
            writer.write(updated.encode('utf-8'))
        try:
            temporary.replace(args.config)
        finally:
            temporary.unlink(missing_ok=True)
    print(json.dumps({'updated_service': 'arcgispro', 'enabled_tool_count': len(tools),
                      'unrelated_settings_preserved': True, 'dry_run': args.dry_run,
                      'content_changed': updated != text}))


if __name__ == '__main__':
    main()
