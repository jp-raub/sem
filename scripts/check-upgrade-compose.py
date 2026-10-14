"""Validate merged candidate configuration with synthetic, nonsecret settings."""
import copy,json,os,subprocess

env=os.environ.copy()
env.update(SEM_TRUSTED_PROXY_IP='172.18.0.5', SEM_BOOTSTRAP_SECRET='',
    SEM_CANDIDATE_IMAGE='sem-dxp:proof-synthetic', SEM_CANDIDATE_VOLUME='sem-proof-synthetic',
    SMTP_USERNAME='synthetic-id',SMTP_PASSWORD='synthetic-password',SMTP_FROM='SEM <test@example.test>',
    PUSH_PUBLIC_KEY='synthetic-public',PUSH_PRIVATE_KEY='synthetic-private')
def load(override=False):
    args=['docker','compose','-f','deploy/compose.yaml']
    if override:args+=['-f','deploy/upgrade-candidate.override.yaml']
    return json.loads(subprocess.check_output(args+['config','--format','json'],env=env))
base=load();candidate=load(True)
expected=copy.deepcopy(base)
expected['services']['sem']['image']=env['SEM_CANDIDATE_IMAGE']
expected['volumes']['sem-data']={'name':env['SEM_CANDIDATE_VOLUME'],'external':True}
assert candidate==expected, 'Override changed more than image and data-volume binding'
assert not candidate['services']['sem'].get('ports')
assert candidate['services']['sem']['environment']['Smtp__From']==env['SMTP_FROM']
assert not candidate['services']['sem']['environment']['Sem__BootstrapSecret']
for key in ['SEM_CANDIDATE_IMAGE','SEM_CANDIDATE_VOLUME']:
    missing=env.copy();missing.pop(key)
    args=['docker','compose','-f','deploy/compose.yaml','-f','deploy/upgrade-candidate.override.yaml','config','--format','json']
    result=subprocess.run(args,env=missing,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    assert result.returncode!=0,'Missing candidate setting did not fail closed'
print('Candidate Compose: only image/volume changed; SMTP/Push/network/security preserved; missing inputs rejected')
