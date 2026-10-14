"""Package the exact tested checkout; never mutate source, merge or deploy."""
import gzip, hashlib, json, os, pathlib, shutil, subprocess, sys

def out(*args):
    return subprocess.check_output(args, text=True).strip()

root = pathlib.Path(__file__).resolve().parents[1]
os.chdir(root)
commit = out('git', 'rev-parse', 'HEAD')
expected = os.environ['GITHUB_SHA']
if commit != expected:
    raise SystemExit('Checkout differs from requested CI source')
subprocess.run(['git', 'diff', '--exit-code', '--', 'src', 'deploy', 'scripts', 'tests', '.github'], check=True)
if '.env' in out('git', 'ls-files').splitlines() or any(p.endswith('/.env') for p in out('git','ls-files').splitlines()):
    raise SystemExit('Private .env must not be tracked')
for name, marker in [('storage-checks.log', 'Storage checks passed: 18'), ('passkey-proof-checks.log', 'Passkey proof checks passed: 17')]:
    if marker not in (root / 'verification' / name).read_text():
        raise SystemExit('Required safety checks not found: ' + name)
report = json.loads((root / 'verification/e2e-result.json').read_text())
if report['passed'] != 45 or report.get('errors'):
    raise SystemExit('Expected all 45 upgrade/auth checks and no client errors')
image = 'sem-dxp:proof-' + commit[:12]
subprocess.run(['docker', 'tag', 'sem-dxp:migration-ci', image], check=True)
info = json.loads(out('docker', 'image', 'inspect', image))[0]
if (info['Os'], info['Architecture']) != ('linux', 'amd64'):
    raise SystemExit('Unexpected image platform')
if info['Config'].get('Labels', {}).get('org.opencontainers.image.revision') != commit:
    raise SystemExit('Image revision does not match tested checkout')
target = root / 'delivery'
target.mkdir(exist_ok=False)
subprocess.run(['git', 'archive', '--format=zip', '-o', str(target / 'sem-dxp-source.zip'), commit], check=True)
with open(target / 'sem-dxp-image.tar.gz', 'wb') as f:
    with gzip.GzipFile(filename='', mode='wb', fileobj=f, compresslevel=1, mtime=0) as gz:
        p = subprocess.Popen(['docker', 'save', image], stdout=subprocess.PIPE)
        try:
            shutil.copyfileobj(p.stdout, gz)
        finally:
            p.stdout.close()
        if p.wait():
            raise SystemExit('Image export failed; do not distribute')
for src, dst in [('deploy/UPGRADE.md', 'README.md'), ('deploy/UPGRADE.ja.md', 'README.ja.md')]:
    shutil.copyfile(src, target / dst)
shutil.copyfile(root / 'verification/e2e-result.json', target / 'verification.json')
manifest = {'sourceCommit':commit, 'image':image, 'imageId':info['Id'], 'platform':'linux/amd64',
    'ciRun':f"https://github.com/{os.environ['GITHUB_REPOSITORY']}/actions/runs/{os.environ['GITHUB_RUN_ID']}",
    'checks':{'storage':18, 'passkeyProof':17, 'upgradeHttps':report['passed']},
    'notDeployed':True, 'rollbackRequiresPreUpgradeVolume':True}
(target / 'VERSION.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2)+'\n')
(target / 'VERSION.txt').write_text(f"Verified source: {commit}\nImage: {image}\nImage ID: {info['Id']}\nPlatform: linux/amd64\nCI run: {manifest['ciRun']}\nNot deployed; retain the original volume and .env.\n")
with open(target / 'SHA256SUMS', 'w') as f:
    for path in sorted(target.iterdir()):
        if path.name != 'SHA256SUMS':
            with open(path, 'rb') as source:
                digest = hashlib.file_digest(source, 'sha256').hexdigest()
            f.write(f'{digest}  {path.name}\n')
# Reload the export and prove that it reconstructs the identical immutable image.
subprocess.run(['docker','image','rm',image],check=True)
with gzip.open(target / 'sem-dxp-image.tar.gz','rb') as source:
    p = subprocess.Popen(['docker','load'],stdin=subprocess.PIPE,stdout=subprocess.DEVNULL)
    try:
        shutil.copyfileobj(source,p.stdin)
    finally:
        p.stdin.close()
    if p.wait():
        raise SystemExit('Image reload failed; do not distribute')
if out('docker','image','inspect',image,'--format','{{.Id}}') != info['Id']:
    raise SystemExit('Exported image identity differs; do not distribute')
subprocess.run(['sha256sum','-c','SHA256SUMS'],cwd=target,check=True)
print('Packaged exact tested source/image:',commit,image)
