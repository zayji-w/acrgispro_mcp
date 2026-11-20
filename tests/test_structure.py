import asyncio
import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import Mock
from contracts import BridgeError
from gis_agent.app import create_server
from gis_agent.client import BridgeClient
from mcp.server.mcpserver.exceptions import ToolError

ROOT = Path(__file__).resolve().parents[1]


class StructureTests(unittest.TestCase):
    def test_factories_isolate_clients(self):
        first, second = Mock(return_value={}), Mock(return_value={})
        a = create_server(BridgeClient(send=first))
        b = create_server(BridgeClient(send=second))
        a.functions['pro_version']()
        first.assert_called_once_with('version')
        second.assert_not_called()
        b.functions['pro_map_state']()
        second.assert_called_once_with('map_state')

    def test_registered_function_is_validated_without_module_globals(self):
        send = Mock(return_value={'ok': True})
        app = create_server(BridgeClient(send=send))
        valid = dict(project_id='p', map_id='m', input_path='x', distance=2, idempotency_key='k')
        asyncio.run(app.call_tool('pro_buffer', valid))
        self.assertEqual(send.call_args.args[1]['parameters']['distance'], 2)
        send.reset_mock()
        for arguments in (valid | {'distance': '2'}, valid | {'unexpected': 1}, {'project_id': 'p'}):
            with self.subTest(arguments=arguments), self.assertRaises(ToolError):
                asyncio.run(app.call_tool('pro_buffer', arguments))
        send.assert_not_called()

    def test_integer_bool_and_nested_list_rejected_before_transport(self):
        send = Mock(return_value={})
        app = create_server(BridgeClient(send=send))
        cases = [('pro_project', dict(project_id='p', map_id='m', input_path='x', target_wkid=True, idempotency_key='k')),
                 ('pro_update_layout_elements', dict(project_id='p', layout_id='l', edits=[1])),
                 ('bridge_status', None)]
        for name, arguments in cases:
            with self.subTest(name=name), self.assertRaises(ToolError):
                asyncio.run(app.call_tool(name, arguments))
        send.assert_not_called()

    def test_duplicate_registration_cannot_silently_shadow(self):
        app = create_server()
        original = app.functions['pro_version']
        with self.assertRaisesRegex(ValueError, 'Duplicate MCP tool'):
            app.add_tool(lambda: {}, name='pro_version')
        self.assertIs(app.functions['pro_version'], original)
        self.assertEqual(len(asyncio.run(app.list_tools())), 53)

    def test_bridge_errors_keep_native_code_and_cause(self):
        original = BridgeError('STALE_CONTEXT', 'Project changed')
        client = BridgeClient(send=Mock(side_effect=original))
        with self.assertRaisesRegex(ToolError, 'STALE_CONTEXT') as failure:
            client.request('map_state')
        self.assertIs(failure.exception.__cause__, original)

    def test_runtime_manifest_covers_modules_and_has_no_path_escape(self):
        manifest = json.loads((ROOT / 'runtime-manifest.json').read_text(encoding='utf-8'))
        files = manifest['files']
        self.assertEqual(len(files), len({name.casefold() for name in files}))
        self.assertTrue({str(p.relative_to(ROOT)).replace('\\', '/') for p in (ROOT / 'gis_agent').rglob('*.py')} <= set(files))
        for file in files:
            path = ROOT / file
            self.assertTrue(path.resolve().is_relative_to(ROOT.resolve()), file)
            self.assertTrue(path.is_file(), file)
        self.assertEqual(manifest['protocol_version'], 2)

    def test_registration_preserves_other_services_and_full_whitelist(self):
        spec = importlib.util.spec_from_file_location('register_client', ROOT / 'scripts/register_client.py')
        register = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(register)
        text = '[settings]\ncustom = true\n[mcp_servers.arcgispro]\ncommand = "old"\nargs = ["server.py"]\nenabled_tools = ["old"]\n[mcp_servers.other]\ncommand = "untouched"\n'
        names = register.exported_tool_names(ROOT)
        result = register.update_client_text(text, 'new-python', names)
        import tomllib
        parsed = tomllib.loads(result)
        self.assertEqual(len(parsed['mcp_servers']['arcgispro']['enabled_tools']), 53)
        self.assertIn('pro_read_table', parsed['mcp_servers']['arcgispro']['enabled_tools'])
        self.assertIn('pro_launch', parsed['mcp_servers']['arcgispro']['enabled_tools'])
        self.assertEqual(parsed['mcp_servers']['arcgispro']['args'], ['server.py'])
        self.assertEqual(parsed['mcp_servers']['other']['command'], 'untouched')
        self.assertTrue(parsed['settings']['custom'])

    def test_registration_adds_a_new_codex_service(self):
        spec = importlib.util.spec_from_file_location('register_client', ROOT / 'scripts/register_client.py')
        register = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(register)
        import tomllib
        for text in ('', '[mcp_servers]\n', '[mcp_servers.other]\r\ncommand = "untouched"\r\n'):
            updated = register.update_client_text(text, 'C:/env/python.exe', ['bridge_status'],
                                                  'C:/自定义 目录/server.py')
            service = tomllib.loads(updated)['mcp_servers']['arcgispro']
            self.assertEqual(service['command'], 'C:/env/python.exe')
            self.assertEqual(service['args'], ['-B', 'C:/自定义 目录/server.py'])
            self.assertEqual(service['enabled_tools'], ['bridge_status'])
            self.assertTrue(updated.startswith(text))
            self.assertEqual(register.update_client_text(updated, 'C:/env/python.exe', ['bridge_status'],
                                                         'C:/自定义 目录/server.py'), updated)
