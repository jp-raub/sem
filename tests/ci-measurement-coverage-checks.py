#!/usr/bin/env python3
"""Guard the pilot's mandatory public-candidate checks."""
from pathlib import Path
import unittest
root=Path(__file__).resolve().parents[1]

class Checks(unittest.TestCase):
    def test_full_fixture_retains_every_scope(self):
        s=(root/'scripts/acceptance-browser-fixture.sh').read_text()
        full=next(line for line in s.splitlines() if line.lstrip().startswith('full)'))
        for flag in ['SEM_PUBLIC_ANSWER_CHECKS=1','SEM_EVENT_HEADER_CHECKS=1','SEM_EVENT_DESCRIPTION_CHECKS=1','SEM_EVENT_LOCATION_CHECKS=1','SEM_EVENT_MATRIX_CHECKS=1','SEM_COMMENT_CHECKS=1','SEM_ACCEPTANCE_WORKFLOW_CHECKS=1','SEM_ACCEPTANCE_RETENTION_COMMAND=']:
            self.assertIn(flag,full)
        self.assertIn('SEM_STORAGE_UPGRADE_COMMAND=',s)
        self.assertIn('exit "$status"',s)
    def test_full_restore_and_private_data_not_removed(self):
        s=(root/'scripts/acceptance-browser-fixture.sh').read_text()
        for line in ['tar -czf','diff -qr','mv "$run/storage"','/health/ready']:
            self.assertIn(line,s)
    def test_release_gate_and_timeouts_stay_strict(self):
        s=(root/'.github/workflows/main-integration.yml').read_text()
        # Public-answer coverage is newly enabled; keep the revised ceiling explicit.
        browser=s.split('  acceptance-browser:',1)[1]
        self.assertIn('timeout-minutes: 30',browser)
        self.assertEqual(s.count('timeout-minutes:'),2)
        self.assertIn('timeout-minutes: 40',s.split('  acceptance-browser:',1)[0])
        self.assertIn('run: bash scripts/acceptance-browser-fixture.sh',s)
        gate=(root/'scripts/cd/release_gate.py').read_text()
        self.assertIn("REQUIRED = {'integration-checks', 'acceptance-browser'}",gate)
    def test_browser_artifacts_allowlist(self):
        s=(root/'.github/workflows/main-integration.yml').read_text().split('name: acceptance-browser-results',1)[1]
        paths=[line.strip() for line in s.splitlines() if 'verification/' in line]
        self.assertEqual(paths,['verification/acceptance-native/e2e-result.json','verification/acceptance-native/acceptance-phases.json','verification/acceptance-native/fixture-phases.tsv'])

if __name__=='__main__':unittest.main()
