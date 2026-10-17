const MAX_JSON_BYTES = 512 * 1024;
const MAX_CSRF_BYTES = 16 * 1024;
const MAX_MUTATION_RESPONSE_BYTES = 4 * 1024;
const MAX_REQUEST_JSON_BYTES = 4 * 1024;
const REQUEST_TIMEOUT_MS = 25_000;
const PAGE_SIZE = 50;
const MAX_PAGE = 10_000;
const PAGE_LIMIT_MESSAGE = 'APIの最大ページ数10000に達し、次データが残っているため、この画面では取得できません。';
let liveState = null;
let nextGeneration = 0;

class AuthenticationStateError extends Error {}
class HttpStatusError extends Error {
  constructor(status) { super('HTTP status'); this.status = status; }
}
class StaleOperationError extends Error {}

function isRecord(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }
function isIdentifier(value) { return typeof value === 'string' && value.length >= 1 && value.length <= 128 && !/[\u0000-\u001f\u007f-\u009f]/.test(value); }
function isCurrent(state) { return liveState === state && !state.disposed && !state.pageHidden && state.generation === nextGeneration && state.root?.isConnected !== false; }
function isOperationCurrent(state, operation) { return isCurrent(state) && state.operationGeneration === operation; }
export function paginationState(page, hasMoreInvitations, hasMoreRequests) {
  const hasMore = hasMoreInvitations || hasMoreRequests;
  const validPage = Number.isSafeInteger(page) && page >= 1 && page <= MAX_PAGE;
  return Object.freeze({
    canAdvance: validPage && page < MAX_PAGE && hasMore,
    limitReached: validPage && page === MAX_PAGE && hasMore,
    notice: validPage && page === MAX_PAGE && hasMore ? PAGE_LIMIT_MESSAGE : ''
  });
}
function element(state, name) { return state.elements[name]; }
function writeText(state, name, value) { const node = element(state, name); if (node) node.textContent = value ?? ''; }
function create(state, tag, text = null) {
  const node = state.root.ownerDocument.createElement(tag);
  if (text !== null) node.textContent = String(text);
  return node;
}
function replaceChildren(node, ...children) { node.replaceChildren(...children); }

async function readBoundedJson(response, maxBytes = MAX_JSON_BYTES) {
  if (!Number.isSafeInteger(maxBytes) || maxBytes < 1 || maxBytes > MAX_JSON_BYTES) throw new TypeError('invalid response bound');
  const chunks = [];
  let total = 0;
  if (response.body?.getReader) {
    const reader = response.body.getReader();
    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        total += value.byteLength;
        if (total > maxBytes) {
          try { await reader.cancel(); } catch { }
          throw new RangeError('response too large');
        }
        chunks.push(value);
      }
    } finally { try { reader.releaseLock(); } catch { } }
  } else {
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (bytes.byteLength > maxBytes) throw new RangeError('response too large');
    chunks.push(bytes); total = bytes.byteLength;
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
}

function cancelBody(response) {
  try { const pending = response.body?.cancel(); pending?.catch?.(() => {}); } catch { }
}

async function requestJson(state, path, { method = 'GET', actorId = null, body = undefined, csrfToken = null, maxBytes = method === 'POST' ? MAX_MUTATION_RESPONSE_BYTES : MAX_JSON_BYTES } = {}) {
  if (!isCurrent(state)) throw new StaleOperationError();
  const url = new URL(path, globalThis.location.href);
  if (url.origin !== globalThis.location.origin || url.username || url.password || url.hash) throw new TypeError('same-origin endpoint required');
  if (actorId !== null) {
    if (url.searchParams.has('expectedActor')) throw new TypeError('duplicate expectedActor');
    url.searchParams.set('expectedActor', actorId);
    if (url.searchParams.getAll('expectedActor').length !== 1) throw new TypeError('duplicate expectedActor');
  }
  const controller = new AbortController();
  state.controllers.add(controller);
  const timer = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
  try {
    const headers = { Accept: 'application/json' };
    const options = {
      method, headers, credentials: 'same-origin', mode: 'same-origin', cache: 'no-store',
      redirect: 'error', referrerPolicy: 'same-origin', signal: controller.signal
    };
    if (method === 'POST') {
      const serialized = JSON.stringify(body);
      if (new TextEncoder().encode(serialized).byteLength > MAX_REQUEST_JSON_BYTES) throw new RangeError('request too large');
      headers['Content-Type'] = 'application/json';
      headers['X-CSRF-TOKEN'] = csrfToken;
      options.body = serialized;
    }
    const response = await globalThis.fetch(url.href, options);
    if (response.status === 401 || response.status === 403) {
      cancelBody(response);
      throw new AuthenticationStateError();
    }
    const value = await readBoundedJson(response, maxBytes);
    if (!response.ok) throw new HttpStatusError(response.status);
    if (!isCurrent(state)) throw new StaleOperationError();
    return value;
  } finally {
    clearTimeout(timer);
    state.controllers.delete(controller);
  }
}

function requiredText(value, maxLength = 4096) {
  if (typeof value !== 'string' || value.length > maxLength) throw new TypeError('invalid text field');
  return value;
}
function optionalText(value, maxLength = 4096) {
  if (value === null || value === undefined) return null;
  return requiredText(value, maxLength);
}
function timestamp(value, optional = false) {
  if (optional && (value === null || value === undefined)) return null;
  if (!Number.isSafeInteger(value)) throw new TypeError('invalid timestamp');
  return value;
}
function integer(value, optional = false) {
  if (optional && (value === null || value === undefined)) return null;
  if (!Number.isSafeInteger(value) || value < 0) throw new TypeError('invalid number');
  return value;
}
function normalizeInvitation(row) {
  if (!isRecord(row) || !isIdentifier(row.id)) throw new TypeError('invalid invitation');
  if (typeof row.disabled !== 'boolean') throw new TypeError('invalid invitation state');
  if (!Object.hasOwn(row, 'purpose') || (row.purpose !== null && typeof row.purpose !== 'string')
      || !Object.hasOwn(row, 'issuanceSource') || (row.issuanceSource !== null && typeof row.issuanceSource !== 'string')) throw new TypeError('invalid invitation purpose or source');
  const purpose = optionalText(row.purpose, 500);
  const issuanceSource = optionalText(row.issuanceSource, 128);
  if ([purpose, issuanceSource].some(value => value !== null && /[\u0000-\u001f\u007f-\u009f]/.test(value))) throw new TypeError('invalid invitation purpose or source');
  return {
    id: row.id,
    ownerId: optionalText(row.ownerId, 128), ownerName: optionalText(row.ownerName, 512),
    issuerId: optionalText(row.issuerId, 128), issuerName: optionalText(row.issuerName, 512),
    requestId: optionalText(row.requestId, 128), purpose,
    issuanceSource, limit: integer(row.limit, true), uses: integer(row.uses),
    created: timestamp(row.created), expires: timestamp(row.expires, true), disabled: row.disabled,
    revokedAt: timestamp(row.revokedAt, true), revokedBy: optionalText(row.revokedBy, 128),
    revokerName: optionalText(row.revokerName, 512), revocationReason: optionalText(row.revocationReason, 500)
  };
}
function normalizeRequest(row) {
  if (!isRecord(row) || !isIdentifier(row.id)) throw new TypeError('invalid request');
  const decisionComment = optionalText(row.decisionComment, 500);
  if (decisionComment !== null && /[\u0000-\u001f\u007f-\u009f]/.test(decisionComment)) throw new TypeError('invalid decision comment');
  return {
    id: row.id, userId: optionalText(row.userId, 128), userName: optionalText(row.userName, 512),
    reason: optionalText(row.reason, 500), count: integer(row.count), days: integer(row.days),
    status: requiredText(row.status, 128), created: timestamp(row.created),
    decidedAt: timestamp(row.decidedAt, true), decidedBy: optionalText(row.decidedBy, 128),
    deciderName: optionalText(row.deciderName, 512), decisionComment
  };
}
function validatePage(value, requestedPage) {
  if (!Number.isSafeInteger(requestedPage) || requestedPage < 1 || requestedPage > MAX_PAGE
      || !isRecord(value) || !isIdentifier(value.actorId) || typeof value.isAdmin !== 'boolean'
      || value.page !== requestedPage || value.pageSize !== PAGE_SIZE
      || typeof value.hasMoreInvitations !== 'boolean' || typeof value.hasMoreRequests !== 'boolean'
      || !Array.isArray(value.invitations) || value.invitations.length > PAGE_SIZE
      || !Array.isArray(value.requests) || value.requests.length > PAGE_SIZE) throw new TypeError('invalid invitations page');
  return {
    actorId: value.actorId, isAdmin: value.isAdmin, page: value.page, pageSize: value.pageSize,
    hasMoreInvitations: value.hasMoreInvitations, hasMoreRequests: value.hasMoreRequests,
    invitations: value.invitations.map(normalizeInvitation), requests: value.requests.map(normalizeRequest)
  };
}

