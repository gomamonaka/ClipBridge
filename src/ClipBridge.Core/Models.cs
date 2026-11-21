namespace ClipBridge.Core;

public enum ClipboardKind { Text, Image }

public sealed record ClipboardEntry(
    string Id, ClipboardKind Kind, string? Text, byte[]? ImagePng,
    DateTimeOffset CreatedUtc, string DeviceId, string DeviceName);

public sealed record Snippet(string Id, string Title, string Text, DateTimeOffset UpdatedUtc, string Group = "共通",
    string? GroupId = null, long SortOrder = long.MaxValue);

public sealed record SnippetGroup(string Id, string Name, long SortOrder, DateTimeOffset UpdatedUtc);

public sealed record ClipBridgeSnapshot(int Version, DateTimeOffset CreatedUtc,
    IReadOnlyList<SnippetGroup> Groups, IReadOnlyList<Snippet> Snippets, IReadOnlyList<ClipboardEntry> History);

public sealed record ImportResult(int Groups, int Snippets, int History);

public sealed record SyncStatus(bool IsOnline, string Message, DateTimeOffset UpdatedUtc, int PendingCount);

public sealed class InvalidPassphraseException : Exception
{
    public InvalidPassphraseException() : base("共有フォルダーの合言葉が一致しません。") { }
}

internal sealed record GroupManifest(int Version, string GroupId, string Salt, int Iterations, string Verifier);
internal sealed record SyncOperation(
    string Id, string Operation, DateTimeOffset CreatedUtc, string DeviceId, string DeviceName,
    ClipboardKind? Kind = null, string? Text = null, byte[]? ImagePng = null,
    string? SnippetId = null, string? Title = null, string? TargetId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Group = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? GroupId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? SortOrder = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? HistoryUtc = null);
internal sealed record SnippetVersion(SyncOperation Operation);
internal sealed record HistoryVersion(string Id, DateTimeOffset OperationUtc, string OperationId);
internal sealed record SavedState(
    int Version, string GroupId, List<ClipboardEntry> History,
    List<SyncOperation> Snippets, List<SyncOperation> Pending,
    List<string> Seen, string? LastFingerprint, DateTimeOffset LastClipboardUtc,
    string LastClipboardId, DateTimeOffset HistoryClearedUtc, List<string> DeletedHistory,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<SyncOperation>? Groups = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<SyncOperation>? Orders = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<HistoryVersion>? HistoryVersions = null);
