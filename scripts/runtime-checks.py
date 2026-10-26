import json, urllib.request, urllib.error, sys
base=sys.argv[1]; expected=sys.argv[2:]
def get(path):
 with urllib.request.urlopen(base+path) as r:
  assert r.status==200 and 'no-store' in r.headers['Cache-Control'];return json.load(r)
p='1'*32;v=get('/api/version?probe='+p)
assert set(v)=={'schema','application','channel','commit','buildId','publicationRun','publicationAttempt','probe'}
assert v['probe']==p and v['application']=='SEM' and v['schema']==1
if expected:
 sha,run,attempt=expected
 assert v==dict(schema=1,application='SEM',channel='published',commit=sha,buildId=f'run-{run}-{attempt}',publicationRun=run,publicationAttempt=attempt,probe=p)
else: assert v['channel']=='development' and v['commit'] is None and v['buildId'] is None
assert get('/health/ready?probe='+p)==dict(status='mock-ui-ready',probe=p)
for query in ['probe=x','probe='+p+'&probe='+p,'host=private']:
 try:urllib.request.urlopen(base+'/api/version?'+query);raise AssertionError(query)
 except urllib.error.HTTPError as e:assert e.code==400
for path in ['/','/about']:
 with urllib.request.urlopen(base+path) as r:
  assert 'no-store' in r.headers['Cache-Control'];s=r.read().decode();assert '/about' in s if path=='/' else 'UIデモ版' in s
print('版・準備状態・nonce・不正入力・開発版／公開版・人向け説明：成功')