function formatTimestamp(seconds) {
  if (seconds === null) return '—';
  const milliseconds = seconds * 1000;
  if (!Number.isFinite(milliseconds) || Math.abs(milliseconds) > 8.64e15) return '日時不明';
  const date = new Date(milliseconds);
  return Number.isNaN(date.getTime()) ? '日時不明' : date.toLocaleString('ja-JP', { timeZone: 'Asia/Tokyo' });
}
function invitationStatus(invitation) {
  if (invitation.disabled) return invitation.revokedAt === null ? '無効' : '失効済み';
  if (invitation.expires !== null && invitation.expires <= Math.floor(Date.now() / 1000)) return '期限切れ';
  if (invitation.limit !== null && invitation.uses >= invitation.limit) return '人数上限到達';
  return '有効';
}
function requestStatus(status) {
  return status === 'Pending' ? '確認待ち'
    : status === 'Approved' ? '許可'
      : status === 'Rejected' ? '拒否' : status;
}
function displayOrDash(value) { return value === null || value === '' ? '—' : value; }
function personText(name, id) {
  const first = typeof name === 'string' && name.trim() ? name : '不明';
  return id ? `${first}（${id}）` : first;
}
function cell(state, row, text, header = false) {
  const node = create(state, header ? 'th' : 'td', text);
  if (header) node.setAttribute('scope', 'col');
  row.append(node);
  return node;
}
function emptyRow(state, body, count, message) {
  const row = create(state, 'tr');
  const col = create(state, 'td', message);
  col.setAttribute('colspan', String(count));
  row.append(col); body.append(row);
}

function renderInvitations(state) {
  const head = element(state, 'invitationHead');
  const body = element(state, 'invitationBody');
  const headers = ['招待ID', '対象ユーザー', '発行者', '関連依頼ID', '目的', '発行元', '上限', '使用数', '作成日時', '有効期限', '状態', '失効情報'];
  headers.push('本人の操作');
  const headerRow = create(state, 'tr');
  for (const value of headers) cell(state, headerRow, value, true);
  replaceChildren(head, headerRow);
  body.replaceChildren();
  if (state.invitations.length === 0) { emptyRow(state, body, headers.length, 'このページに招待はありません。'); return; }
  for (const invitation of state.invitations) {
    const row = create(state, 'tr');
    cell(state, row, invitation.id);
    cell(state, row, personText(invitation.ownerName, invitation.ownerId));
    cell(state, row, personText(invitation.issuerName, invitation.issuerId));
    cell(state, row, displayOrDash(invitation.requestId));
    cell(state, row, invitation.purpose === null || invitation.purpose === '' ? '未記録' : invitation.purpose);
    cell(state, row, invitation.issuanceSource === null || invitation.issuanceSource === '' ? '未記録'
      : invitation.issuanceSource === 'Direct' ? '直接発行'
        : invitation.issuanceSource === 'Request' ? '依頼承認' : invitation.issuanceSource);
    cell(state, row, invitation.limit === null ? '—' : String(invitation.limit));
    cell(state, row, String(invitation.uses));
    cell(state, row, formatTimestamp(invitation.created));
    cell(state, row, formatTimestamp(invitation.expires));
    cell(state, row, invitationStatus(invitation));
    const revokeInfo = [];
    if (invitation.revokedAt !== null) revokeInfo.push(`失効日時：${formatTimestamp(invitation.revokedAt)}`);
    if (invitation.revokerName || invitation.revokedBy) revokeInfo.push(`失効者：${personText(invitation.revokerName, invitation.revokedBy)}`);
    if (invitation.revocationReason) revokeInfo.push(`理由：${invitation.revocationReason}`);
    cell(state, row, revokeInfo.join(' ／ ') || '—');
    const actionCell = create(state, 'td');
    if (invitation.ownerId === state.actorId && invitationStatus(invitation) === '有効') {
      const linkButton = create(state, 'button', '本人リンクを取得');
      linkButton.type = 'button';
      linkButton.disabled = state.busy || !!state.unknown || state.readErrorLock;
      linkButton.setAttribute('aria-label', `招待 ${invitation.id} の本人リンクを取得`);
      linkButton.addEventListener('click', () => void retrieveOwnerLink(state, invitation));
      actionCell.append(linkButton);
    }
    if (state.isAdmin && !invitation.disabled) {
      const button = create(state, 'button', '失効対象に選ぶ');
      button.type = 'button';
      button.disabled = state.busy || !!state.unknown || state.readErrorLock;
      button.setAttribute('aria-label', `招待 ${invitation.id} を失効対象に選ぶ`);
      button.addEventListener('click', () => selectTarget(state, invitation));
      actionCell.append(button);
    }
    if (!actionCell.childNodes.length) actionCell.textContent = '—';
    row.append(actionCell);
    body.append(row);
  }
}

function renderRequests(state) {
  const head = element(state, 'requestHead');
  const body = element(state, 'requestBody');
  const headers = ['依頼ID', '依頼者', '依頼理由', '希望数', '日数', '状態', '作成日時', '決定日時', '決定者', '判断コメント', '操作'];
  const headerRow = create(state, 'tr');
  for (const value of headers) cell(state, headerRow, value, true);
  replaceChildren(head, headerRow);
  body.replaceChildren();
  if (state.requests.length === 0) { emptyRow(state, body, headers.length, 'このページに依頼はありません。'); return; }
  for (const request of state.requests) {
    const row = create(state, 'tr');
    cell(state, row, request.id);
    cell(state, row, personText(request.userName, request.userId));
    cell(state, row, displayOrDash(request.reason));
    cell(state, row, String(request.count));
    cell(state, row, String(request.days));
    cell(state, row, requestStatus(request.status));
    cell(state, row, formatTimestamp(request.created));
    cell(state, row, formatTimestamp(request.decidedAt));
    cell(state, row, personText(request.deciderName, request.decidedBy));
    cell(state, row, request.decisionComment ?? '—');
    const actionCell = create(state, 'td');
    if (state.isAdmin && request.status === 'Pending') {
      const button = create(state, 'button', '確認・判断');
      button.type = 'button';
      button.disabled = state.busy || !!state.unknown || state.readErrorLock;
      button.setAttribute('aria-label', `依頼 ${request.id} を確認・判断`);
      button.addEventListener('click', () => selectRequest(state, request));
      actionCell.append(button);
    } else actionCell.textContent = '—';
    row.append(actionCell);
    body.append(row);
  }
}

