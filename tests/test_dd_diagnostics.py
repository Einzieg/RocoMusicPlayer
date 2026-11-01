import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import dd_diagnostics as diagnostics


class DiagnosticsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.log_patch = patch('dd_diagnostics.record', return_value=True)
        self.log = self.log_patch.start()
        self.addCleanup(self.log_patch.stop)

    def test_known_library_hash_enforced_but_custom_version_is_unverified(self):
        path = self.root / 'ddhid.63340.dll'
        path.write_bytes(b'fixture')
        with self.assertRaisesRegex(RuntimeError, 'mismatch'):
            diagnostics.verify_library(path)
        with patch.dict(diagnostics.PACKAGE_HASHES, {path.name: hashlib.sha256(b'fixture').hexdigest()}):
            self.assertEqual(diagnostics.verify_library(path)['status'], 'verified')
        custom = self.root / 'custom.dll'
        custom.write_bytes(b'fixture')
        self.assertEqual(diagnostics.verify_library(custom)['status'], 'unverified')
        with self.assertRaisesRegex(RuntimeError, 'missing'):
            diagnostics.verify_library(self.root / 'missing.dll')

    def test_pinned_hashes_are_full_sha256_values(self):
        self.assertEqual(len(diagnostics.PACKAGE_HASHES), 5)
        for digest in diagnostics.PACKAGE_HASHES.values():
            self.assertRegex(digest, r'^[0-9a-f]{64}$')

    def test_report_exposes_device_problem_and_missing_package_without_loading(self):
        path = self.root / 'ddhid.63340.dll'
        path.write_bytes(b'changed')
        device = {'registered': True, 'state': 4, 'devices': [
            {'id': 'ROOT\\FIXTURE', 'problem': 52, 'started': False, 'query_result': 0}]}
        with patch('dd_setup.inspect_driver', return_value=device), \
                patch('dd_setup.subprocess.Popen') as launch, patch('ctypes.WinDLL') as load, \
                patch.dict('os.environ', {'PROGRAMDATA': str(self.root)}):
            report = diagnostics.diagnose(path)
        load.assert_not_called()
        launch.assert_not_called()
        self.assertEqual(report['device']['devices'][0]['problem'], 52)
        self.assertEqual(len(report['files']), 8)
        self.assertEqual(report['files'][0]['status'], 'mismatch')
        self.assertTrue(any('PnP' in issue for issue in report['issues']))
        self.assertTrue(report['log_saved'])

    def test_query_failure_and_pending_marker_are_reported_without_deleting(self):
        marker = self.root / 'RocoPlayer' / 'logs' / 'dd-install-pending.json'
        marker.parent.mkdir(parents=True)
        marker.write_text('{}', encoding='utf-8')
        with patch('dd_setup.inspect_driver', side_effect=OSError('access denied')), \
                patch.dict('os.environ', {'PROGRAMDATA': str(self.root)}):
            report = diagnostics.diagnose(None)
        self.assertIsNone(report['device'])
        self.assertTrue(report['pending_install'])
        self.assertTrue(marker.is_file())
        self.assertTrue(any('access denied' in issue for issue in report['issues']))

    def test_runtime_logs_are_utf8_bounded_and_json(self):
        self.log_patch.stop()
        path = self.root / 'logs' / 'runtime.jsonl'
        with patch('dd_diagnostics._logger', None), patch('dd_diagnostics.runtime_log_path', return_value=path):
            try:
                self.assertTrue(diagnostics.record('fixture', message='诊断'))
                handler = diagnostics._logger.handlers[0]
                handler.maxBytes = 300
                for _ in range(12):
                    diagnostics.record('fixture', message='rotation' * 15)
                self.assertLessEqual(len(list(path.parent.glob('*'))), 3)
                self.assertTrue(Path(str(path) + '.1').is_file())
                entry = json.loads(path.read_text(encoding='utf-8').splitlines()[-1])
                self.assertEqual(entry['event'], 'fixture')
                self.assertIn('pid', entry)
            finally:
                if diagnostics._logger:
                    for handler in diagnostics._logger.handlers:
                        handler.close()

    def test_unwritable_runtime_log_does_not_raise(self):
        self.log_patch.stop()
        with patch('dd_diagnostics._logger', None), \
                patch('dd_diagnostics.runtime_log_path', side_effect=OSError('disk full')), \
                patch('dd_diagnostics.sys.stderr'):
            self.assertFalse(diagnostics.record('fixture'))


if __name__ == '__main__':
    unittest.main()
