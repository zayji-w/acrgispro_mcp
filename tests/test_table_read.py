import unittest

from contracts import BridgeError, validate_args


class TableReadRequestTests(unittest.TestCase):
    def test_table_request_allowlist(self):
        base = {'path': r'D:\fixture\data.gdb\samples'}
        validate_args('read_table', base | {'fields': ['OID', 'VALUE'], 'order_by': 'VALUE',
                                           'direction': 'desc', 'offset': 2, 'limit': 100})
        for options in ({'fields': []}, {'fields': ['ID', 'id']}, {'fields': [7]},
                        {'direction': 'DESC'}, {'offset': -1}, {'limit': 101},
                        {'where_clause': '1=1'}):
            with self.subTest(options=options), self.assertRaisesRegex(BridgeError, 'INVALID_ARGUMENT'):
                validate_args('read_table', base | options)
