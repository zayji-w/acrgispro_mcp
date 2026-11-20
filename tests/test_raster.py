import asyncio
import unittest
from unittest.mock import patch

import bridge_common
from contracts import BridgeError, validate_args
from gis_agent.app import create_server


class RasterContractTests(unittest.TestCase):
    target = dict(project_id='project', map_id='map', idempotency_key='test-key')

    def test_clip_bounds_and_mask_are_exclusive(self):
        base = dict(self.target, input_path='C:/images/source.tif')
        validate_args('raster_clip', dict(base, bbox=[1, 1, 3, 4]))
        validate_args('raster_clip', dict(base, mask_path='C:/data/a.gdb/mask'))
        for extra in ({}, {'bbox': [0, 0, 0, 3]}, {'bbox': [0, 0, 3, True]},
                      {'bbox': [0, 0, 3, float('inf')]},
                      {'bbox': [1, 1, 3, 4], 'mask_path': 'C:/data/a.gdb/mask'}):
            with self.subTest(extra=extra), self.assertRaises(BridgeError):
                validate_args('raster_clip', dict(base, **extra))

    def test_positive_cell_size_target_and_band(self):
        base = dict(self.target, input_path='C:/images/source.tif', target_wkid=4326, cell_size=.001)
        validate_args('raster_project', base)
        for extra in ({'cell_size': 0}, {'cell_size': float('nan')}, {'target_wkid': -1}, {'target_wkid': 1.5}):
            with self.subTest(extra=extra), self.assertRaises(BridgeError):
                validate_args('raster_project', base | extra)
        args = dict(self.target, index='NDVI', positive_path='C:/a.tif', negative_path='C:/b.tif')
        validate_args('raster_index', dict(args, positive_band=1, negative_band=2))
        for band in (0, True, 1.2, 2_147_483_648):
            with self.subTest(band=band), self.assertRaises(BridgeError):
                validate_args('raster_index', dict(args, positive_band=band))

    def test_raster_requires_installed_native_capability(self):
        old_bridge = dict(connected=True, protocol_version=2, capabilities=[])
        with patch.object(bridge_common, 'status', return_value=old_bridge):
            with self.assertRaisesRegex(BridgeError, 'CAPABILITY_UNAVAILABLE'):
                bridge_common.request('describe_raster', {'path': 'C:/source.tif'})

    def test_all_tool_routes_and_read_only_annotation(self):
        class Bridge:
            def request(self, operation, args):
                validate_args(operation, args)
                return dict(operation=operation, args=args)

        server = create_server(Bridge())
        tools = {tool.name: tool for tool in asyncio.run(server.list_tools())}
        target = {k: v for k, v in self.target.items() if k != 'idempotency_key'}
        for op, args in {
            'describe_raster': {'path': 'C:/source.tif'},
            'add_raster_layer': dict(target, path='C:/source.tif'),
            'raster_clip': dict(self.target, input_path='C:/source.tif', bbox=[0, 1, 2, 3]),
            'raster_project': dict(self.target, input_path='C:/source.tif', target_wkid=4326, cell_size=.01),
            'raster_resample': dict(self.target, input_path='C:/source.tif', cell_size=5),
            'raster_index': dict(self.target, index='NDWI', positive_path='C:/a.tif', negative_path='C:/b.tif'),
            'raster_zonal_statistics': dict(self.target, zone_path='C:/a.gdb/zones', zone_field='code',
                                            value_path='C:/source.tif')
        }.items():
            with self.subTest(op=op):
                tool = 'pro_' + op
                result = server.functions[tool](**args)
                self.assertEqual(result['operation'], op)
                self.assertEqual(tools[tool].annotations.read_only_hint, op == 'describe_raster')