function normalizedReason(raw) {
  if (typeof raw !== 'string' || /[\u0000-\u001f\u007f-\u009f]/.test(raw)) return null;
  const reason = raw.trim();
  return reason.length >= 1 && reason.length <= 500 ? reason : null;
}
function normalizedPurpose(raw) {
  if (typeof raw !== 'string' || /[\u0000-\u001f\u007f-\u009f]/.test(raw)) return null;
  const purpose = raw.trim();
  return purpose.length >= 1 && purpose.length <= 500 ? purpose : null;
}
function issueValues(state) {
  const purpose = normalizedPurpose(element(state, 'issuePurpose').value);
  const countText = element(state, 'issueCount').value;
  const daysText = element(state, 'issueDays').value;
  if (purpose === null || !/^[1-5]$/.test(String(countText)) || !/^[1-7]$/.test(String(daysText))) return null;
  return Object.freeze({ purpose, count: Number(countText), days: Number(daysText) });
}
function requestValues(state) {
  const reason = normalizedReason(element(state, 'requestReason').value);
  const countText = element(state, 'requestCount').value;
  const daysText = element(state, 'requestDays').value;
  if (reason === null || !/^[1-5]$/.test(String(countText)) || !/^[1-7]$/.test(String(daysText))) return null;
  return Object.freeze({ reason, count: Number(countText), days: Number(daysText) });
}
// INV-003-A approved contract; source-only until parent review/test freeze:
// decision comments are visible to requester/admins; legacy NULL remains NULL.
function normalizedDecisionComment(raw) {
  if (typeof raw !== 'string' || /[\u0000-\u001f\u007f-\u009f]/.test(raw)) return null;
  const comment = raw.trim();
  return comment.length <= 500 ? comment : null;
}
function validOptionalDecisionComment(raw) {
  return typeof raw === 'string' && !/[\u0000-\u001f\u007f-\u009f]/.test(raw) && raw.trim().length <= 500;
}
function clearIssueOutput(state) {
  state.issuedLink = null;
  element(state, 'issueLink').value = '';
  element(state, 'issueLink').disabled = true;
  element(state, 'issueCopy').disabled = true;
  element(state, 'issueResult').hidden = true;
  writeText(state, 'issueCopyStatus', '');
}
function clearOwnerLink(state) {
  state.ownerLink = null;
  const input = element(state, 'ownerLinkValue');
  if (input) { input.value = ''; input.disabled = true; }
  const button = element(state, 'ownerLinkCopy');
  if (button) button.disabled = true;
  const section = element(state, 'ownerLinkResult');
  if (section) section.hidden = true;
  writeText(state, 'ownerLinkSummary', '');
  writeText(state, 'ownerLinkCopyStatus', '');
}
function resetIssueConfirmation(state, clearOutput = false) {
  state.issueSnapshot = null;
  element(state, 'issueConfirm').checked = false;
  if (clearOutput) clearIssueOutput(state);
}
function updateIssue(state) {
  const values = issueValues(state);
  const locked = !isCurrent(state) || !state.isAdmin || state.busy || !!state.unknown || state.readErrorLock;
  element(state, 'issue').hidden = !state.isAdmin;
  element(state, 'issueFields').disabled = locked;
  element(state, 'issueSubmit').disabled = locked || values === null || !element(state, 'issueConfirm').checked;
  element(state, 'issueLink').disabled = !state.issuedLink;
  element(state, 'issueCopy').disabled = !state.issuedLink;
}
function updateRequestControls(state) {
  const visible = !!state.actorId && !state.isAdmin;
  element(state, 'requestCreate').hidden = !visible;
  const locked = !isCurrent(state) || !visible || state.busy || !!state.unknown || state.readErrorLock;
  element(state, 'requestFields').disabled = locked;
  element(state, 'requestSubmit').disabled = locked || requestValues(state) === null;
}
function updateReviewControls(state) {
  const selected = state.selectedRequest;
  const visible = !!state.isAdmin && !!selected;
  element(state, 'requestReview').hidden = !visible;
  const locked = !isCurrent(state) || !visible || state.busy || !!state.unknown || state.readErrorLock
    || !state.requests.some(row => row.id === selected?.id && row.status === 'Pending');
  const commentValid = validOptionalDecisionComment(element(state, 'reviewComment').value);
  for (const name of ['reviewChoice', 'reviewComment', 'reviewConfirm', 'reviewSubmit']) {
    element(state, name).disabled = locked || (name === 'reviewSubmit' &&
      (!element(state, 'reviewConfirm').checked || !commentValid));
  }
  element(state, 'reviewCancel').disabled = state.busy;
  if (selected) {
    writeText(state, 'reviewSummary',
      `依頼者：${personText(selected.userName, selected.userId)} ／ 依頼日時：${formatTimestamp(selected.created)}\n` +
      `希望人数：${selected.count}名 ／ 有効期間：${selected.days}日（発行から）\n理由：${selected.reason}`);
  }
}
function clearConfirmation(state) {
  state.selectedTarget = null;
  element(state, 'reason').value = '';
  element(state, 'confirm').checked = false;
}
function updateConfirmation(state) {
  const selected = state.selectedTarget;
  element(state, 'selected').hidden = !selected || !state.isAdmin;
  const revokeDisabled = !isCurrent(state) || !state.isAdmin || !selected || state.busy || !!state.unknown || state.readErrorLock
    || !element(state, 'confirm').checked || normalizedReason(element(state, 'reason').value) === null;
  element(state, 'reason').disabled = !selected || state.busy || !!state.unknown || state.readErrorLock || !state.isAdmin;
  element(state, 'confirm').disabled = !selected || state.busy || !!state.unknown || state.readErrorLock || !state.isAdmin;
  element(state, 'revoke').disabled = revokeDisabled;
  if (selected) writeText(state, 'selectedSummary', `招待ID：${selected.id} ／ 対象ユーザー：${personText(selected.ownerName, selected.ownerId)} ／ 発行者：${personText(selected.issuerName, selected.issuerId)}。この内容を確認してから理由を入力し、確認欄にチェックしてください。`);
  const unknownElement = element(state, 'unknown');
  unknownElement.hidden = !state.unknown;
  unknownElement.textContent = state.unknown?.message ?? '';
  updateIssue(state);
  updateRequestControls(state);
  updateReviewControls(state);
}
function render(state) {
  if (!isCurrent(state)) return;
  const pagination = paginationState(state.page, state.hasMoreInvitations, state.hasMoreRequests);
  element(state, 'admin').hidden = !state.isAdmin;
  writeText(state, 'scope', state.isAdmin
    ? '全体管理者として、招待・依頼の一覧と操作を利用できます。招待リンクを取得できるのは所有者本人だけです。'
    : '本人の依頼と招待を表示しています。招待リンクは所有者本人だけが取得できます。');
  writeText(state, 'status', state.statusMessage);
  writeText(state, 'error', state.errorMessage);
  writeText(state, 'issueStatus', state.issueStatusMessage);
  writeText(state, 'issueError', state.issueErrorMessage);
  writeText(state, 'requestCreateStatus', state.requestStatusMessage);
  writeText(state, 'requestCreateError', state.requestErrorMessage);
  writeText(state, 'reviewStatus', state.reviewStatusMessage);
  writeText(state, 'reviewError', state.reviewErrorMessage);
  writeText(state, 'ownerLinkCopyStatus', state.ownerLink?.copyStatus ?? '');
  element(state, 'refresh').disabled = state.busy || (!state.actorId && !state.readErrorLock);
  element(state, 'previous').disabled = !state.actorId || state.busy || state.readErrorLock || state.page <= 1;
  element(state, 'next').disabled = !state.actorId || state.busy || state.readErrorLock || !pagination.canAdvance;
  writeText(state, 'pageLabel', `ページ ${state.page || '—'}（最大${PAGE_SIZE}件ずつ。全件取得済みとは限りません）${pagination.notice ? ` ${pagination.notice}` : ''}`);
  renderInvitations(state);
  renderRequests(state);
  updateConfirmation(state);
}

