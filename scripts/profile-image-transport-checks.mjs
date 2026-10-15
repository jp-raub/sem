import fs from 'node:fs/promises';

const editorSource=await fs.readFile(new URL('../src/Sem.Web/wwwroot/profile-editor.js',import.meta.url),'utf8');
const shareSource=await fs.readFile(new URL('../src/Sem.Web/wwwroot/profile-qr-share.js',import.meta.url),'utf8');
const settingsMarkup=await fs.readFile(new URL('../src/Sem.Web/Components/Pages/ProfileSettings.razor',import.meta.url),'utf8');
const memberMarkup=await fs.readFile(new URL('../src/Sem.Web/Components/Pages/MemberProfile.razor',import.meta.url),'utf8');
const profileTransport=await fs.readFile(new URL('../src/Sem.Web/wwwroot/profile-history.js',import.meta.url),'utf8');
let passed=0;
const check=(value,label)=>{if(!value)throw new Error('FAIL '+label);passed++;console.log('PASS '+label);};

class FakeElement {
    constructor(tag='div') {
        this.tagName=tag.toUpperCase();this.children=[];this.listeners=new Map();this.style={};this.dataset={};
        this.hidden=false;this.disabled=false;this.isConnected=true;this.files=[];this.value='';this.textContent='';
        this.attributes=new Map();this.src='';this.id='';
    }
    append(...nodes){this.children.push(...nodes);}
    replaceChildren(...nodes){this.children=[...nodes];}
    removeAttribute(name){this.attributes.delete(name);if(name==='src')this.src='';}
    addEventListener(name,fn){const handlers=this.listeners.get(name)||[];handlers.push(fn);this.listeners.set(name,handlers);}
    async dispatch(name,event={}){for(const fn of this.listeners.get(name)||[])await fn({preventDefault(){},...event});}
    querySelectorAll(){return [];}
    querySelector(){return null;}
}
class FakeDocument {
    constructor(ids){this.nodes=new Map(ids.map(id=>[id,new FakeElement()]));}
    getElementById(id){return this.nodes.get(id)||null;}
    createElement(tag){return new FakeElement(tag);}
}
const editorIds=[
    'profile-settings','profile-settings-error','profile-settings-status','profile-image-refresh',
    'profile-image-preview','profile-image-fallback','profile-image-file','profile-image-selection',
    'profile-image-upload','profile-image-delete','profile-display-name','profile-reading','profile-call-name',
    'profile-icon','profile-social-links','profile-answer-fields','profile-question-admin',
    'profile-question-list','profile-qr-status','profile-qr-issue','profile-qr-image',
    'profile-editor-form','profile-social-add','profile-question-add','profile-question-new-text',
    'profile-question-new-type','profile-save'
];
const PNG='data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC';
const json=(value,extra={})=>new Response(JSON.stringify(value),{status:200,headers:{'Content-Type':'application/json',...extra}});
const tick=()=>new Promise(resolve=>setImmediate(resolve));

function editorPayload(imageDataUrl=PNG,imageRowVersion=2) {
    return {actorId:'cookie-actor',canManageQuestions:false,questions:[],profile:{
        userId:'member',displayName:'Member',reading:'',callName:'',icon:'🐟',social:[],answers:[],rowVersion:1,
        imageDataUrl,imageRowVersion
    }};
}
function installEditorGlobals() {
    globalThis.document=new FakeDocument(editorIds);
    document.getElementById('profile-settings').isConnected=true;
    globalThis.location={origin:'https://example.test',pathname:'/profile/settings',hash:''};
    globalThis.confirm=()=>true;
}

