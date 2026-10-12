// Uses only synthetic users, isolated SMTP and Chromium virtual authenticators.
module.exports = async function({p,g,cdp,adminAuth,ok}) {
 const api=(page,action,body={})=>page.evaluate(async({action,body})=>{
  const {token}=await(await fetch('/api/csrf')).json();
  const r=await fetch('/api/'+action,{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:JSON.stringify(body)});
  return {status:r.status,body:await r.json()};
 },{action,body});
 const denied=r=>r.status===400||r.status===403;
 const mails=async()=>await(await fetch('http://127.0.0.1:18025/')).json();
 const proofMails=async()=> (await mails()).filter(m=>m.to.includes('admin@example.test')&&m.subject==='SEM パスキー追加のメール確認');
 const waitProof=async(count)=>{for(let n=0;n<40;n++){const found=await proofMails();if(found.length>count)return found.at(-1);await new Promise(r=>setTimeout(r,500))}throw Error('Addition proof email not delivered')};
 const tokenOf=m=>m.body.match(/passkeyAddToken=([A-F0-9]+)/)[1];
 const oldKeys=(await cdp.send('WebAuthn.getCredentials',{authenticatorId:adminAuth})).credentials;
 ok(denied(await api(p,'passkey-options')),'Existing EmailConfirmed cannot bypass fresh addition proof');
 ok(denied(await api(p,'passkey-complete',{credential:'{}'})),'Direct completion cannot bypass addition proof');
 const initialCount=(await proofMails()).length;
 await p.locator('#add-passkey').click();await p.waitForSelector('#passkey-add-panel',{timeout:20000});
 const firstMail=await waitProof(initialCount),firstToken=tokenOf(firstMail);
 const flow=await p.evaluate(async()=> (await(await fetch('/api/status')).json()).passkeyAddition);
 ok(!!flow&&!flow.emailVerified,'Native existing passkey assertion starts a session-bound operation');
 ok(denied(await api(p,'passkey-options')),'Creation options denied until fresh saved-email proof');
 ok((await p.request.get('https://localhost:18443/verify?token='+firstToken)).status()===400,'Addition email token cannot be used for initial enrollment');
 ok(denied(await api(g,'passkey-add-confirm',{token:firstToken})),'Another authenticated user cannot consume addition proof');
 await p.locator('#resend-add-passkey').click();
 const secondMail=await waitProof(initialCount+1),secondToken=tokenOf(secondMail);
 const resent=await p.evaluate(async()=> (await(await fetch('/api/status')).json()).passkeyAddition);
 ok(firstToken!==secondToken&&resent.expires===flow.expires,'Resend replaces token without extending operation deadline');
 ok(denied(await api(p,'passkey-add-confirm',{token:firstToken})),'Resent old email token is invalid');
 const confirms=await Promise.all([api(p,'passkey-add-confirm',{token:secondToken}),api(p,'passkey-add-confirm',{token:secondToken})]);
 ok(confirms.filter(r=>r.status===200).length===1&&confirms.filter(denied).length===1,'Concurrent email proof is consumed exactly once');
 await p.locator('#sem-refresh').click();await p.waitForSelector('#complete-add-passkey');
 const verified=await p.evaluate(async()=> (await(await fetch('/api/status')).json()).passkeyAddition);
 ok(verified.emailVerified&&verified.registrationUntil<=verified.expires&&verified.registrationUntil<=Math.floor(Date.now()/1000)+300,'Registration grant is capped at five minutes and whole-operation deadline');
 const opts=await api(p,'passkey-options');ok(opts.status===200,'Fresh reauthentication and email proof unlock native creation options');
 // A second device must not already contain an excluded credential. Replace only the synthetic authenticator.
 await cdp.send('WebAuthn.removeVirtualAuthenticator',{authenticatorId:adminAuth});
 const {authenticatorId:newAuth}=await cdp.send('WebAuthn.addVirtualAuthenticator',{options:{protocol:'ctap2',transport:'internal',hasResidentKey:true,hasUserVerification:true,isUserVerified:true,automaticPresenceSimulation:true}});
 const credential=await p.evaluate(async(o)=>{
  const decode=s=>Uint8Array.from(atob(s.replace(/-/g,'+').replace(/_/g,'/')),c=>c.charCodeAt(0));
  o.challenge=decode(o.challenge);o.user.id=decode(o.user.id);
  for(const c of o.excludeCredentials||[])c.id=decode(c.id);
  const c=await navigator.credentials.create({publicKey:o});return JSON.stringify(c.toJSON());
 },opts.body);
 const completions=await Promise.all([api(p,'passkey-complete',{credential}),api(p,'passkey-complete',{credential})]);
 ok(completions.filter(r=>r.status===200).length===1&&completions.filter(denied).length===1,'Concurrent native attestation completes addition exactly once');
 ok(denied(await api(p,'passkey-options'))&&denied(await api(p,'passkey-complete',{credential})),'Completed operation and attestation cannot be replayed');
 const newKeys=(await cdp.send('WebAuthn.getCredentials',{authenticatorId:newAuth})).credentials;
 ok(newKeys.length===1&&!oldKeys.some(k=>k.credentialId===newKeys[0].credentialId),'A separate virtual device stores the newly added native passkey');
 await p.locator('#sem-refresh').click();await p.waitForFunction(()=>document.getElementById('sem-root').textContent.includes('パスキーを追加しました'));
 ok((await p.locator('#sem-root').innerText()).includes('パスキーを追加しました'),'Addition completion notification reaches inbox');
 let completionMail=false;for(let n=0;n<40;n++){if((await mails()).some(m=>m.to.includes('admin@example.test')&&m.subject==='SEM パスキー追加完了')){completionMail=true;break}await new Promise(r=>setTimeout(r,500))}
 ok(completionMail,'Addition completion email reaches isolated SMTP');
 const beforeCancel=(await proofMails()).length;
 await p.locator('#add-passkey').click();await p.waitForSelector('#passkey-add-panel');
 const cancelledToken=tokenOf(await waitProof(beforeCancel));
 await p.goto('https://localhost:18443/?passkeyAddToken='+cancelledToken);await p.waitForSelector('#complete-add-passkey');
 ok(!p.url().includes('passkeyAddToken')&&(await p.evaluate(async()=> (await(await fetch('/api/status')).json()).passkeyAddition)).emailVerified,'Same-browser email link confirms via CSRF POST and removes the secret from the visible URL');
 await p.locator('#cancel-add-passkey').click();await p.waitForSelector('#passkey-add-panel',{state:'detached'});
 ok(denied(await api(p,'passkey-add-confirm',{token:cancelledToken}))&&denied(await api(p,'passkey-options')),'Cancellation invalidates pending proof and registration');
 // The original virtual device was removed from Chromium only; its server record is retained.
 await p.locator('[data-action=logout]').click();await p.waitForSelector('#login');
 await p.locator('#login [name=email]').fill('admin@example.test');await p.locator('#login button').click();await p.waitForSelector('#issue',{timeout:20000});
 ok(await p.locator('#issue').isVisible(),'Newly added passkey authenticates after logout without the original virtual key');
 const beforeLogout=(await proofMails()).length;
 await p.locator('#add-passkey').click();await p.waitForSelector('#passkey-add-panel');
 const logoutToken=tokenOf(await waitProof(beforeLogout));
 await p.locator('[data-action=logout]').click();await p.waitForSelector('#login');
 await p.locator('#login [name=email]').fill('admin@example.test');await p.locator('#login button').click();await p.waitForSelector('#issue');
 ok(denied(await api(p,'passkey-add-confirm',{token:logoutToken}))&&denied(await api(p,'passkey-options')),'Logout invalidates the previous unfinished operation even after a new login');
};
