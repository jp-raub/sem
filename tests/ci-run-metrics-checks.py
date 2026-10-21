#!/usr/bin/env python3
import copy
import importlib.util
from pathlib import Path
import unittest
spec=importlib.util.spec_from_file_location('metrics',Path(__file__).resolve().parents[1]/'scripts/ci-run-metrics.py')
metrics=importlib.util.module_from_spec(spec);spec.loader.exec_module(metrics)

class Checks(unittest.TestCase):
    def setUp(self):
        self.run={'id':123,'head_sha':'a'*40,'run_attempt':1,'conclusion':'success','status':'completed','created_at':'2026-10-21T00:00:00Z'}
        self.jobs={'total_count':2,'jobs':[{'run_id':123,'name':'integration-checks','status':'completed','conclusion':'success','started_at':'2026-10-21T00:00:03Z','completed_at':'2026-10-21T00:10:03Z'},{'run_id':123,'name':'acceptance-browser','status':'completed','conclusion':'success','started_at':'2026-10-21T00:00:04Z','completed_at':'2026-10-21T00:20:04Z'}]}
    def test_parallel_not_summed_critical_path(self):
        x=metrics.summarize(self.run,self.jobs)
        self.assertEqual(x['critical_path_seconds'],1201)
        self.assertEqual(x['summed_job_seconds'],1800)
        self.assertEqual(x['queue_to_first_job_seconds'],3)
        self.assertEqual(x['first_completed_job_feedback_seconds'],603)
    def test_unknown_quality_is_not_zero(self):
        self.assertTrue(all(x=='not_assessed' for x in metrics.summarize(self.run,self.jobs)['quality'].values()))
    def test_rerun(self):
        self.run['run_attempt']=2;self.assertTrue(metrics.summarize(self.run,self.jobs)['rerun_observed'])
    def test_failure_is_feedback_not_inferred_defect(self):
        self.jobs['jobs'][0]['conclusion']='failure';self.run['conclusion']='failure'
        x=metrics.summarize(self.run,self.jobs);self.assertEqual(x['conclusion'],'failure');self.assertEqual(x['quality']['ci_detected_defects'],'not_assessed')
    def test_truncation(self):
        self.jobs['total_count']=3
        with self.assertRaises(ValueError):metrics.summarize(self.run,self.jobs)
    def test_mixed_run(self):
        self.jobs['jobs'][0]['run_id']=124
        with self.assertRaises(ValueError):metrics.summarize(self.run,self.jobs)
    def test_pending(self):
        self.run['status']='in_progress'
        with self.assertRaises(ValueError):metrics.summarize(self.run,self.jobs)
    def test_bad_time(self):
        self.jobs['jobs'][0]['completed_at']='2026-10-20T00:00:00Z'
        with self.assertRaises(ValueError):metrics.summarize(self.run,self.jobs)

if __name__=='__main__':unittest.main()