const originalFetch=globalThis.fetch;
try {
    installEditorGlobals();
    let currentPayload=editorPayload();
    const calls=[];
    globalThis.fetch=async(url,options={})=>{
        const parsed=new URL(url,'https://example.test');
        calls.push({url:parsed,options});
        if(parsed.pathname==='/api/csrf')return json({token:'csrf-token'});
        if(parsed.pathname==='/api/core/profile-editor')return json(currentPayload);
        if(parsed.pathname==='/api/core/profile-qr')return json({exists:false,active:false,rowVersion:0});
        if(parsed.pathname==='/api/core/profile-image'&&options.method==='POST'){
            currentPayload=editorPayload(PNG,3);
            return json({rowVersion:3,width:512,height:256});
        }
        if(parsed.pathname==='/api/core/profile-image-delete'&&options.method==='POST'){
            currentPayload=editorPayload(null,4);
            return json({rowVersion:4,width:0,height:0});
        }
        throw new Error('Unexpected editor request '+parsed.pathname);
    };
    const editor=await import('data:text/javascript;base64,'+Buffer.from(editorSource).toString('base64')+'#success');
    await editor.init();
    check(document.getElementById('profile-image-preview').src===PNG&&!document.getElementById('profile-image-preview').hidden,
        'editor renders only a validated saved PNG data URL');
    check(document.getElementById('profile-image-fallback').hidden,'saved image takes display precedence over emoji');

    const file=new File([new Uint8Array([1,2,3])],'original.webp',{type:'image/webp'});
    const fileInput=document.getElementById('profile-image-file');fileInput.files=[file];
    await fileInput.dispatch('change');
    check(!calls.some(call=>call.url.pathname==='/api/core/profile-image'),'file selection never uploads automatically');
    check(document.getElementById('profile-image-selection').textContent.includes('original.webp')&&
        document.getElementById('profile-image-selection').textContent.includes('3 bytes'),
        'selection displays filename and byte size only');
    check(document.getElementById('profile-image-preview').src===PNG,'selected original is not previewed or decoded');
    fileInput.files=[{name:'unsafe.svg',size:10,type:'image/svg+xml'}];
    await fileInput.dispatch('change');
    await document.getElementById('profile-image-upload').dispatch('click');
    check(document.getElementById('profile-image-selection').textContent.includes('静止画像')&&
        !calls.some(call=>call.url.pathname==='/api/core/profile-image'),
        'SVG selection is rejected without upload');
    fileInput.files=[{name:'oversized.png',size:10*1024*1024+1,type:'image/png'}];
    await fileInput.dispatch('change');
    await document.getElementById('profile-image-upload').dispatch('click');
    check(document.getElementById('profile-image-selection').textContent.includes('10 MiB')&&
        !calls.some(call=>call.url.pathname==='/api/core/profile-image'),
        'files above 10 MiB are rejected without upload');
    fileInput.files=[file];
    await fileInput.dispatch('change');

    await document.getElementById('profile-image-upload').dispatch('click');
    check(calls.filter(call=>call.url.pathname==='/api/core/profile-image').length===1,'explicit upload makes exactly one image request');
    const upload=calls.find(call=>call.url.pathname==='/api/core/profile-image');
    check(upload.url.searchParams.get('expectedActor')==='cookie-actor'&&upload.url.searchParams.get('rowVersion')==='2',
        'upload binds current actor and image row version');
    check(upload.options.method==='POST'&&upload.options.body===file&&upload.options.headers['Content-Type']==='image/webp'&&
        upload.options.headers['X-CSRF-TOKEN']==='csrf-token','upload sends the File directly with MIME type and CSRF header');
    check(document.getElementById('profile-image-preview').src===PNG,'successful upload reloads the normalized saved DTO image');

    await document.getElementById('profile-image-delete').dispatch('click');
    const deletion=calls.find(call=>call.url.pathname==='/api/core/profile-image-delete');
    check(deletion?.url.searchParams.get('expectedActor')==='cookie-actor'&&deletion.options.headers['Content-Type']==='application/json'&&
        JSON.parse(deletion.options.body).rowVersion===3,'delete uses actor query and the current row version JSON');
    check(document.getElementById('profile-image-preview').hidden&&
        document.getElementById('profile-image-fallback').textContent==='🐟'&&
        !document.getElementById('profile-image-fallback').hidden,'delete reload restores the emoji fallback');

    currentPayload=editorPayload('data:image/svg+xml,<svg onload=alert(1)>',undefined);
    calls.length=0;
    await document.getElementById('profile-image-refresh').dispatch('click');
    check(document.getElementById('profile-image-preview').hidden&&
        document.getElementById('profile-image-fallback').textContent==='🐟',
        'unsafe saved SVG is rejected and cannot become an image source');
    check(!calls.some(call=>call.url.pathname==='/api/core/profile-image'),'manual refresh does not replay an image write');
    currentPayload=editorPayload(null);
    delete currentPayload.profile.imageRowVersion;
    await document.getElementById('profile-image-refresh').dispatch('click');
    const noImageFile=new File([new Uint8Array([5])],'no-image.png',{type:'image/png'});
    document.getElementById('profile-image-file').files=[noImageFile];
    await document.getElementById('profile-image-file').dispatch('change');
    await document.getElementById('profile-image-upload').dispatch('click');
    const defaultVersionUpload=calls.filter(call=>call.url.pathname==='/api/core/profile-image').at(-1);
    check(defaultVersionUpload?.url.searchParams.get('rowVersion')==='0',
        'missing image row version defaults to zero for upload');
    await editor.cleanup();

    // An unknown write result must lock every write until an explicit profile refresh.
    installEditorGlobals();
    let refreshPayload=editorPayload(null,0);
    const unknownCalls=[];
    globalThis.fetch=async(url,options={})=>{
        const parsed=new URL(url,'https://example.test');unknownCalls.push({url:parsed,options});
        if(parsed.pathname==='/api/csrf')return json({token:'csrf-token'});
        if(parsed.pathname==='/api/core/profile-editor')return json(refreshPayload);
        if(parsed.pathname==='/api/core/profile-qr')return json({exists:false,active:false,rowVersion:0});
        if(parsed.pathname==='/api/core/profile-image')throw new TypeError('network outcome unknown');
        throw new Error('Unexpected unknown-outcome request '+parsed.pathname);
    };
    const uncertain=await import('data:text/javascript;base64,'+Buffer.from(editorSource).toString('base64')+'#uncertain');
    await uncertain.init();
    const uncertainFile=new File([new Uint8Array([4])],'uncertain.png',{type:'image/png'});
    const uncertainInput=document.getElementById('profile-image-file');uncertainInput.files=[uncertainFile];
    await uncertainInput.dispatch('change');
    await document.getElementById('profile-image-upload').dispatch('click');
    check(unknownCalls.filter(call=>call.url.pathname==='/api/core/profile-image').length===1&&
        document.getElementById('profile-image-upload').disabled,
        'unknown upload outcome is not automatically replayed and locks further writes');
    check(document.getElementById('profile-image-status')===null&&
        document.getElementById('profile-settings-status').textContent.includes('再アップロードせず'),
        'unknown outcome instructs the user to refresh instead of retrying');
    await document.getElementById('profile-image-upload').dispatch('click');
    check(unknownCalls.filter(call=>call.url.pathname==='/api/core/profile-image').length===1,
        'another upload click before manual refresh makes no request');
    await document.getElementById('profile-image-refresh').dispatch('click');
    check(!document.getElementById('profile-image-upload').disabled&&
        document.getElementById('profile-image-refresh').hidden,
        'explicit successful refresh unlocks writes and clears the refresh prompt');
    await uncertain.cleanup();

    installEditorGlobals();
    const deniedCalls=[];
    globalThis.fetch=async(url,options={})=>{
        const parsed=new URL(url,'https://example.test');deniedCalls.push({url:parsed,options});
        if(parsed.pathname==='/api/csrf')return json({token:'csrf-token'});
        if(parsed.pathname==='/api/core/profile-editor')return json(editorPayload(PNG,2));
        if(parsed.pathname==='/api/core/profile-qr')return json({exists:false,active:false,rowVersion:0});
        if(parsed.pathname==='/api/core/profile-image')return new Response('denied',{status:403});
        throw new Error('Unexpected denied request '+parsed.pathname);
    };
    const deniedModule=await import('data:text/javascript;base64,'+Buffer.from(editorSource).toString('base64')+'#denied');
    await deniedModule.init();
    document.getElementById('profile-image-file').files=[new File([new Uint8Array([6])],'denied.png',{type:'image/png'})];
    await document.getElementById('profile-image-file').dispatch('change');
    await document.getElementById('profile-image-upload').dispatch('click');
    check(deniedCalls.some(call=>call.url.pathname==='/api/core/profile-image')&&
        document.getElementById('profile-settings').hidden&&
        document.getElementById('profile-image-preview').hidden&&
        !document.getElementById('profile-image-preview').src,
        '403 after cookie change clears profile identity and leaves no saved image in the DOM');
    await deniedModule.cleanup();

    check(editorSource.includes('setTimeout(()=>controller.abort(),25000)')&&
        editorSource.includes('controller.signal')&&editorSource.includes('activeRequests.add(controller)')&&
        !/FileReader|readAsDataURL|createObjectURL\(file/.test(editorSource),
        'image transport is bounded, abortable, and never previews or buffers the selected original');
    check(settingsMarkup.includes('id="profile-image-preview"')&&memberMarkup.includes('id="profile-member-image"')&&
        shareSource.includes("image.id='profile-qr-share-image'"),
        'editor, member, and guest image DOM IDs are stable');
    check(editorSource.includes('const JSON_LIMIT = 4 * 1024 * 1024')&&
        shareSource.includes('boundedJson(response,4*1024*1024)')&&
        profileTransport.includes('const BASIC_PROFILE_LIMIT = 4 * 1024 * 1024')&&
        memberMarkup.includes('profileStream",id,4*1024*1024'),
        'editor, guest, and basic profile JSON/stream bounds match at 4 MiB');

    // Guest QR shares only the existing authorized JSON response and removes all content at expiry.
    const shareRoot=new FakeElement();shareRoot.id='profile-qr-share-result';shareRoot.hidden=true;
    const shareError=new FakeElement();shareError.id='profile-qr-share-error';
    const shareStatus=new FakeElement();shareStatus.id='profile-qr-share-status';
    globalThis.document={getElementById:id=>({[shareRoot.id]:shareRoot,[shareError.id]:shareError,[shareStatus.id]:shareStatus})[id]||null,
        createElement:tag=>new FakeElement(tag)};
    globalThis.location={pathname:'/profile/share',hash:'#token='+'a'.repeat(64)};
    globalThis.history={replaceState(){globalThis.location.hash='';}};
    globalThis.window={addEventListener(){}};
    const timers=new Map();let timerId=0;
    const originalSetTimeout=globalThis.setTimeout,originalClearTimeout=globalThis.clearTimeout;
    globalThis.setTimeout=(fn,delay)=>{const id=++timerId;timers.set(id,{fn,delay});return id;};
    globalThis.clearTimeout=id=>timers.delete(id);
    const shareCalls=[];
    globalThis.fetch=async(url,options={})=>{
        const parsed=new URL(url,'https://example.test');shareCalls.push({url:parsed,options});
        if(parsed.pathname==='/api/csrf')return json({token:'csrf-token'});
        if(parsed.pathname==='/api/core/profile-qr-view')
            return json({userId:'member',displayName:'Member',icon:'🐟',reading:'',callName:'',answers:[],social:[],imageDataUrl:PNG},
                {'X-Profile-QR-Expires-At':new Date(Date.now()+60_000).toISOString()});
        throw new Error('Unexpected guest request '+parsed.pathname);
    };
    const share=await import('data:text/javascript;base64,'+Buffer.from(shareSource).toString('base64')+'#guest');
    await share.init();
    const qrImage=shareRoot.children.flatMap(node=>node.children||[]).find(node=>node.id==='profile-qr-share-image');
    check(shareCalls.filter(call=>call.url.pathname==='/api/core/profile-qr-view').length===1&&
        shareCalls.every(call=>call.url.origin==='https://example.test'),
        'guest image is carried by one same-origin authorized QR DTO without an image fetch');
    check(qrImage?.src===PNG&&shareRoot.hidden===false,'guest QR renders the validated image data URL');
    const expiry=[...timers.values()].find(timer=>timer.delay>25_000);
    check(Boolean(expiry),'guest QR expiry timer is established');
    expiry.fn();
    check(shareRoot.hidden&&shareRoot.children.length===0&&
        ![...shareRoot.children].some(node=>node.id==='profile-qr-share-image'),
        'guest QR expiry clears the image with the rest of the profile DOM');
    globalThis.setTimeout=originalSetTimeout;globalThis.clearTimeout=originalClearTimeout;
    console.log(`Profile image transport checks passed: ${passed}`);
} finally {
    globalThis.fetch=originalFetch;
}