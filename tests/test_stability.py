from pathlib import Path
from tempfile import TemporaryDirectory
import time
import unittest
from unittest.mock import patch

import bridge_common
from bridge_common import read_response
from contracts import BridgeError, RESPONSE_MAX_BYTES, loads, validate_args


class StabilityTests(unittest.TestCase):
    def test_large_response_does_not_raise_request_limit(self):
        response = '{"ok":true,"data":"' + '河' * 28000 + '"}'
        with self.assertRaisesRegex(BridgeError, 'Request exceeds 64 KiB'):
            loads(response)
        with TemporaryDirectory() as folder:
            path = Path(folder) / 'response.json'
            path.write_text(response, encoding='utf-8')
            self.assertEqual(loads(response, response=True), read_response(path))
            self.assertFalse(path.exists())

    def test_invalid_and_oversized_responses_are_removed(self):
        with TemporaryDirectory() as folder:
            path = Path(folder) / 'response.json'
            for payload, code in [
                (b'{"ok":true,"ok":false}', 'INVALID_RESPONSE'),
                (b'{"ok":true,"data":"\xff"}', 'INVALID_RESPONSE'),
                (b' ' * (RESPONSE_MAX_BYTES + 1), 'RESPONSE_TOO_LARGE'),
            ]:
                path.write_bytes(payload)
                with self.assertRaisesRegex(BridgeError, code):
                    read_response(path)
                self.assertFalse(path.exists())

    def test_page_validation_and_map_scope(self):
        validate_args('map_state', {})
        validate_args('map_state', {'project_id': 'p', 'map_id': 'm', 'offset': 0, 'limit': 100})
        for operation, arguments in [
            ('map_state', {'map_id': 'm'}),
            ('list_layouts', {'limit': 101}),
            ('list_layouts', {'offset': -1}),
            ('list_layouts', {'offset': 2147483648}),
            ('list_layouts', {'limit': True}),
        ]:
            with self.subTest(operation=operation, arguments=arguments):
                with self.assertRaisesRegex(BridgeError, 'INVALID_ARGUMENT'):
                    validate_args(operation, arguments)

    def test_locked_response_is_retried_without_resubmitting_the_command(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            session_id = '1' * 32
            folder = root / session_id
            writer = bridge_common.write_json
            writer(root / 'session.json', {'id': session_id, 'token': 'ab' * 32})
            writer(folder / 'heartbeat.json', {'active': True, 'time': time.time(),
                                                'protocol_version': 2, 'pid': 1})
            commands, response_reads = [], []
            real_open = Path.open

            def publish(path, command):
                commands.append(path)
                writer(path, command)
                writer(folder / ('result_' + command['id'] + '.json'), {'ok': True, 'data': {'visible': True}})

            def open_with_transient_lock(path, mode, *args, **kwargs):
                if mode == 'rb' and path.name.startswith('result_'):
                    response_reads.append(path)
                    if len(response_reads) == 1:
                        raise PermissionError(13, 'Temporary sharing violation', str(path))
                return real_open(path, mode, *args, **kwargs)

            with (patch.object(bridge_common, 'config', return_value={'state_root': directory}),
                  patch.object(bridge_common, 'write_json', side_effect=publish),
                  patch.object(Path, 'open', open_with_transient_lock)):
                reply = bridge_common.request('set_layer_visibility',
                                              {'project_id': 'p', 'map_id': 'm', 'layer_id': 'l', 'visible': True})
            self.assertEqual(reply['data'], {'visible': True})
            self.assertEqual(len(commands), 1)
            self.assertEqual(len(response_reads), 2)
            self.assertEqual(response_reads[0], response_reads[1])
            self.assertFalse(response_reads[1].exists())