function elements(root) {
  const byId = id => root.querySelector(`#invitation-management-${id}`);
  const byIssueId = id => root.querySelector(`#invitation-issue-${id}`);
  const byRequestId = id => root.querySelector(`#invitation-request-create-${id}`);
  const byReviewId = id => root.querySelector(`#invitation-request-review-${id}`);
  const byOwnerLinkId = id => root.querySelector(`#invitation-owner-link-${id}`);
  return {
    scope: byId('scope'), status: byId('status'), error: byId('error'), moduleError: byId('module-error'),
    refresh: byId('refresh'), admin: byId('admin'), selected: byId('selected'), selectedSummary: byId('selected-summary'),
    form: byId('revoke-form'), reason: byId('reason'), confirm: byId('confirm'), revoke: byId('revoke'), unknown: byId('unknown'),
    issue: root.querySelector('#invitation-issue'), issueForm: byIssueId('form'), issueFields: byIssueId('fields'),
    issuePurpose: byIssueId('purpose'), issueCount: byIssueId('count'), issueDays: byIssueId('days'),
    issueConfirm: byIssueId('confirm'), issueSubmit: byIssueId('submit'), issueResult: byIssueId('result'),
    issueStatus: byIssueId('status'), issueError: byIssueId('error'),
    issueLink: byIssueId('link'), issueCopy: byIssueId('copy'), issueCopyStatus: byIssueId('copy-status'),
    requestCreate: root.querySelector('#invitation-request-create'),
    requestForm: byRequestId('form'), requestFields: byRequestId('fields'),
    requestReason: byRequestId('reason'), requestCount: byRequestId('count'),
    requestDays: byRequestId('days'), requestSubmit: byRequestId('submit'),
    requestCreateStatus: byRequestId('status'), requestCreateError: byRequestId('error'),
    requestReview: root.querySelector('#invitation-request-review'),
    reviewForm: byReviewId('form'), reviewSummary: byReviewId('summary'),
    reviewStatus: byReviewId('status'), reviewError: byReviewId('error'),
    reviewChoice: byReviewId('choice'), reviewComment: byReviewId('comment'),
    reviewConfirm: byReviewId('confirm'), reviewSubmit: byReviewId('submit'),
    reviewCancel: byReviewId('cancel'),
    ownerLinkResult: root.querySelector('#invitation-owner-link-result'),
    ownerLinkSummary: byOwnerLinkId('summary'), ownerLinkValue: byOwnerLinkId('value'),
    ownerLinkCopy: byOwnerLinkId('copy'), ownerLinkCopyStatus: byOwnerLinkId('copy-status'),
    invitationHead: byId('invitations-head'), invitationBody: byId('invitations-body'),
    requestHead: byId('requests-head'), requestBody: byId('requests-body'),
    previous: byId('previous'), next: byId('next'), pageLabel: byId('page')
  };
}

function selectTarget(state, invitation) {
  if (!isCurrent(state) || !state.isAdmin || state.busy || state.unknown || state.readErrorLock || invitation.disabled) return;
  state.operationGeneration++;
  state.selectedTarget = Object.freeze({ ...invitation });
  element(state, 'reason').value = '';
  element(state, 'confirm').checked = false;
  state.errorMessage = '';
  state.statusMessage = `招待ID ${invitation.id} を失効対象として選択しました。対象を確認し、理由を入力して明示確認してください。`;
  render(state);
}

function selectRequest(state, request) {
  if (!isCurrent(state) || !state.isAdmin || state.busy || state.unknown || state.readErrorLock || request.status !== 'Pending') return;
  state.operationGeneration++;
  state.selectedRequest = Object.freeze({ ...request });
  element(state, 'reviewChoice').value = 'approve';
  element(state, 'reviewComment').value = '';
  element(state, 'reviewConfirm').checked = false;
  state.reviewStatusMessage = '依頼内容を確認し、判断と必要なコメントを入力してください。';
  state.reviewErrorMessage = '';
  render(state);
}

function applyPage(state, data) {
  if (state.actorId !== null && data.actorId !== state.actorId) throw new AuthenticationStateError();
  state.actorId = data.actorId;
  state.isAdmin = data.isAdmin;
  if (!data.isAdmin) resetIssueConfirmation(state, true);
  state.page = data.page;
  state.pageSize = data.pageSize;
  state.hasMoreInvitations = data.hasMoreInvitations;
  state.hasMoreRequests = data.hasMoreRequests;
  state.invitations = data.invitations;
  state.requests = data.requests;
  state.readErrorLock = false;
  if (state.unknown?.kind !== 'issue') state.issueErrorMessage = '';
  if (state.unknown?.kind === 'issue') {
    state.unknown.message = '発行結果は未確認です。読み取り専用の一覧を更新しましたが、新しい発行の成否を完全には照合できません。自動再送は行わず、発行操作のロックを維持します。';
    state.statusMessage = '発行結果未確認のため、一覧は更新しましたが発行操作はロック中です。';
    state.issueStatusMessage = state.statusMessage;
    state.issueErrorMessage = state.unknown.message;
  } else if (state.unknown?.kind === 'request') {
    state.unknown.message = '依頼の結果はまだ特定できません。自動再送しません。依頼一覧を確認し、同じ内容を再送しないでください。結果が不明な場合は管理者へ確認してください。';
    state.statusMessage = '依頼結果未確認のため、新しい依頼をロックしています。';
    state.requestErrorMessage = state.unknown.message;
  } else if (state.unknown?.kind === 'decision') {
    const current = state.requests.find(row => row.id === state.unknown.id);
    if (current && current.status !== 'Pending') {
      state.unknown = null;
      state.selectedRequest = null;
      state.reviewStatusMessage = `手動確認で依頼 ${current.id} の判断済み状態を確認しました。`;
      state.reviewErrorMessage = '';
      state.statusMessage = state.reviewStatusMessage;
    } else {
      state.unknown.message = `依頼 ${state.unknown.id} の判断結果は未確認です。自動再送せず、依頼の状態を管理者間で確認してください。`;
      state.statusMessage = '判断結果未確認のため、管理者操作をロックしています。';
      state.reviewErrorMessage = state.unknown.message;
    }
  } else if (state.unknown) {
    const current = state.invitations.find(row => row.id === state.unknown.id);
    if (current) {
      const previousUnknown = state.unknown;
      state.unknown = null;
      clearConfirmation(state);
      state.statusMessage = current.disabled
        ? `手動確認で招待ID ${previousUnknown.id} が失効済みと確認できました。`
        : `手動確認で招待ID ${previousUnknown.id} はまだ有効と確認できました。再度操作する場合は招待を選び直し、新しく確認してください。`;
    } else {
      state.unknown.message = `招待ID ${state.unknown.id} の書き込み結果は未確認です。このページには対象がありません。ページを移動して手動で探してください。自動再送は行いません。`;
      state.statusMessage = '書き込み結果は未確認のままです。対象行を含むページを手動で再確認してください。';
    }
  } else {
    clearConfirmation(state);
    state.statusMessage = `ページ ${data.page} を表示しています。招待 ${data.invitations.length} 件、依頼 ${data.requests.length} 件。全件取得済みとは限りません。`;
    state.issueStatusMessage = data.isAdmin ? '管理者向けの直接発行操作を利用できます。発行前に目的・上限・日数を確認してください。'
      : '招待の直接発行は管理者だけが利用できます。';
    state.issueErrorMessage = '';
  }
  state.errorMessage = '';
}

async function fetchPage(state, page) {
  const url = new URL('/api/core/invitations', globalThis.location.href);
  url.searchParams.set('page', String(page));
  const value = await requestJson(state, `${url.pathname}${url.search}`);
  if (!isCurrent(state)) throw new StaleOperationError();
  return validatePage(value, page);
}

async function loadPage(state, page) {
  if (!isCurrent(state) || state.busy || !Number.isSafeInteger(page) || page < 1 || page > MAX_PAGE) return;
  const operation = ++state.operationGeneration;
  state.busy = true;
  state.errorMessage = '';
  state.statusMessage = `ページ ${page} を読み込んでいます。`;
  clearConfirmation(state);
  state.selectedRequest = null;
  element(state, 'reviewComment').value = '';
  element(state, 'reviewConfirm').checked = false;
  clearOwnerLink(state);
  resetIssueConfirmation(state, false);
  render(state);
  try {
    const data = await fetchPage(state, page);
    if (!isOperationCurrent(state, operation)) return;
    applyPage(state, data);
    render(state);
  } catch (error) {
    if (!isOperationCurrent(state, operation)) return;
    if (error instanceof AuthenticationStateError) clearActorState(state);
    else {
      state.readErrorLock = true;
      state.errorMessage = error instanceof RangeError
        ? '一覧応答が上限を超えたか、形式を確認できませんでした。部分データは使用せず、書き込み操作を停止しました。'
        : '招待・依頼一覧を安全に読み込めませんでした。通信状態を確認し、明示的に再読込してください。';
      state.issueErrorMessage = state.errorMessage;
      state.statusMessage = '一覧を読み込めませんでした。';
      render(state);
    }
  } finally {
    if (isOperationCurrent(state, operation)) { state.busy = false; render(state); }
  }
}

