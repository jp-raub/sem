async function boundedJson(response, limit) {
    if (!(response.headers.get('Content-Type') || '').toLowerCase().startsWith('application/json')) throw new Error('JSON応答を確認できません。');
    const length=response.headers.get('Content-Length');
    if(length!==null && (!/^\d+$/.test(length)||Number(length)>limit)) throw new Error('応答サイズが大きすぎます。');
    if(!response.body) throw new Error('応答本文を確認できません。');
    const reader=response.body.getReader(), chunks=[];let size=0;
    try { for(;;){const {done,value}=await reader.read();if(done)break;size+=value.byteLength;if(size>limit)throw new Error('応答サイズが大きすぎます。');chunks.push(value);} }
    catch(error){await reader.cancel().catch(()=>{});throw error;}
    const data=new Uint8Array(size);let offset=0;for(const chunk of chunks){data.set(chunk,offset);offset+=chunk.byteLength;}
    return JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(data));
}
let generation = 0, activeController = null, expiryTimer = null, bound = false;
function clearProfile() { const r=document.getElementById("profile-qr-share-result"); if(r){r.replaceChildren();r.hidden=true;} clearTimeout(expiryTimer); }
function element(tag, text = null) {
    const node = document.createElement(tag);
    if (text !== null) node.textContent = text;
    return node;
}
function showError(text) {
    document.getElementById('profile-qr-share-error').textContent = text;
    document.getElementById('profile-qr-share-status').textContent = '';
}
function safeSocial(url) {
    try {
        const parsed = new URL(url);
        return (parsed.protocol === 'https:' || parsed.protocol === 'http:') && !parsed.username && !parsed.password ? parsed : null;
    } catch { return null; }
}
async function csrf(signal) {
    const response = await fetch('/api/csrf', {
        method: 'GET', credentials: 'same-origin', mode: 'same-origin', cache: 'no-store',
        redirect: 'error', signal, headers: { Accept: 'application/json' }
    });
    if (!response.ok) throw new Error();
    const data = await boundedJson(response,64*1024);
    if (typeof data.token !== 'string' || data.token.length > 8192) throw new Error();
    return data.token;
}
function render(profile) {
    const root = document.getElementById('profile-qr-share-result');
    root.replaceChildren();
    const heading = element('h2', `${profile.icon || ''}${profile.icon ? ' ' : ''}${profile.displayName}`);
    root.append(heading);
    const details = element('dl');
    for (const [title, value] of [['読み', profile.reading], ['対面での呼び', profile.callName]]) {
        details.append(element('dt', title), element('dd', value || '未設定'));
    }
    root.append(details);
    const answers = (profile.answers || []).filter(answer => answer.value !== null && answer.value !== '');
    if (answers.length) {
        root.append(element('h3', 'プロフィール設問'));
        const list = element('dl');
        for (const answer of answers) {
            list.append(element('dt', answer.text),
                element('dd', answer.type === 'YesNo' ? (answer.value === 'YES' ? 'はい' : 'いいえ') : answer.value));
        }
        root.append(list);
    }
    root.append(element('h3', 'SNS等'));
    const links = (profile.social || []).map(social => ({ ...social, safe: safeSocial(social.url) })).filter(x => x.safe);
    if (!links.length) root.append(element('p', 'リンクは未設定です。'));
    for (const link of links) {
        const p = element('p');
        const a = element('a', link.service);
        a.href = link.safe.href; a.target = '_blank'; a.rel = 'noopener noreferrer';
        p.append(a); root.append(p);
    }
    root.append(element('p', 'メールアドレス、イベント登録履歴、招待、認証情報は公開していません。'));
    root.hidden = false;
}
export async function init() {
    if (!bound) { bound=true; window.addEventListener('hashchange', () => { if (location.pathname === '/profile/share') init(); }); }
    const myGeneration=++generation;
    activeController?.abort(); activeController=new AbortController();
    const controller=activeController; const signal=controller.signal;
    clearProfile();
    const alert=document.getElementById('profile-qr-share-error'); if(alert)alert.textContent='';
    const timeout=setTimeout(()=>controller.abort(),25000);
    try {
        const match = /^#token=([0-9a-fA-F]{64})$/.exec(location.hash);
        // Remove the bearer from the address bar before any network request. URL fragments
        // are not sent to the server, and this page loads no third-party assets.
        history.replaceState(null, '', location.pathname);
        if (!match) throw new Error();
        const token = match[1];
        const csrfToken = await csrf(signal);
        const response = await fetch('/api/core/profile-qr-view', {
            method: 'POST', credentials: 'same-origin', mode: 'same-origin', cache: 'no-store',
            redirect: 'error', signal,
            headers: { Accept: 'application/json', 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
            body: JSON.stringify({ token })
        });
        if (!response.ok || response.redirected) throw new Error();
        const expiresAt=Date.parse(response.headers.get('X-Profile-QR-Expires-At'));
        if (!Number.isFinite(expiresAt) || expiresAt <= Date.now()) throw new Error();
        const profile = await boundedJson(response,2*1024*1024);
        if (myGeneration!==generation || location.pathname!=='/profile/share') return;
        if (!profile || typeof profile.userId !== 'string' || typeof profile.displayName !== 'string' ||
            !Array.isArray(profile.answers) || !Array.isArray(profile.social)) throw new Error();
        render(profile);
        expiryTimer=setTimeout(()=>{ if(myGeneration===generation){clearProfile();showError('このプロフィールQRは期限切れです。新しいQRを依頼してください。');} }, Math.max(0,expiresAt-Date.now()));
        document.getElementById('profile-qr-share-status').textContent = '';
    } catch {
        if(myGeneration!==generation || location.pathname!=='/profile/share') return;
        clearProfile();
        showError('このプロフィールQRは無効か期限切れです。本人に新しいQRを発行してもらってください。');
    } finally { clearTimeout(timeout); }
}