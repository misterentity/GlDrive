using System.Text.Json.Serialization;

namespace GlDrive.AiAgent;

public abstract record TelemetryEnvelope
{
    [JsonPropertyName("ts")] public string Ts { get; init; } = DateTime.UtcNow.ToString("O");
    [JsonPropertyName("v")]  public int V   { get; init; } = 1;
}

public record RaceParticipant(
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("role")]     string Role,        // "src" | "dst"
    [property: JsonPropertyName("bytes")]    long Bytes,
    [property: JsonPropertyName("files")]    int Files,
    [property: JsonPropertyName("avgKbps")]  double AvgKbps,
    [property: JsonPropertyName("abortReason")] string? AbortReason);

public record RaceOutcomeEvent : TelemetryEnvelope
{
    [JsonPropertyName("raceId")]     public string RaceId { get; init; } = "";
    [JsonPropertyName("section")]    public string Section { get; init; } = "";
    [JsonPropertyName("release")]    public string Release { get; init; } = "";
    [JsonPropertyName("startedAt")]  public string StartedAt { get; init; } = "";
    [JsonPropertyName("endedAt")]    public string EndedAt { get; init; } = "";
    [JsonPropertyName("participants")] public List<RaceParticipant> Participants { get; init; } = [];
    [JsonPropertyName("winner")]     public string? Winner { get; init; }
    [JsonPropertyName("fxpMode")]    public string FxpMode { get; init; } = "";
    [JsonPropertyName("scoreBreakdown")] public Dictionary<string, int> ScoreBreakdown { get; init; } = new();
    [JsonPropertyName("result")]     public string Result { get; init; } = "";  // complete|aborted|blacklisted
    [JsonPropertyName("filesExpected")] public int FilesExpected { get; init; }
    [JsonPropertyName("filesTotal")]    public int FilesTotal { get; init; }
}

public record NukeDetectedEvent : TelemetryEnvelope
{
    [JsonPropertyName("serverId")] public string ServerId { get; init; } = "";
    [JsonPropertyName("section")]  public string Section { get; init; } = "";
    [JsonPropertyName("release")]  public string Release { get; init; } = "";
    [JsonPropertyName("nukedAt")]  public string NukedAt { get; init; } = "";
    [JsonPropertyName("nuker")]    public string Nuker { get; init; } = "";
    [JsonPropertyName("reason")]   public string Reason { get; init; } = "";
    [JsonPropertyName("multiplier")] public int Multiplier { get; init; }
    [JsonPropertyName("ourRaceRef")] public string? OurRaceRef { get; init; }
}

public record SiteHealthEvent : TelemetryEnvelope
{
    [JsonPropertyName("serverId")]         public string ServerId { get; init; } = "";
    [JsonPropertyName("windowStart")]      public string WindowStart { get; init; } = "";
    [JsonPropertyName("windowEnd")]        public string WindowEnd { get; init; } = "";
    [JsonPropertyName("avgConnectMs")]     public double AvgConnectMs { get; init; }
    [JsonPropertyName("p99ConnectMs")]     public double P99ConnectMs { get; init; }
    [JsonPropertyName("disconnects")]      public int Disconnects { get; init; }
    [JsonPropertyName("tlsHandshakeMs")]   public double TlsHandshakeMs { get; init; }
    [JsonPropertyName("poolExhaustCount")] public int PoolExhaustCount { get; init; }
    [JsonPropertyName("ghostKills")]       public int GhostKills { get; init; }
    [JsonPropertyName("errors5xx")]        public int Errors5xx { get; init; }
    [JsonPropertyName("reinitCount")]      public int ReinitCount { get; init; }
}

public record AnnounceNoMatchEvent : TelemetryEnvelope
{
    [JsonPropertyName("serverId")]             public string ServerId { get; init; } = "";
    [JsonPropertyName("channel")]              public string Channel { get; init; } = "";
    [JsonPropertyName("botNick")]              public string BotNick { get; init; } = "";
    [JsonPropertyName("message")]              public string Message { get; init; } = "";
    [JsonPropertyName("nearestRulePattern")]   public string? NearestRulePattern { get; init; }
    [JsonPropertyName("nearestRuleDistance")]  public int? NearestRuleDistance { get; init; }
}