async function readCsrf(state) {
  const value = await requestJson(state, '/api/csrf', { maxBytes: MAX_CSRF_BYTES });
  if (!isCurrent(state)) throw new StaleOperationError();
  if (!isRecord(value) || typeof value.token !== 'string' || value.token.length < 1 || value.token.length > 8192 || /[\u0000-\u001f\u007f-\u009f]/.test(value.token)) throw new TypeError('invalid CSRF response');
  return value.token;
}
function actionCurrent(state, operation, selected, actorId) {
  return isOperationCurrent(state, operation) && state.selectedTarget === selected && state.actorId === actorId;
}
function issueActionCurrent(state, operation, snapshot, actorId) {
  if (!isOperationCurrent(state, operation) || state.actorId !== actorId || !state.isAdmin || state.issueSnapshot !== snapshot) return false;
  const values = issueValues(state);
  return values !== null && values.purpose === snapshot.purpose && values.count === snapshot.count && values.days === snapshot.days
    && element(state, 'issueConfirm').checked;
}
function validateIssueResponse(value, actorId, snapshot) {
  const expectedKeys = ['actorId', 'id', 'purpose', 'issuanceSource', 'limit', 'uses', 'created', 'expires', 'link'];
  const actualKeys = isRecord(value) ? Object.keys(value).sort() : [];
  const sortedExpectedKeys = [...expectedKeys].sort();
  if (!isRecord(value) || actualKeys.length !== sortedExpectedKeys.length || actualKeys.some((key, index) => key !== sortedExpectedKeys[index])
      || value.actorId !== actorId || !isIdentifier(value.id) || value.purpose !== snapshot.purpose
      || value.issuanceSource !== 'Direct' || value.limit !== snapshot.count || value.uses !== 0) throw new TypeError('invalid issue response');
  const created = timestamp(value.created);
  const expires = timestamp(value.expires);
  if (expires <= created || typeof value.link !== 'string' || value.link.length < 1 || value.link.length > 8192
      || /[\u0000-\u0020\u007f-\u009f]/.test(value.link)) throw new TypeError('invalid issue link');
  const link = new URL(value.link);
  const inviteParams = new URLSearchParams(link.hash.slice(1));
  const inviteEntries = [...inviteParams.entries()];
  if (link.origin !== globalThis.location.origin || link.username || link.password || link.pathname !== '/join'
      || link.search !== '' || inviteEntries.length !== 1 || inviteEntries[0][0] !== 'invite' || inviteEntries[0][1].length < 1) throw new TypeError('invalid issue link');
  return Object.freeze({ id: value.id, created, expires, link: value.link });
}

async function issueInvitation(state, event) {
  event.preventDefault();
  if (!isCurrent(state) || !state.isAdmin || state.busy || state.unknown || state.readErrorLock) return;
  const snapshot = issueValues(state);
  if (snapshot === null || !element(state, 'issueConfirm').checked) {
    state.errorMessage = '目的（1〜500文字・制御文字不可）、利用上限、日数、確認欄を確認してください。';
    state.issueErrorMessage = state.errorMessage;
    render(state); return;
  }
  const actorId = state.actorId;
  const operation = ++state.operationGeneration;
  state.issueSnapshot = snapshot;
  clearIssueOutput(state);
  state.busy = true;
  state.errorMessage = '';
  state.statusMessage = '新しい招待を送信しています。';
  state.issueStatusMessage = state.statusMessage;
  state.issueErrorMessage = '';
  render(state);
  try {
    const csrfToken = await readCsrf(state);
    if (!issueActionCurrent(state, operation, snapshot, actorId)) return;
    state.unknown = { kind: 'issue', message: '招待発行の結果が未確認です。自動再送は行いません。読み取り専用の一覧を手動で再読込し、発行の有無を確認してください。' };
    render(state);
    const value = await requestJson(state, '/api/core/invitations/issue', {
      method: 'POST', actorId, csrfToken,
      body: { purpose: snapshot.purpose, count: snapshot.count, days: snapshot.days }
    });
    if (!issueActionCurrent(state, operation, snapshot, actorId)) return;
    const result = validateIssueResponse(value, actorId, snapshot);
    state.unknown = null;
    state.issuedLink = result.link;
    element(state, 'issueLink').value = result.link;
    element(state, 'issueLink').disabled = false;
    element(state, 'issueResult').hidden = false;
    element(state, 'issueConfirm').checked = false;
    state.issueSnapshot = null;
    state.statusMessage = `招待ID ${result.id} の発行をサーバーが確認しました。有効期限は ${formatTimestamp(result.expires)} です。リンクは一時欄に表示しています。`;
    state.issueStatusMessage = state.statusMessage;
    state.issueErrorMessage = '';
    state.errorMessage = '';
    render(state);
  } catch (error) {
    if (!isOperationCurrent(state, operation)) return;
    if (error instanceof AuthenticationStateError) clearActorState(state);
    else if (state.unknown?.kind === 'issue') {
      state.unknown.message = '招待発行の結果は未確認です。自動再送は行いません。状態を手動で再読込してください。読み取り一覧では新規招待の成否を完全には照合できないため、この画面の発行操作をロックしました。';
      resetIssueConfirmation(state, true);
      state.busy = false;
      state.operationGeneration++;
      state.statusMessage = '発行結果は未確認です。管理者の変更操作はロックされました。';
      state.issueStatusMessage = state.statusMessage;
      state.errorMessage = '';
      state.issueErrorMessage = '発行結果を確認できないため、自動再送せず、発行操作をロックしました。読み取り専用の一覧を再読込してください。';
      render(state);
    } else {
      resetIssueConfirmation(state, true);
      state.errorMessage = '招待発行を開始できませんでした。招待の書き込み要求は送信していません。';
      state.issueErrorMessage = state.errorMessage;
      state.statusMessage = '内容を確認してから明示的に再試行できます。';
      state.issueStatusMessage = state.statusMessage;
      render(state);
    }
  } finally {
    if (isOperationCurrent(state, operation)) { state.busy = false; render(state); }
  }
}

function selectIssueLink(state) {
  if (!isCurrent(state) || !state.isAdmin || !state.issuedLink) return;
  const input = element(state, 'issueLink');
  try { input.focus?.(); input.select?.(); } catch { }
  writeText(state, 'issueCopyStatus', 'リンクを選択しました。クリップボードが使えない場合は手動でコピーしてください。');
}
async function copyIssueLink(state) {
  if (!isCurrent(state) || !state.isAdmin || !state.issuedLink) return;
  const link = state.issuedLink;
  const actorId = state.actorId;
  const operation = state.operationGeneration;
  const clipboard = globalThis.navigator?.clipboard;
  if (!clipboard || typeof clipboard.writeText !== 'function') { selectIssueLink(state); return; }
  try {
    await clipboard.writeText(link);
    if (!isOperationCurrent(state, operation) || state.actorId !== actorId || state.issuedLink !== link) return;
    writeText(state, 'issueCopyStatus', 'リンクをクリップボードへコピーしました。安全に取り扱ってください。');
  } catch {
    if (!isOperationCurrent(state, operation) || state.actorId !== actorId || state.issuedLink !== link) return;
    selectIssueLink(state);
  }
}

