from pathlib import Path
import hashlib,json,subprocess
root=Path('src/Sem.Web/wwwroot');manifest=json.loads(Path('src/Sem.Web/mock-manifest.json').read_text())
actual={p.relative_to(root).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in root.rglob('*') if p.is_file()}
assert actual==manifest
for p in root.glob('*.js'):subprocess.run(['node','--check',str(p)],check=True)
print(f'固定モック{len(actual)}資産の完全一致と全JS構文：成功')
