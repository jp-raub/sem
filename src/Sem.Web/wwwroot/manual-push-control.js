// One explicit browser request. No storage, logging, background loop or automatic retry.
async function request(action, actor, payload) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 25000);
    try {
        const options = {cache:'no-store',redirect:'error',signal:controller.signal};
        if (payload !== undefined) {
            const csrf = await fetch('/api/csrf', options);
            if (!csrf.ok) throw new Error('CSRF unavailable');
            const {token} = await csrf.json();
            if (typeof token !== 'string' || !token) throw new Error('CSRF unavailable');
            Object.assign(options, {method:'POST',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':token},body:JSON.stringify(payload)});
        }
        const query = payload === undefined ? '' : '?' + new URLSearchParams({actor});
        const response = await fetch('/api/core/' + action + query, options);
        if ([400,403,409].includes(response.status)) return {status:response.status,value:null};
        if (response.status !== 200) throw new Error('Uncertain result');
        return {status:200,value:await response.json()};
    } finally { clearTimeout(timer); }
}
export const context = () => request('manual-push-control');
export const change = (actor, payload) => request('manual-push-control-change', actor, payload);
