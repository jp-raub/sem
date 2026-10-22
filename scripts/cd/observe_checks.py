#!/usr/bin/env python3
import copy
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch
from email.message import Message
import urllib.error
import observe

C = dict(deployment_mode='auto', schema=1, repository='jp-raub/sem', commit='a'*40,
    image='ghcr.io/jp-raub/sem@sha256:'+'b'*64, platform='linux/amd64',
    integration_run=1, integration_attempt=1, publication_run=2, publication_attempt=3)

class Checks(unittest.TestCase):
    def good(self, path, nonce):
        return (dict(observe.expected_identity(C), probe=nonce) if path == '/api/version'
                else dict(status='storage-ready', probe=nonce))
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
        for ready in ({'status':'storage-ready','probe':'old'}, {'status':'failed','probe':None}, {}):
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
        url=observe.ORIGIN+'/api/version?probe='+probe
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
            return observe.fresh_json('/api/version',probe)
    def test_fresh_https_json(self):self.assertEqual(self.http()['commit'],'a'*40)
    def test_http_error_html_cache_redirect_and_large_rejected(self):
        for opts in [dict(status=503),dict(status=302),dict(content_type='text/html'),dict(cache='public'),
                     dict(final='https://evil.invalid'),dict(body=b'x'*8193),dict(body=b'<html>OK</html>'),dict(body=b'{"x":1,"x":2}')]:
            with self.assertRaises(observe.ObservationError):self.http(**opts)
    def test_tls_network_and_redirect_errors_rejected(self):
        for error in [urllib.error.URLError('synthetic TLS error'),observe.CDException('Unexpected HTTP redirect')]:
            with patch('observe.urllib.request.build_opener') as make:
                make.return_value.open.side_effect=error
                with self.assertRaises(observe.ObservationError):observe.fresh_json('/api/version','a'*32)
    def test_no_arbitrary_url_or_probe(self):
        for path,probe in [('https://evil.invalid','a'*32),('/api/version','bad')]:
            with self.assertRaises(observe.ObservationError):observe.fresh_json(path,probe)
    def test_existing_error_ui_styles_are_preserved(self):
        root=Path(__file__).resolve().parents[2]
        styles=(root/'src/Sem.Web/Components/Layout/MainLayout.razor.css').read_text()
        self.assertIn('#blazor-error-ui',styles)
        self.assertIn('display: none;',styles)
        self.assertIn('.runtime-version-footer',styles)
    def test_workflows_keep_publication_independent_and_embed_build(self):
        root=Path(__file__).resolve().parents[2]
        publish=(root/'.github/workflows/cd-image.yml').read_text()
        observer=(root/'.github/workflows/cd-observe.yml').read_text()
        self.assertNotIn('observe.py',publish)
        self.assertIn('workflows: [CD verified image]',observer)
        self.assertIn('timeout-minutes: 20',observer)
        for name in ['SEM_COMMIT','SEM_PUBLICATION_RUN','SEM_PUBLICATION_ATTEMPT']:self.assertIn('--build-arg '+name+'=',publish)
        self.assertIn('check_image_build.py sem-cd:tested',publish)
        self.assertIn('docker_updater_checks.py',publish)
        self.assertIn('observe_checks.py',publish)
        self.assertIn('observe_checks.py',(root/'scripts/integration-checks.sh').read_text())

if __name__=='__main__':unittest.main()
