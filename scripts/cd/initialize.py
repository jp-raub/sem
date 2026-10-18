#!/usr/bin/env python3
"""Initial local capture. Do not fetch/execute remote configuration or print secrets."""
import argparse
import json
import os
from pathlib import Path
import re

from updater import CDException, Docker, atomic_json, run, secure_path


def escape_dollars(value):
    # Compose interpolates JSON too. Freeze literal secrets containing '$'.
    if isinstance(value, str):
        return value.replace('$', '$$')
    if isinstance(value, list):
        return [escape_dollars(v) for v in value]
    if isinstance(value, dict):
        return {k: escape_dollars(v) for k, v in value.items()}
    return value


def prepare(compose_files, env_file, project, backup_root, config_root=Path('/etc/sem-cd'),
            state_root=Path('/var/lib/sem-cd')):
    if not re.fullmatch('[a-z0-9][a-z0-9_-]{0,62}', project):
        raise CDException('Invalid Compose project')
    if (config_root/'config.json').exists():
        raise CDException('Already configured; do not overwrite existing state')
    for file in compose_files + ([env_file] if env_file else []):
        if not Path(file).is_absolute() or not Path(file).is_file() or Path(file).is_symlink():
            raise CDException('Initial Compose and env inputs must be absolute regular files')
    command=['docker','compose','--project-name',project]
    for file in compose_files:command+=['-f',file]
    if env_file:command+=['--env-file',env_file]
    ids=run(command+['ps','-a','-q','sem']).decode().split()
    if len(ids)!=1 or not re.fullmatch('[a-f0-9]{12,64}',ids[0]):
        raise CDException('Expected existing single SEM service')
    current=json.loads(run(['docker','inspect',ids[0]]))[0]
    if not current['State']['Running']:
        raise CDException('SEM must be running during initial capture')
    labels=current['Config'].get('Labels') or {}
    if labels.get('com.docker.compose.project')!=project or labels.get('com.docker.compose.service')!='sem':
        raise CDException('Existing service identity mismatch')
    effective=json.loads(run(command+['config','--format','json']))
    service=effective['services']['sem']
    image=json.loads(run(['docker','image','inspect',current['Image']]))[0]
    env=dict(v.split('=',1) for v in image['Config'].get('Env',[]))
    for key,value in service.get('environment',{}).items():
        if value is None:env.pop(key,None)
        else:env[key]=str(value)
    actual=dict(v.split('=',1) for v in current['Config'].get('Env',[]))
    if env!=actual:
        raise CDException('Resolved environment differs from running SEM; include all original Compose/env inputs')
    mounts=[m for m in current['Mounts'] if m['Destination']=='/data']
    if len(mounts)!=1 or mounts[0]['Type'] not in {'bind','volume'} or not mounts[0].get('RW'):
        raise CDException('Expected one writable persistent /data mount')
    mount=mounts[0]
    data_source=mount.get('Name') if mount['Type']=='volume' else mount['Source']
    for path in (config_root,state_root,backup_root):
        Path(path).mkdir(parents=True,exist_ok=True,mode=0o700)
        secure_path(path,directory=True)
    if Path(backup_root).resolve().is_relative_to(Path(mount['Source']).resolve()):
        raise CDException('Backup cannot be inside data mount')
    project_dir=labels.get('com.docker.compose.project.working_dir') or str(Path(compose_files[0]).parent)
    baseline=config_root/'deployment.private.json'
    atomic_json(baseline,escape_dollars(effective))
    c=dict(project=project,service='sem',project_directory=project_dir,
           compose_files=[str(baseline)],env_file=None,data_type=mount['Type'],data_source=data_source,
           backup_root=str(backup_root),state_directory=str(state_root))
    engine=Docker(c,state_root)
    # Re-read our frozen JSON through Compose to prove identical sensitive env and mount.
    frozen=json.loads(engine.compose('config','--format','json'))
    if frozen['services']['sem'].get('environment',{})!=service.get('environment',{}):
        baseline.unlink()
        raise CDException('Frozen Compose environment changed during interpolation')
    engine.check_container(current)
    # Validate final composed volume identity, never create another named volume.
    engine.preflight(current,image,backup_root)
    atomic_json(state_root/'status.json',dict(paused=True,blocked=False,phase='configured',
        current_commit=None,current_image=None,message='Configured; paused until local inspection and resume'))
    atomic_json(config_root/'config.json',c)
    print('Captured existing SEM privately. No container changed. Auto-update remains paused.')


def main():
    os.umask(0o077)
    parser=argparse.ArgumentParser()
    parser.add_argument('--compose',action='append',required=True)
    parser.add_argument('--env-file')
    parser.add_argument('--project',required=True)
    parser.add_argument('--backup-root',type=Path,required=True)
    args=parser.parse_args()
    if os.geteuid()!=0:raise CDException('Root is required for installation')
    if not args.backup_root.is_absolute():raise CDException('Backup path must be absolute')
    prepare(args.compose,args.env_file,args.project,args.backup_root)


if __name__=='__main__':
    try:main()
    except CDException as error:
        raise SystemExit(str(error))
