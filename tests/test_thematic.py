import asyncio
import unittest
from unittest.mock import patch

import bridge_common
from contracts import BridgeError, validate_args
from gis_agent.app import create_server


class ThematicContractTests(unittest.TestCase):
    target = dict(project_id='p', map_id='m', layer_id='l')
    unique = dict(target, field='LAND_USE')
    graded = dict(target, field='DENSITY')

    def test_manual_breaks_must_be_finite_ordered_and_exclusive(self):
        validate_args('set_graduated_colors_renderer', dict(self.graded, breaks=[0, 10, 20]))
        for options in ({'breaks': [0, 10, 10]}, {'breaks': [0, True, 20]},
                        {'breaks': [0, 10, float('inf')]}, {'breaks': [0, 1]},
                        {'method': 'manual'}, {'method': 'quantile', 'breaks': [0, 10, 20]},
                        {'class_count': 3, 'breaks': [0, 10, 20]}, {'class_count': 99}):
            with self.subTest(options=options), self.assertRaises(BridgeError):
                validate_args('set_graduated_colors_renderer', self.graded | options)

    def test_categorical_options_have_strict_types(self):
        validate_args('set_unique_value_renderer', dict(self.unique, colors=['#123abc'], show_other=False))
        for options in ({'category_limit': True}, {'category_limit': 0}, {'colors': []},
                        {'colors': ['red']}, {'colors': ['#112233\n']}, {'other_label': 'x' * 121}):
            with self.subTest(options=options), self.assertRaises(BridgeError):
                validate_args('set_unique_value_renderer', self.unique | options)

    def test_tool_routes_and_annotation(self):
        class Bridge:
            def request(self, operation, args):
                validate_args(operation, args)
                return operation

        server = create_server(Bridge())
        tools = {tool.name: tool for tool in asyncio.run(server.list_tools())}
        for op, args in (
            ('set_unique_value_renderer', self.unique),
            ('set_graduated_colors_renderer', self.graded),
            ('get_thematic_renderer', self.target),
        ):
            with self.subTest(tool=op):
                self.assertEqual(server.functions['pro_' + op](**args), op)
                self.assertEqual(tools['pro_' + op].annotations.read_only_hint, op.startswith('get_'))

    def test_older_bridge_has_clear_capability_error(self):
        with patch.object(bridge_common, 'status', return_value=dict(connected=True, protocol_version=2, capabilities=[])):
            with self.assertRaisesRegex(BridgeError, 'CAPABILITY_UNAVAILABLE'):
                bridge_common.request('get_thematic_renderer', self.target)
