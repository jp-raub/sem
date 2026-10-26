// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;

/// <summary>Only server-normalized PNG bytes are stored; source filenames and metadata are discarded.</summary>
public sealed class EventHeaderImage
{
    public string EventId { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
}
