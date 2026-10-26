// 保全済み旧mainからモデル宣言のみを抽出。サービス・画面・起動処理は含めない。

namespace Sem.Web.Core;

public sealed class EventShorteningPolicy {
    public string EventId { get; set; } = "";
    public string PartId { get; set; } = "";
    public string Scope { get; set; } = "Plan";
    public bool AllowPrep { get; set; }
    public int MinPrepSeconds { get; set; }
    public bool AllowDuration { get; set; }
    public int MinDurationSeconds { get; set; }
}

public sealed class ShorteningOperation {
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string EventId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string At { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string ResultJson { get; set; } = "{}";
}