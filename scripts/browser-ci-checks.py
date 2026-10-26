#!/usr/bin/env python3
"""ブラウザーCIの局所的な契約検査（YAML一般の妥当性検査ではない）。"""
from pathlib import Path
import re
import unittest

WORKFLOW = Path(__file__).resolve().parents[1] / '.github/workflows/main-integration.yml'

class BrowserCiChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workflow = WORKFLOW.read_text()
        cls.job = cls.workflow.split('  acceptance-browser:\n', 1)[1]

    def test_pinned_matching_browser(self):
        image = re.search(r'image: mcr\.microsoft\.com/playwright:v([\d.]+)-noble@sha256:[0-9a-f]{64}', self.job)
        self.assertIsNotNone(image)
        self.assertIn('playwright@' + image.group(1), self.job)
        self.assertIn("PLAYWRIGHT_BROWSERS_PATH: /ms-playwright", self.job)
        self.assertIn('chromium.executablePath()', self.job)

    def test_no_runtime_os_install(self):
        for forbidden in ('--with-deps', 'apt-get', 'playwright install '):
            self.assertNotIn(forbidden, self.job)
        self.assertIn("PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD: '1'", self.job)

    def test_required_checks_preserved(self):
        self.assertIn('  integration-checks:\n', self.workflow)
        self.assertIn('    timeout-minutes: 8\n', self.job)
        self.assertEqual(self.job.count('run: node scripts/runtime-browser.cjs'), 1)
        self.assertEqual(self.job.count('run: node scripts/ui-inventory-browser.cjs'), 1)
        self.assertNotIn('continue-on-error', self.job)

    def test_bounded_separate_stages(self):
        stages = self.job.split('      - name: ')[1:6]
        self.assertEqual(len(stages), 5)
        for stage in stages:
            self.assertRegex(stage, r'\n        timeout-minutes: [12]\n')
        self.assertIn('--fetch-retries=1 --fetch-timeout=30000', self.job)
        self.assertIn('--connect-timeout 1 --max-time 2', self.job)
        self.assertIn('exit 1', self.job)

    def test_cleanup_diagnostics_no_secret(self):
        self.assertIn('if: always()', self.job)
        self.assertIn('kill "$(cat /tmp/sem-browser-host.pid)"', self.job)
        self.assertIn('if: failure()', self.job)
        self.assertIn('path: /tmp/sem-browser-host.log', self.job)
        self.assertIn('retention-days: 3', self.job)
        self.assertNotIn('secrets.', self.job)
        self.assertNotIn('SEM_PUBLIC_ORIGIN', self.job)

if __name__ == '__main__':
    unittest.main()