public record WishlistAttemptEvent : TelemetryEnvelope
{
    [JsonPropertyName("wishlistItemId")] public string WishlistItemId { get; init; } = "";
    [JsonPropertyName("release")]        public string Release { get; init; } = "";
    [JsonPropertyName("score")]          public double Score { get; init; }
    [JsonPropertyName("matched")]        public bool Matched { get; init; }
    [JsonPropertyName("missReason")]     public string? MissReason { get; init; }
    [JsonPropertyName("section")]        public string Section { get; init; } = "";
    [JsonPropertyName("serverId")]       public string ServerId { get; init; } = "";
}

public record ConfigOverrideEvent : TelemetryEnvelope
{
    [JsonPropertyName("jsonPointer")]  public string JsonPointer { get; init; } = "";
    [JsonPropertyName("beforeValue")]  public string? BeforeValue { get; init; }
    [JsonPropertyName("afterValue")]   public string? AfterValue { get; init; }
    [JsonPropertyName("aiAuditRef")]   public string? AiAuditRef { get; init; }
}

public record DownloadOutcomeEvent : TelemetryEnvelope
{
    [JsonPropertyName("downloadId")]   public string DownloadId { get; init; } = "";
    [JsonPropertyName("serverId")]     public string ServerId { get; init; } = "";
    [JsonPropertyName("remotePath")]   public string RemotePath { get; init; } = "";
    [JsonPropertyName("result")]       public string Result { get; init; } = ""; // complete|failed|retried|resumed
    [JsonPropertyName("bytes")]        public long Bytes { get; init; }
    [JsonPropertyName("elapsedMs")]    public long ElapsedMs { get; init; }
    [JsonPropertyName("retryCount")]   public int RetryCount { get; init; }
    [JsonPropertyName("failureClass")] public string? FailureClass { get; init; }
}

public record FileTransferEvent : TelemetryEnvelope
{
    [JsonPropertyName("raceId")]        public string RaceId { get; init; } = "";
    [JsonPropertyName("srcServer")]     public string SrcServer { get; init; } = "";
    [JsonPropertyName("dstServer")]     public string DstServer { get; init; } = "";
    [JsonPropertyName("file")]          public string File { get; init; } = "";
    [JsonPropertyName("bytes")]         public long Bytes { get; init; }
    [JsonPropertyName("elapsedMs")]     public long ElapsedMs { get; init; }
    [JsonPropertyName("ttfbMs")]        public long TtfbMs { get; init; }
    [JsonPropertyName("pasvLatencyMs")] public long PasvLatencyMs { get; init; }
    [JsonPropertyName("abortReason")]   public string? AbortReason { get; init; }
}

public record SectionActivityEvent : TelemetryEnvelope
{
    [JsonPropertyName("serverId")]  public string ServerId { get; init; } = "";
    [JsonPropertyName("section")]   public string Section { get; init; } = "";
    [JsonPropertyName("filesIn")]   public int FilesIn { get; init; }
    [JsonPropertyName("bytesIn")]   public long BytesIn { get; init; }
    [JsonPropertyName("ourRaces")]  public int OurRaces { get; init; }
    [JsonPropertyName("ourWins")]   public int OurWins { get; init; }
    [JsonPropertyName("dayOfWeek")] public int DayOfWeek { get; init; }
}

public record ErrorSignatureEvent : TelemetryEnvelope
{
    [JsonPropertyName("component")]         public string Component { get; init; } = "";
    [JsonPropertyName("exceptionType")]     public string ExceptionType { get; init; } = "";
    [JsonPropertyName("normalizedMessage")] public string NormalizedMessage { get; init; } = "";
    [JsonPropertyName("stackTopFrame")]     public string StackTopFrame { get; init; } = "";
    [JsonPropertyName("count")]             public int Count { get; init; }
    [JsonPropertyName("firstAt")]           public string FirstAt { get; init; } = "";
    [JsonPropertyName("lastAt")]            public string LastAt { get; init; } = "";
}

public enum TelemetryStream
{
    Races, Nukes, SiteHealth, AnnouncesNoMatch, WishlistAttempts,
    Overrides, Downloads, Transfers, SectionActivity, Errors
}
