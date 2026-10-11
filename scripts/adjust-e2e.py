from pathlib import Path
p=Path('scripts/e2e.cjs');s=p.read_text()
s=s.replace("checks.push(t)};", "checks.push(t);fs.mkdirSync('verification',{recursive:true});fs.writeFileSync('verification/e2e-result.json',JSON.stringify({passed:checks.length,checks},null,2))};")
s=s.replace("await g.locator('#request button').click();await p.locator('#sem-refresh').click();", "await g.locator('#request button').click();await g.waitForFunction(()=>document.getElementById('sem-message').textContent.includes('発行をリクエストしました'));await p.locator('#sem-refresh').click();")
s=s.replace("await p.locator('[data-action=decide-invitation][data-approve=false]').click();await g.locator('#sem-refresh').click();", "await p.locator('[data-action=decide-invitation][data-approve=false]').click();await p.waitForFunction(()=>document.getElementById('sem-message').textContent.includes('拒否しました'));await g.locator('#sem-refresh').click();")
p.write_text(s)