function validateJoinLink(link) {
  if (typeof link !== 'string' || link.length < 1 || link.length > 8192 || /[\u0000-\u0020\u007f-\u009f]/.test(link)) throw new TypeError('invalid invitation link');
  const url = new URL(link);
  const inviteEntries = [...new URLSearchParams(url.hash.slice(1)).entries()];
  if (url.origin !== globalThis.location.origin || url.username || url.password || url.pathname !== '/join'
      || url.search !== '' || inviteEntries.length !== 1 || inviteEntries[0][0] !== 'invite'
      || inviteEntries[0][1].length < 1) throw new TypeError('invalid invitation link');
  return link;
}

function validateOwnerLinkResponse(value, actorId, invitation) {
  const expectedKeys = ['actorId', 'id', 'limit', 'uses', 'created', 'expires', 'link'];
  const keys = isRecord(value) ? Object.keys(value).sort() : [];
  const expected = [...expectedKeys].sort();
  if (!isRecord(value) || keys.length !== expected.length || keys.some((key, i) => key !== expected[i])
      || value.actorId !== actorId || value.id !== invitation.id
      || value.limit !== invitation.limit || value.uses !== invitation.uses) throw new TypeError('invalid owner link response');
  const created = timestamp(value.created);
  const expires = timestamp(value.expires);
  if (expires <= created) throw new TypeError('invalid invitation expiry');
  return { id: value.id, limit: value.limit, uses: value.uses, created, expires, link: validateJoinLink(value.link), copyStatus: '' };
}

async function retrieveOwnerLink(state, invitation) {
  if (!isCurrent(state) || !state.actorId || invitation.ownerId !== state.actorId || state.busy
      || state.unknown || state.readErrorLock || invitation.disabled) return;
  const actorId = state.actorId;
  const operation = ++state.operationGeneration;
  clearOwnerLink(state);
  state.busy = true;
  state.errorMessage = '';
  state.statusMessage = `招待ID ${invitation.id} の本人リンクを確認しています。`;
  render(state);
  try {
    const path = `/api/core/invitations/link/${encodeURIComponent(invitation.id)}`;
    const value = await requestJson(state, path, { actorId });
    if (!isOperationCurrent(state, operation) || state.actorId !== actorId) return;
    const result = validateOwnerLinkResponse(value, actorId, invitation);
    state.ownerLink = result;
    element(state, 'ownerLinkValue').value = result.link;
    element(state, 'ownerLinkValue').disabled = false;
    element(state, 'ownerLinkCopy').disabled = false;
    element(state, 'ownerLinkResult').hidden = false;
    writeText(state, 'ownerLinkSummary',
      `招待ID：${result.id} ／ 登録済み：${result.uses} / ${result.limit}名 ／ 有効期限：${formatTimestamp(result.expires)}`);
    state.statusMessage = '本人の招待リンクを一時表示しました。共有後はページを離れて消去してください。';
    state.errorMessage = '';
  } catch (error) {
    if (!isOperationCurrent(state, operation)) return;
    if (error instanceof AuthenticationStateError) clearActorState(state);
    else {
      clearOwnerLink(state);
      state.errorMessage = error instanceof HttpStatusError && error.status === 410
        ? 'この招待リンクは期限切れ・上限到達・失効済みです。'
        : '本人リンクを取得できませんでした。招待状態を再読込してください。';
      state.statusMessage = '招待リンクを表示していません。';
    }
  } finally {
    if (isOperationCurrent(state, operation)) { state.busy = false; render(state); }
  }
}

async function copyOwnerLink(state) {
  if (!isCurrent(state) || !state.ownerLink || element(state, 'ownerLinkValue').disabled) return;
  const link = state.ownerLink.link;
  const actorId = state.actorId;
  const operation = state.operationGeneration;
  const clipboard = globalThis.navigator?.clipboard;
  if (!clipboard || typeof clipboard.writeText !== 'function') {
    try { element(state, 'ownerLinkValue').focus?.(); element(state, 'ownerLinkValue').select?.(); } catch { }
    state.ownerLink.copyStatus = 'リンクを選択しました。クリップボードが使えない場合は手動でコピーしてください。';
    render(state);
    return;
  }
  try {
    await clipboard.writeText(link);
    if (!isOperationCurrent(state, operation) || state.actorId !== actorId || state.ownerLink?.link !== link) return;
    state.ownerLink.copyStatus = 'リンクをクリップボードへコピーしました。安全に取り扱ってください。';
  } catch {
    if (!isOperationCurrent(state, operation) || state.actorId !== actorId || state.ownerLink?.link !== link) return;
    try { element(state, 'ownerLinkValue').focus?.(); element(state, 'ownerLinkValue').select?.(); } catch { }
    state.ownerLink.copyStatus = 'リンクを選択しました。手動でコピーしてください。';
  }
  render(state);
}

function requestActionCurrent(state, operation, actorId, snapshot) {
  if (!isOperationCurrent(state, operation) || state.actorId !== actorId || state.isAdmin) return false;
  const current = requestValues(state);
  return current !== null && current.reason === snapshot.reason && current.count === snapshot.count
    && current.days === snapshot.days;
}

async function createInvitationRequest(state, event) {
  event.preventDefault();
  if (!isCurrent(state) || !state.actorId || state.isAdmin || state.busy || state.unknown || state.readErrorLock) return;
  const snapshot = requestValues(state);
  if (snapshot === null) {
    state.requestErrorMessage = '依頼理由（1〜500文字・制御文字不可）、人数、期間を確認してください。';
    render(state); return;
  }
  const actorId = state.actorId;
  const operation = ++state.operationGeneration;
  state.busy = true;
  state.requestErrorMessage = '';
  state.requestStatusMessage = '招待リンク発行の依頼を送信しています。';
  render(state);
  try {
    const csrfToken = await readCsrf(state);
    if (!requestActionCurrent(state, operation, actorId, snapshot)) return;
    state.unknown = { kind: 'request', message: '依頼の送信結果は未確認です。自動再送しません。依頼一覧を再読込し、同じ依頼を繰り返し送信しないでください。' };
    render(state);
    const value = await requestJson(state, '/api/core/invitations/request', {
      method: 'POST', actorId, csrfToken, body: snapshot
    });
    if (!requestActionCurrent(state, operation, actorId, snapshot)) return;
    const keys = isRecord(value) ? Object.keys(value).sort() : [];
    if (keys.join(',') !== 'actorId,id,status' || value.actorId !== actorId
        || !isIdentifier(value.id) || value.status !== 'Pending') throw new TypeError('invalid request response');
    state.unknown = null;
    element(state, 'requestReason').value = '';
    element(state, 'requestCount').value = '1';
    element(state, 'requestDays').value = '1';
    state.busy = false;
    state.operationGeneration++;
    state.requestStatusMessage = `依頼 ${value.id} を受け付けました。管理者の判断は依頼一覧で確認できます。`;
    state.requestErrorMessage = '';
    render(state);
    void loadPage(state, 1);
  } catch (error) {
    if (!isOperationCurrent(state, operation)) return;
    if (error instanceof AuthenticationStateError) clearActorState(state);
    else if (state.unknown?.kind === 'request') {
      state.unknown.message = '依頼の結果は未確認です。自動再送しません。依頼一覧を再読込して確認してください。結果確認まで新しい依頼をロックしています。';
      state.busy = false;
      state.operationGeneration++;
      state.requestStatusMessage = '依頼結果は未確認です。';
      state.requestErrorMessage = state.unknown.message;
      render(state);
    } else {
      state.requestErrorMessage = '依頼を開始できませんでした。依頼内容を確認してください。';
      state.requestStatusMessage = '依頼は送信されていません。';
      render(state);
    }
  } finally {
    if (isOperationCurrent(state, operation)) { state.busy = false; render(state); }
  }
}

function decisionActionCurrent(state, operation, actorId, request) {
  return isOperationCurrent(state, operation) && state.actorId === actorId && state.isAdmin
    && state.selectedRequest === request
    && state.requests.some(row => row.id === request.id && row.status === 'Pending');
}

