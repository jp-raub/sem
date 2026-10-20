// Compatibility for public section links only. Never forwards a query or accepts a destination.
let owner = null;
function redirectLegacyAccountLink() {
    if (!owner?.isConnected || owner !== document.getElementById('profile-settings') ||
        location.pathname !== '/profile/settings') return false;
    const hash = location.hash;
    if (hash !== '#account-passkeys' && hash !== '#profile-email-change') return false;
    location.replace('/settings/account' + hash);
    return true;
}
function onHashChange() { redirectLegacyAccountLink(); }
export function cleanup() {
    window.removeEventListener('hashchange', onHashChange);
    owner = null;
}
export function init() {
    cleanup();
    owner = document.getElementById('profile-settings');
    if (!owner) return false;
    if (redirectLegacyAccountLink()) return true;
    window.addEventListener('hashchange', onHashChange);
    return false;
}
