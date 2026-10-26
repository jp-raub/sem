#!/usr/bin/env python3
import copy
import io
import json
import os
import contextlib
import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch
from email.message import Message
import urllib.error
import observe

TEST_ORIGIN = 'https://runtime.example.invalid'

C = dict(deployment_mode='auto', schema=1, repository='jp-raub/sem', commit='a'*40,
    image='ghcr.io/jp-raub/sem@sha256:'+'b'*64, platform='linux/amd64',
    integration_run=1, integration_attempt=1, publication_run=2, publication_attempt=3)

class Checks(unittest.TestCase):
    def good(self, path, nonce):
        return (dict(observe.expected_identity(C), probe=nonce) if path == '/api/version'
                else dict(status='mock-ui-ready', probe=nonce))
    def run_observation(self, fetch, budget=100):
        tick=[0]
        def wait(seconds): tick[0]+=seconds
        return observe.observe(C,fetch,clock=lambda:tick[0],wait=wait,budget=budget,interval=20)
    def test_three_matches_require_time_separation(self):
        calls=[]
        def fetch(p,n):calls.append((p,n));return self.good(p,n)
        self.assertEqual(self.run_observation(fetch)['state'],'runtime-match')
        self.assertEqual(len(calls),6)
        self.assertEqual(len({n for p,n in calls}),6)
    def test_mismatch_resets_consecutive_matches(self):
        calls=[0]
        def fetch(p,n):
            calls[0]+=1
            if calls[0]==3: raise observe.ObservationError('STALE')
            return self.good(p,n)
        self.assertEqual(self.run_observation(fetch,budget=80)['state'],'unconfirmed')
    def reject_mutation(self, change):
        def fetch(p,n):
            d=self.good(p,n)
            if p=='/api/version':change(d)
            return d
        self.assertEqual(self.run_observation(fetch)['state'],'unconfirmed')
    def test_same_sha_different_build(self):self.reject_mutation(lambda d:d.update(buildId='run-2-4',publicationAttempt='4'))
    def test_old_sha(self):self.reject_mutation(lambda d:d.update(commit='c'*40))
    def test_cache_nonce(self):self.reject_mutation(lambda d:d.update(probe='0'*32))
    def test_boolean_schema(self):self.reject_mutation(lambda d:d.update(schema=True))
    def test_extra_key(self):self.reject_mutation(lambda d:d.update(host='private'))
    def test_missing_key(self):self.reject_mutation(lambda d:d.pop('buildId'))
    def test_development_build(self):self.reject_mutation(lambda d:d.update(channel='development',commit=None,buildId=None))
    def test_non_string_run(self):self.reject_mutation(lambda d:d.update(publicationRun=2))
    def test_unready_or_cached_readiness(self):
        for ready in ({'status':'mock-ui-ready','probe':'old'}, {'status':'failed','probe':None}, {}):
            self.assertEqual(self.run_observation(lambda p,n:self.good(p,n) if p=='/api/version' else ready)['state'],'unconfirmed')
    def test_timeout_never_means_success(self):
        def fail(p,n):raise observe.ObservationError('HTTPS_OR_INVALID_RESPONSE')
        self.assertEqual(self.run_observation(fail),dict(state='unconfirmed',samples=0,reason='HTTPS_OR_INVALID_RESPONSE'))
    def event(self):
        return dict(workflow_run=dict(repository={'full_name':'jp-raub/sem'},head_repository={'full_name':'jp-raub/sem'},
            event='workflow_run',head_branch='main',status='completed',conclusion='success',id=2,run_attempt=3,head_sha='a'*40))
    def test_eligible_trigger(self):
        class GH:
            def latest(self):return copy.deepcopy(C)
        self.assertEqual(observe.eligible(GH(),self.event()),C)
        self.assertEqual(observe.eligible(GH(),None),C)
    def test_other_or_failed_trigger_not_accepted(self):
        class GH:
            def latest(self):return C
        for key,val in [('id',9),('run_attempt',4),('head_sha','c'*40),('conclusion','failure'),('event','pull_request')]:
            event=self.event();event['workflow_run'][key]=val
            with self.assertRaises(observe.ObservationError):observe.eligible(GH(),event)
    def test_no_eligible_candidate(self):
        class GH:
            def latest(self):return None
        with self.assertRaises(observe.ObservationError):observe.eligible(GH(),None)
    def http(self, *, body=None, status=200, content_type='application/json', cache='no-store', final=None):
        probe='a'*32
        url=TEST_ORIGIN+'/api/version?probe='+probe
        class Response(io.BytesIO):
            def geturl(self):return final or url
        response=Response(body if body is not None else json.dumps(self.good('/api/version',probe)).encode())
        response.status=status;response.headers=Message();response.headers['Content-Type']=content_type
        response.headers['Cache-Control']=cache
        class Opener:
            def open(inner,req,timeout):
                self.assertEqual(req.full_url,url)
                self.assertNotIn('Authorization',req.headers)
                return response
        with patch('observe.urllib.request.build_opener',return_value=Opener()):
            return observe.fresh_json('/api/version',probe,origin=TEST_ORIGIN)
    def test_fresh_https_json(self):self.assertEqual(self.http()['commit'],'a'*40)
    def test_http_error_html_cache_redirect_and_large_rejected(self):
        for opts in [dict(status=503),dict(status=302),dict(content_type='text/html'),dict(cache='public'),
                     dict(final='https://evil.invalid'),dict(body=b'x'*8193),dict(body=b'<html>OK</html>'),dict(body=b'{"x":1,"x":2}')]:
            with self.assertRaises(observe.ObservationError):self.http(**opts)
    def test_tls_network_and_redirect_errors_rejected(self):
        for error in [urllib.error.URLError('synthetic TLS error'),observe.CDException('Unexpected HTTP redirect')]:
            with patch('observe.urllib.request.build_opener') as make:
                make.return_value.open.side_effect=error
                with self.assertRaises(observe.ObservationError):observe.fresh_json('/api/version','a'*32,origin=TEST_ORIGIN)
    def test_no_arbitrary_url_or_probe(self):
        for path,probe in [('https://evil.invalid','a'*32),('/api/version','bad')]:
            with self.assertRaises(observe.ObservationError):observe.fresh_json(path,probe)
    def test_origin_is_required_and_no_network_fallback(self):
        with patch.dict(os.environ, {}, clear=True), patch('observe.urllib.request.build_opener') as make:
            with self.assertRaisesRegex(observe.ObservationError, '^PUBLIC_ORIGIN_REQUIRED$'):
                observe.fresh_json('/api/version', 'a'*32)
            make.assert_not_called()
    def test_environment_origin_and_normalization(self):
        with patch.dict(os.environ, {'SEM_PUBLIC_ORIGIN':'https://RUNTIME.example.invalid:8443/'}):
            self.assertEqual(observe.configured_origin(),'https://runtime.example.invalid:8443')
        self.assertEqual(observe.configured_origin('https://[2001:db8::1]:8443'),'https://[2001:db8::1]:8443')
    def test_invalid_origins_are_rejected_without_value_disclosure(self):
        for value in ['http://runtime.example.invalid','https://name:password@runtime.example.invalid',
                      'https://runtime.example.invalid/path','https://runtime.example.invalid?token=hidden',
                      'https://runtime.example.invalid#hidden','https://runtime.example.invalid?',
                      'https://runtime.example.invalid:0','https://runtime.example.invalid:65536',
                      'https://runtime.example.invalid:','https://bad_host.invalid','https://-bad.invalid',
                      'https://runtime.example.invalid\n',' https://runtime.example.invalid','https://[broken']:
            with patch('observe.urllib.request.build_opener') as make:
                with self.assertRaisesRegex(observe.ObservationError, '^INVALID_PUBLIC_ORIGIN$'):
                    observe.fresh_json('/api/version','a'*32,origin=value)
                make.assert_not_called()
    def test_main_missing_origin_fails_before_candidate_network(self):
        with tempfile.TemporaryDirectory() as folder:
            before=os.getcwd()
            try:
                os.chdir(folder)
                with patch.dict(os.environ, {'GITHUB_REPOSITORY':'jp-raub/sem','GITHUB_EVENT_NAME':'workflow_dispatch'},clear=True), patch('observe.GitHub') as gh, contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(observe.main(),1)
                    gh.assert_not_called()
                    report=json.loads(Path('verification/runtime-observation.json').read_text())
                    self.assertEqual(report['reason'],'PUBLIC_ORIGIN_REQUIRED')
            finally:os.chdir(before)
    def test_summary_and_artifact_never_include_origin(self):
        with tempfile.TemporaryDirectory() as folder:
            before=os.getcwd()
            try:
                os.chdir(folder)
                env={'GITHUB_REPOSITORY':'jp-raub/sem','GITHUB_EVENT_NAME':'workflow_dispatch',
                     'SEM_PUBLIC_ORIGIN':TEST_ORIGIN,'GITHUB_STEP_SUMMARY':str(Path(folder)/'summary.md')}
                output=io.StringIO()
                with patch.dict(os.environ,env,clear=True), patch('observe.eligible',return_value=C), patch('observe.observe',return_value={'state':'runtime-match','samples':3,'reason':'FRESH_BUILD_AND_READY'}), contextlib.redirect_stdout(output):
                    self.assertEqual(observe.main(),0)
                for text in [output.getvalue(),Path(env['GITHUB_STEP_SUMMARY']).read_text(),Path('verification/runtime-observation.json').read_text()]:
                    self.assertNotIn(TEST_ORIGIN,text)
                    self.assertNotIn('runtime.example.invalid',text)
            finally:os.chdir(before)
    def test_mock_boundary_and_footer_are_explicit(self):
        root=Path(__file__).resolve().parents[2]
        source=(root/'src/Sem.Web/Program.cs').read_text()
        self.assertIn('sem-runtime-footer',source)
        self.assertIn('UIデモ版',source)
        self.assertIn('mock-ui-ready',source)
        # PR128の「全モードでDB参照なし」は分離段階の制約。現在は既定demoと明示実認証を分ける。
        self.assertIn('product is null ? null : await ProductStorage.OpenAsync(product)',source)
        self.assertIn('if (product is not null)',source)
        self.assertIn('app.MapSemNativeAuthentication();',source)
        self.assertNotIn('SqliteStorageInitializer.InitializeAsync',source)
        settings=(root/'src/Sem.Hosting/ProductSettings.cs').read_text()
        self.assertIn('mode is null or "demo"',settings)
        self.assertIn('SEM_PRODUCT_MIXED_CONFIGURATION',settings)
        self.assertNotIn('SEM_APPLICATION_MODE=authenticated',(root/'deploy/Dockerfile').read_text())
    def test_workflows_keep_publication_independent_and_embed_build(self):
        root=Path(__file__).resolve().parents[2]
        publish=(root/'.github/workflows/cd-image.yml').read_text()
        observer=(root/'.github/workflows/cd-observe.yml').read_text()
        self.assertNotIn('observe.py',publish)
        self.assertIn('workflows: [CD verified image]',observer)
        self.assertIn('SEM_PUBLIC_ORIGIN: ${{ secrets.SEM_PUBLIC_ORIGIN }}',observer)
        self.assertIn('timeout-minutes: 20',observer)
        for name in ['SEM_COMMIT','SEM_PUBLICATION_RUN','SEM_PUBLICATION_ATTEMPT']:self.assertIn('--build-arg '+name+'=',publish)
        self.assertIn('check_image_build.py sem-cd:tested',publish)
        self.assertIn('docker_updater_checks.py',publish)
        self.assertIn('observe_checks.py',publish)
        self.assertIn('observe_checks.py',(root/'.github/workflows/main-integration.yml').read_text())

if __name__=='__main__':unittest.main()
