// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
namespace Sem.Web.Core;

// Immutable receipt only: no raw CSV, assignment values or client-provided row IDs.
public sealed class EventImportOperation
{
    public string Id { get; set; } = "";
    public string EventId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public long ResultVersion { get; set; }
    public int NewSongs { get; set; }
    public int NewTransitions { get; set; }
    public int SkippedRows { get; set; }
    public long AcceptedAt { get; set; }
}
