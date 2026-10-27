'use strict';
// 採用mockの最終account管理hubと旧入口。読取だけを接続し、認証変更を模擬成功にしない。
(()=>{
 function valid(v){return !!v&&Object.keys(v).sort().join(',')==='actorId,email,passkeys,siteName'&&['actorId','email','siteName'].every(k=>typeof v[k]==='string')&&Array.isArray(v.passkeys)&&v.passkeys.length<=512&&v.passkeys.every(k=>k&&Object.keys(k).sort().join(',')==='createdAt,name'&&typeof k.name==='string'&&(k.createdAt===null||typeof k.createdAt==='string'&&Number.isFinite(Date.parse(k.createdAt))));}
 function content(v,{esc,dateText},route){const hub=route==='#/account/manage',email=route==='#/account/email',title=email?'メールアドレス変更':hub?'アカウント管理':'パスキー管理';
  const scope='<div id="account-scope" class="notice preview-notice">本人の保存済みアカウント情報を参照しています。メール変更・パスキー追加／削除はまだ接続していません。メール送信や認証情報の変更は行いません。</div>';
  const mail=hub?`<section class="account-email-summary"><h2>メールアドレス</h2><p id="account-email">${esc(v.email)}</p><a id="manage-email" href="#/account/email">メールアドレスを変更</a><h2>パスキー</h2></section>`:'';
  const keys=`<p>複数の端末を登録しておくと、端末紛失時に別のパスキーを使えます。</p><div class="table-wrap"><table><thead><tr><th>パスキー名</th><th>登録日時</th><th>操作</th></tr></thead><tbody>${v.passkeys.map(k=>`<tr><td>${esc(k.name||'名前未設定')}</td><td>${k.createdAt===null?'記録なし':esc(dateText(k.createdAt))}</td><td><button disabled aria-describedby="account-scope">削除</button></td></tr>`).join('')}</tbody></table></div>${v.passkeys.length?'':'<p class="muted">登録済みパスキーはありません。</p>'}<p class="tiny muted">最後の有効なパスキーは削除できません。この段階版ではすべての追加・削除操作が未接続です。</p><button id="add-passkey" class="primary" disabled aria-describedby="account-scope">パスキーを追加</button>${hub?'':'<p><a href="#/account/email">メールアドレス変更の画面</a></p>'}<details><summary>運用者の緊急復旧</summary><p>最後のサービス管理者も全パスキーを紛失した場合は、ホスト管理権限を持つ運用者の限定手順を利用します。公開画面からの認証迂回は提供しません。</p></details>`;
  const change='<p>既存パスキーの再認証→新アドレス確認→変更完了。旧アドレスにも通知します。復旧とは別の操作です。</p><label data-field-extra="" data-explicit-required="false"><span class="compact-caption">新しいメールアドレス<span class="field-status">任意</span></span><input id="new-email" type="email" disabled placeholder="未接続"></label><button id="email-preview" disabled aria-describedby="account-scope">変更フローは未接続</button>';
  return `<div class="preview-page"><div class="breadcrumb"><a href="#/">イベント一覧</a> / ${title}</div><section class="panel${hub?' account-management-hub':''}">${scope}<h1>${title}</h1>${mail}${email?change:keys}</section></div>`;
 }
 window.SemAccountView={valid,content};
})();