async function decideInvitationRequest(state, event) {
  event.preventDefault();
  const request = state.selectedRequest;
  if (!isCurrent(state) || !state.isAdmin || !request || state.busy || state.unknown || state.readErrorLock) return;
  const rawComment = element(state, 'reviewComment').value;
  const comment = normalizedDecisionComment(rawComment);
  if (!validOptionalDecisionComment(rawComment)) {
    state.reviewErrorMessage = '判断コメントは500文字以内、制御文字不可で入力してください。';
    render(state); return;
  }
  if (!element(state, 'reviewConfirm').checked) return;
  const actorId = state.actorId;
  const approve = element(state, 'reviewChoice').value === 'approve';
  const operation = ++state.operationGeneration;
  state.busy = true;
  state.reviewErrorMessage = '';
  state.reviewStatusMessage = `依頼 ${request.id} の判断を送信しています。`;
  render(state);
  try {
    const csrfToken = await readCsrf(state);
    if (!decisionActionCurrent(state, operation, actorId, request)) return;
    state.unknown = { kind: 'decision', id: request.id, message: `依頼 ${request.id} の判断結果は未確認です。自動再送しません。依頼一覧を再読込して状態を確認してください。` };
    render(state);
    const value = await requestJson(state, '/api/core/invitations/decide', {
      method: 'POST', actorId, csrfToken,
      body: { id: request.id, approve, comment: comment ?? '' }
    });
    if (!decisionActionCurrent(state, operation, actorId, request)) return;
    const keys = isRecord(value) ? Object.keys(value).sort() : [];
    if (keys.join(',') !== 'actorId,decisionComment,id,invitationId,status'
        || value.actorId !== actorId || value.id !== request.id
        || value.status !== (approve ? 'Approved' : 'Rejected')
        || value.decisionComment !== comment
        || (approve ? !isIdentifier(value.invitationId) : value.invitationId !== null)) throw new TypeError('invalid decision response');
    state.unknown = null;
    state.requests = state.requests.map(row => row.id === request.id
      ? { ...row, status: value.status, decidedAt: Math.floor(Date.now() / 1000), decidedBy: actorId, deciderName: '本人', decisionComment: comment }
      : row);
    state.selectedRequest = null;
    element(state, 'reviewComment').value = '';
    element(state, 'reviewConfirm').checked = false;
    state.busy = false;
    state.operationGeneration++;
    state.reviewStatusMessage = approve
      ? '依頼を許可し、招待リンクを発行しました。リンクは依頼者本人だけが取得できます。'
      : '依頼を拒否しました。依頼者へ通知しました。';
    state.reviewErrorMessage = '';
    render(state);
    void loadPage(state, state.page || 1);
  } catch (error) {
    if (!isOperationCurrent(state, operation)) return;
    if (error instanceof AuthenticationStateError) clearActorState(state);
    else if (state.unknown?.kind === 'decision') {
      state.unknown.message = `依頼 ${request.id} の結果は未確認です。自動再送せず、一覧を再読込して判断状態を確認してください。`;
      state.busy = false;
      state.operationGeneration++;
      state.reviewStatusMessage = '判断結果は未確認です。';
      state.reviewErrorMessage = state.unknown.message;
      render(state);
    } else {
      state.reviewErrorMessage = '判断を開始できませんでした。依頼を再読込して状態を確認してください。';
      state.reviewStatusMessage = '判断は送信されていません。';
      render(state);
    }
  } finally {
    if (isOperationCurrent(state, operation)) { state.busy = false; render(state); }
  }
}

async function revokeSelected(state, event) {
  event.preventDefault();
  if (!isCurrent(state) || !state.isAdmin || state.busy || state.unknown || state.readErrorLock || !state.selectedTarget) return;
  const target = state.selectedTarget;
  const operation = state.operationGeneration;
  const actorId = state.actorId;
  const reason = normalizedReason(element(state, 'reason').value);
  if (!isIdentifier(target.id) || reason === null || !element(state, 'confirm').checked) {
    state.errorMessage = '対象ID、理由（1〜500文字・制御文字不可）、確認欄を確認してください。';
    render(state); return;
  }
  state.busy = true;
  state.errorMessage = '';
  state.statusMessage = `招待ID ${target.id} の失効を送信しています。`;
  render(state);
  try {
    const csrfToken = await readCsrf(state);
    if (!actionCurrent(state, operation, target, actorId)) return;
    state.unknown = { kind: 'revoke', id: target.id, message: `招待ID ${target.id} の失効結果を確認できません。操作を自動再送しません。状態を手動で再読込してください。` };
    render(state);
    const value = await requestJson(state, '/api/core/invitations/revoke', {
      method: 'POST', actorId, csrfToken,
      body: { id: target.id, reason, explicitlyConfirmed: true }
    });
    if (!actionCurrent(state, operation, target, actorId)) return;
    if (!isRecord(value) || value.actorId !== actorId || value.id !== target.id || value.disabled !== true) throw new TypeError('invalid revoke response');
    state.unknown = null;
    state.invitations = state.invitations.map(row => row.id === target.id ? { ...row, disabled: true } : row);
    clearConfirmation(state);
    state.busy = false;
    state.operationGeneration++;
    state.statusMessage = `招待ID ${target.id} の失効をサーバーが確認しました。確認欄は解除済みです。`;
    state.errorMessage = '';
    render(state);
  } catch (error) {
    if (!isOperationCurrent(state, operation)) return;
    if (error instanceof AuthenticationStateError) clearActorState(state);
    else if (state.unknown) {
      state.unknown.message = `招待ID ${state.unknown.id} の失効結果は未確認です。自動再送は行いません。状態を手動で再読込し、一覧に対象が現れる場合だけ照合してください。`;
      clearConfirmation(state);
      state.busy = false;
      state.operationGeneration++;
      state.statusMessage = '失効結果は未確認です。管理者操作はロックされました。';
      state.errorMessage = '';
      render(state);
    } else {
      state.errorMessage = '失効操作を開始できませんでした。サーバーへの書き込みは送信していません。';
      state.statusMessage = '確認してから明示的に再試行できます。';
      render(state);
    }
  } finally {
    if (isOperationCurrent(state, operation)) { state.busy = false; render(state); }
  }
}

function abortRequests(state) {
  for (const controller of state.controllers) { try { controller.abort(); } catch { } }
}

function onPageHide(state) {
  if (liveState !== state || state.disposed || state.pageHidden || state.generation !== nextGeneration) return;
  state.operationGeneration++;
  state.actorId = null;
  state.isAdmin = false;
  state.page = 1;
  state.hasMoreInvitations = false;
  state.hasMoreRequests = false;
  state.invitations = [];
  state.requests = [];
  state.selectedRequest = null;
  state.busy = false;
  state.readErrorLock = true;
  state.selectedTarget = null;
  state.statusMessage = 'ページを離れたため、表示中の招待リンクとアカウント情報を消去しました。復帰時に読み取り専用で状態を再確認します。';
  state.issueStatusMessage = 'ページを離れたため、一時招待リンクを消去しました。復帰後は読み取り専用の再確認が必要です。';
  state.errorMessage = '';
  state.issueErrorMessage = '';
  clearConfirmation(state);
  element(state, 'requestReason').value = '';
  element(state, 'reviewComment').value = '';
  element(state, 'reviewConfirm').checked = false;
  clearOwnerLink(state);
  resetIssueConfirmation(state, true);
  render(state);
  state.pageHidden = true;
  abortRequests(state);
}

function onPageShow(state) {
  if (!state.pageHidden || liveState !== state || state.disposed || state.generation !== nextGeneration || state.root?.isConnected === false) return;
  state.pageHidden = false;
  state.operationGeneration++;
  state.actorId = null;
  state.isAdmin = false;
  state.page = 1;
  state.hasMoreInvitations = false;
  state.hasMoreRequests = false;
  state.invitations = [];
  state.requests = [];
  state.selectedRequest = null;
  state.busy = false;
  state.readErrorLock = true;
  state.selectedTarget = null;
  state.statusMessage = 'ページへ復帰しました。変更操作の前に読み取り専用でアカウントと一覧を再確認しています。';
  state.issueStatusMessage = 'ページへ復帰しました。一時リンクと確認状態は復元せず、読み取り専用で再確認します。';
  state.errorMessage = '';
  state.issueErrorMessage = '';
  clearConfirmation(state);
  element(state, 'requestReason').value = '';
  element(state, 'reviewComment').value = '';
  element(state, 'reviewConfirm').checked = false;
  clearOwnerLink(state);
  resetIssueConfirmation(state, true);
  render(state);
  void loadPage(state, 1);
}

