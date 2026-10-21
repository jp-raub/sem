#!/usr/bin/env python3
"""Summarize public Actions run/jobs JSON; never fetch logs or credentials."""
import argparse
from datetime import datetime
import json
from pathlib import Path


def instant(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00'))


def summarize(run, data):
    jobs = data['jobs']
    if data['total_count'] != len(jobs):
        raise ValueError('Truncated jobs; fetch every page before evaluation')
    if run['status'] != 'completed' or not jobs:
        raise ValueError('Completed run with jobs required')
    if any(j.get('run_id') != run['id'] or j['status'] != 'completed' for j in jobs):
        raise ValueError('Mixed run or incomplete jobs')
    created = instant(run['created_at'])
    starts = [instant(j['started_at']) for j in jobs]
    ends = [instant(j['completed_at']) for j in jobs]
    if min(starts) < created or any(end < start for start, end in zip(starts, ends)):
        raise ValueError('Invalid timing order')
    duration = lambda end, start: round((end-start).total_seconds(), 3)
    # A failed completed job is also useful feedback; do not label it a product defect.
    return {
        'schema': 1, 'run_id': run['id'], 'head_sha': run['head_sha'],
        'attempt': run['run_attempt'], 'conclusion': run['conclusion'],
        'queue_to_first_job_seconds': duration(min(starts), created),
        'critical_path_seconds': duration(max(ends), min(starts)),
        'created_to_last_result_seconds': duration(max(ends), created),
        'first_completed_job_feedback_seconds': duration(min(ends), created),
        'summed_job_seconds': round(sum((b-a).total_seconds() for a,b in zip(starts,ends)),3),
        'rerun_observed': run['run_attempt'] > 1,
        'jobs': [{'name': j['name'], 'conclusion': j['conclusion'],
                  'seconds': duration(end,start)} for j,start,end in zip(jobs,starts,ends)],
        'quality': {
            'flaky_failures': 'not_assessed', 'ci_detected_defects': 'not_assessed',
            'user_acceptance_regressions': 'not_assessed',
            'data_retention_result': 'not_assessed',
            'manual_confirmation_burden': 'not_assessed'
        },
        'interpretation': 'Single run; first completed job is a feedback proxy, not a measured assertion timestamp. No median or speedup inferred.'
    }


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run', required=True, type=Path)
    parser.add_argument('--jobs', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    result = summarize(json.loads(args.run.read_text()), json.loads(args.jobs.read_text()))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2)+'\n')
