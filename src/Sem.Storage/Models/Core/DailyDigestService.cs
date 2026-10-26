// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
namespace Sem.Web.Core;
public sealed class DailyDigestRun {
 public string Day{get;set;}="";public long CreatedAt{get;set;}
}

public sealed class DailyDigestMail {
 public string Id{get;set;}="";public string Day{get;set;}="";public string UserId{get;set;}="";
 public long Due{get;set;}public string Status{get;set;}="Pending";public int Attempts{get;set;}
 public string?Claim{get;set;}public long?LeaseUntil{get;set;}public long?SentAt{get;set;}public string?Error{get;set;}
}