function clearActorState(state) {
  state.operationGeneration++;
  state.actorId = null;
  state.isAdmin = false;
  state.invitations = [];
  state.requests = [];
  state.selectedRequest = null;
  state.unknown = null;
  state.readErrorLock = false;
  state.busy = false;
  state.statusMessage = 'ログイン状態またはアカウントが変わりました。操作を停止しました。ページを再読み込みしてください。';
  state.errorMessage = '';
  state.issueStatusMessage = 'ログイン状態またはアカウントが変わりました。発行操作を停止し、一時リンクを消去しました。';
  state.issueErrorMessage = '';
  clearConfirmation(state);
  element(state, 'requestReason').value = '';
  element(state, 'reviewComment').value = '';
  element(state, 'reviewConfirm').checked = false;
  clearOwnerLink(state);
  resetIssueConfirmation(state, true);
  render(state);
}

function disposeState(state) {
  if (!state || state.disposed) return;
  state.disposed = true;
  if (state.root && state.listeners) {
    state.root.removeEventListener('click', state.listeners.click);
    state.root.removeEventListener('submit', state.listeners.submit);
    element(state, 'reason')?.removeEventListener('input', state.listeners.reasonInput);
    element(state, 'confirm')?.removeEventListener('change', state.listeners.confirmChange);
    for (const name of ['issuePurpose', 'issueCount', 'issueDays']) element(state, name)?.removeEventListener('input', state.listeners.issueInput);
    element(state, 'issueConfirm')?.removeEventListener('change', state.listeners.issueConfirmChange);
    for (const name of ['requestReason', 'requestCount', 'requestDays']) element(state, name)?.removeEventListener('input', state.listeners.requestInput);
    element(state, 'reviewChoice')?.removeEventListener('change', state.listeners.reviewInput);
    element(state, 'reviewComment')?.removeEventListener('input', state.listeners.reviewInput);
    element(state, 'reviewConfirm')?.removeEventListener('change', state.listeners.reviewConfirmChange);
  }
  globalThis.removeEventListener?.('pagehide', state.listeners?.pageHide);
  globalThis.removeEventListener?.('pageshow', state.listeners?.pageShow);
  abortRequests(state);
  state.controllers.clear();
  resetIssueConfirmation(state, true);
  clearOwnerLink(state);
  state.actorId = null;
  state.isAdmin = false;
  state.invitations = [];
  state.requests = [];
  state.selectedTarget = null;
  state.unknown = null;
  state.elements = Object.create(null);
  if (liveState === state) { liveState = null; nextGeneration++; }
}

export async function init(root, owner) {
  if (!root || typeof root.querySelector !== 'function' || typeof root.addEventListener !== 'function' || typeof owner !== 'string' || owner.length < 1) throw new TypeError('root and owner required');
  if (liveState?.owner === owner && liveState.root === root && !liveState.disposed) return;
  if (liveState) disposeState(liveState);
  const state = {
    root, owner, generation: ++nextGeneration, operationGeneration: 0, disposed: false, pageHidden: false, controllers: new Set(),
    elements: elements(root), listeners: null, actorId: null, isAdmin: false, page: 1, pageSize: PAGE_SIZE,
    hasMoreInvitations: false, hasMoreRequests: false, invitations: [], requests: [], selectedTarget: null, selectedRequest: null,
    unknown: null, readErrorLock: false, busy: false, errorMessage: '', statusMessage: '一覧を準備しています。',
    issueSnapshot: null, issuedLink: null, ownerLink: null,
    issueStatusMessage: '発行機能を準備しています。', issueErrorMessage: '',
    requestStatusMessage: '依頼機能を準備しています。', requestErrorMessage: '',
    reviewStatusMessage: '', reviewErrorMessage: ''
  };
  if (Object.values(state.elements).some(node => !node)) throw new TypeError('invitation UI elements missing');
  liveState = state;
  state.listeners = {
    click: event => {
      const target = event.target;
      if (target === element(state, 'refresh')) void loadPage(state, state.page || 1);
      else if (target === element(state, 'previous')) void loadPage(state, state.page - 1);
      else if (target === element(state, 'next')) void loadPage(state, state.page + 1);
      else if (target === element(state, 'issueCopy')) void copyIssueLink(state);
      else if (target === element(state, 'ownerLinkCopy')) void copyOwnerLink(state);
      else if (target === element(state, 'reviewCancel')) {
        state.selectedRequest = null;
        element(state, 'reviewComment').value = '';
        element(state, 'reviewConfirm').checked = false;
        state.reviewErrorMessage = '';
        render(state);
      }
    },
    submit: event => {
      if (event.target === element(state, 'form')) void revokeSelected(state, event);
      else if (event.target === element(state, 'issueForm')) void issueInvitation(state, event);
      else if (event.target === element(state, 'requestForm')) void createInvitationRequest(state, event);
      else if (event.target === element(state, 'reviewForm')) void decideInvitationRequest(state, event);
    },
    reasonInput: () => {
      if (!isCurrent(state)) return;
      element(state, 'confirm').checked = false;
      state.statusMessage = '理由が変更されました。対象と理由を確認し、確認欄に再度チェックしてください。';
      updateConfirmation(state); writeText(state, 'status', state.statusMessage);
    },
    confirmChange: () => { if (isCurrent(state)) updateConfirmation(state); },
    issueInput: () => {
      if (!isCurrent(state)) return;
      state.operationGeneration++;
      resetIssueConfirmation(state, true);
      state.errorMessage = '';
      state.statusMessage = '発行内容が変更されました。内容を確認して確認欄に再度チェックしてください。';
      state.issueStatusMessage = state.statusMessage;
      state.issueErrorMessage = '';
      render(state);
    },
    issueConfirmChange: () => { if (isCurrent(state)) updateConfirmation(state); },
    requestInput: () => {
      if (!isCurrent(state)) return;
      state.operationGeneration++;
      state.requestErrorMessage = '';
      updateConfirmation(state);
    },
    reviewInput: () => {
      if (!isCurrent(state)) return;
      state.operationGeneration++;
      element(state, 'reviewConfirm').checked = false;
      state.reviewErrorMessage = '';
      updateConfirmation(state);
    },
    reviewConfirmChange: () => { if (isCurrent(state)) updateConfirmation(state); },
    pageHide: () => onPageHide(state),
    pageShow: () => onPageShow(state)
  };
  root.addEventListener('click', state.listeners.click);
  root.addEventListener('submit', state.listeners.submit);
  element(state, 'reason').addEventListener('input', state.listeners.reasonInput);
  element(state, 'confirm').addEventListener('change', state.listeners.confirmChange);
  for (const name of ['issuePurpose', 'issueCount', 'issueDays']) element(state, name).addEventListener('input', state.listeners.issueInput);
  element(state, 'issueConfirm').addEventListener('change', state.listeners.issueConfirmChange);
  for (const name of ['requestReason', 'requestCount', 'requestDays']) element(state, name).addEventListener('input', state.listeners.requestInput);
  element(state, 'reviewChoice').addEventListener('change', state.listeners.reviewInput);
  element(state, 'reviewComment').addEventListener('input', state.listeners.reviewInput);
  element(state, 'reviewConfirm').addEventListener('change', state.listeners.reviewConfirmChange);
  globalThis.addEventListener?.('pagehide', state.listeners.pageHide);
  globalThis.addEventListener?.('pageshow', state.listeners.pageShow);
  render(state);
  await loadPage(state, 1);
}

export function cleanup(owner) {
  if (liveState?.owner === owner) disposeState(liveState);
}
