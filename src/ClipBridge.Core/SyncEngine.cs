using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ClipBridge.Core;

/// <summary>Encrypted immutable-operation synchronization through an ordinary shared folder.</summary>
public sealed partial class SyncEngine : IAsyncDisposable
{
    public const int MaxTextBytes = 1024 * 1024;
    public const int MaxImageBytes = 8 * 1024 * 1024;
    public const int MaxHistoryEntries = 2000;
    public const int MaxSnippets = 5000;
    public const int MaxGroups = 256;
    public static readonly TimeSpan SharedClipboardRetention = TimeSpan.FromDays(7);
    private const long MaxHistoryBytes = 96L * 1024 * 1024;
    private const long MaxPendingBytes = 96L * 1024 * 1024;
    private const int MaxPendingOperations = 8192;
    private const int MaxCacheBytes = 256 * 1024 * 1024;
    private const string ManifestName = "clipbridge.group.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _viewLock = new();
    private readonly List<ClipboardEntry> _history = [];
    private readonly Dictionary<string, SyncOperation> _snippets = [];
    private readonly Dictionary<string, SyncOperation> _groups = [];
    private readonly Dictionary<string, SyncOperation> _orders = [];
    private readonly Dictionary<string, HistoryVersion> _historyVersions = [];
    private readonly List<SyncOperation> _pending = [];
    private readonly HashSet<string> _seen = [];
    private readonly HashSet<string> _deletedHistory = [];
    private readonly Dictionary<string, (long Length, long Modified)> _badFiles = [];
    private readonly string _sharedFolder, _localFolder, _deviceId, _deviceName;
    private readonly DateTimeOffset _openedUtc = DateTimeOffset.UtcNow;
    private byte[] _key = [];
    private GroupManifest _manifest = null!;
    private string? _lastFingerprint;
    private DateTimeOffset _lastClipboardUtc, _historyClearedUtc;
    private DateTimeOffset _lastOperationUtc, _lastPruneUtc;
    private string _lastClipboardId = "";
    private bool _disposed;
    // Kept internal so the same persistence budget can be exercised without allocating hundreds of megabytes in tests.
    internal int CacheByteLimit { get; set; } = MaxCacheBytes;
    private SyncStatus _status = new(false, "接続を準備しています…", DateTimeOffset.UtcNow, 0);

    private SyncEngine(string sharedFolder, string localFolder, string deviceId, string deviceName)
    {
        _sharedFolder = Path.GetFullPath(sharedFolder);
        _localFolder = Path.GetFullPath(localFolder);
        _deviceId = deviceId;
        _deviceName = deviceName;
        if (ContainsPath(_sharedFolder, _localFolder) || ContainsPath(_localFolder, _sharedFolder))
            throw new ArgumentException("ローカル保存先と共有フォルダーは別の場所を指定してください。");
    }

    public event Action<ClipboardEntry>? RemoteClipboardReceived;
    public event Action? Changed;
    public event Action<SyncStatus>? StatusChanged;
    public SyncStatus Status { get { lock (_viewLock) return _status; } }

    public static async Task<SyncEngine> OpenAsync(string sharedFolder, string passphrase, string localFolder,
        string deviceId, string deviceName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(localFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);
        if (passphrase.Length < 12 || passphrase.Length > 1024) throw new ArgumentException("合言葉は12〜1024文字で指定してください。");
        ValidateShort(deviceId, nameof(deviceId), 128);
        ValidateShort(deviceName, nameof(deviceName), 128);
        var engine = new SyncEngine(sharedFolder, localFolder, deviceId, deviceName);
        try
        {
            Directory.CreateDirectory(engine._localFolder);
            await engine.InitializeAsync(passphrase, ct).ConfigureAwait(false);
            await engine.PollInternalAsync(initial: true, ct).ConfigureAwait(false);
            return engine;
        }
        catch { CryptographicOperations.ZeroMemory(engine._key); engine._gate.Dispose(); throw; }
    }

    public IReadOnlyList<ClipboardEntry> GetHistory()
    {
        lock (_viewLock) return _history.Select(Clone).ToArray();
    }

    /// <summary>Returns an isolated snapshot of one entry without copying unrelated image payloads.</summary>
    public ClipboardEntry? GetHistoryEntry(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_viewLock)
        {
            foreach (var entry in _history)
                if (string.Equals(entry.Id, id, StringComparison.Ordinal)) return Clone(entry);
            return null;
        }
    }

    public IReadOnlyList<Snippet> GetSnippets()
    {
        lock (_viewLock) return _snippets.Values.Where(x => x.Operation == "snippet-upsert")
            .Select(ToSnippet).OrderBy(x => x.SortOrder).ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    public Task<ClipboardEntry?> CaptureTextAsync(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateText(text, MaxTextBytes, nameof(text));
        if (text.Length == 0) return Task.FromResult<ClipboardEntry?>(null);
        return CaptureAsync(ClipboardKind.Text, text, null, ct);
    }

    public Task<ClipboardEntry?> CaptureImageAsync(byte[] png, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        ValidatePng(png);
        return CaptureAsync(ClipboardKind.Image, null, png.ToArray(), ct);
    }

    public Task<Snippet> UpsertSnippetAsync(string? id, string title, string text, CancellationToken ct = default) =>
        UpsertSnippetCoreAsync(id, title, text, null, ct);

    public Task<Snippet> UpsertSnippetAsync(string? id, string title, string text, string group, CancellationToken ct = default)
    {
        ValidateShort(group, nameof(group), 60);
        return UpsertSnippetCoreAsync(id, title, text, group.Trim(), ct);
    }

    private async Task<Snippet> UpsertSnippetCoreAsync(string? id, string title, string text, string? group, CancellationToken ct)
    {
        ValidateShort(title, nameof(title), 120);
        ValidateText(text, 64 * 1024, nameof(text));
        if (id is not null && !IsId(id)) throw new ArgumentException("定型文IDが不正です。", nameof(id));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            id ??= NewId();
            lock (_viewLock)
            {
                if (!_snippets.ContainsKey(id) && _snippets.Values.Count(x => x.Operation == "snippet-upsert") >= MaxSnippets)
                    throw new InvalidOperationException($"定型文は{MaxSnippets}件まで登録できます。");
                group ??= _snippets.TryGetValue(id, out var existing) ? ToSnippet(existing).Group : "共通";
            }
            string groupId;
            lock (_viewLock)
            {
                groupId = FindGroupId(group);
                if (!GetGroups().Any(x => x.Id == groupId) && GetGroups().Count >= MaxGroups)
                    throw new InvalidOperationException($"グループは{MaxGroups}件まで登録できます。");
            }
            var op = NewOperation("snippet-upsert") with { SnippetId = id, Title = title.Trim(), Text = text, Group = group, GroupId = groupId };
            // Persist a group identity even if its only snippet is subsequently deleted.
            if (groupId != DefaultGroupId && !_groups.ContainsKey(groupId))
                await CommitBatchAsync([NewOperation("group-upsert") with { GroupId = groupId, Group = group }, op], ct).ConfigureAwait(false);
            else await CommitAsync(op, ct).ConfigureAwait(false);
            lock (_viewLock) return ToSnippet(op);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteSnippetAsync(string id, CancellationToken ct = default)
    {
        if (!IsId(id)) throw new ArgumentException("定型文IDが不正です。", nameof(id));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var deletion = NewOperation("snippet-delete") with { SnippetId = id };
            if (_snippets.TryGetValue(id, out var existing) && existing.Operation == "snippet-upsert")
            {
                var snippet = ToSnippet(existing);
                if (snippet.GroupId != DefaultGroupId && !_groups.ContainsKey(snippet.GroupId!))
                {
                    await CommitBatchAsync([NewOperation("group-upsert") with { GroupId = snippet.GroupId, Group = snippet.Group }, deletion], ct).ConfigureAwait(false);
                    return;
                }
            }
            await CommitAsync(deletion, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task ClearHistoryAsync(CancellationToken ct = default) => MutateAsync(() => NewOperation("history-clear"), ct);

    public Task DeleteHistoryAsync(string id, CancellationToken ct = default)
    {
        if (!IsId(id)) throw new ArgumentException("履歴IDが不正です。", nameof(id));
        return MutateAsync(() => NewOperation("history-delete") with { TargetId = id }, ct);
    }

    public async Task PollAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { ThrowIfDisposed(); await PollInternalAsync(false, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<ClipboardEntry?> CaptureAsync(ClipboardKind kind, string? text, byte[]? png, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var fingerprint = Fingerprint(kind, text, png);
            if (fingerprint == _lastFingerprint) return null;
            var op = NewOperation("clipboard") with { Kind = kind, Text = text, ImagePng = png };
            await CommitAsync(op, ct).ConfigureAwait(false);
            return ToEntry(op);
        }
        finally { _gate.Release(); }
    }

    private async Task MutateAsync(Func<SyncOperation> factory, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { ThrowIfDisposed(); await CommitAsync(factory(), ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task CommitAsync(SyncOperation op, CancellationToken ct)
    {
        await CommitBatchAsync([op], ct).ConfigureAwait(false);
    }

    private async Task InitializeAsync(string passphrase, CancellationToken ct)
    {
        var localManifestPath = Path.Combine(_localFolder, ManifestName);
        GroupManifest? localManifest = null;
        if (File.Exists(localManifestPath)) localManifest = await ReadManifestAsync(localManifestPath, ct).ConfigureAwait(false);
        GroupManifest? sharedManifest = null;
        try
        {
            if (File.Exists(Path.Combine(_sharedFolder, ManifestName)))
                sharedManifest = await ReadManifestAsync(Path.Combine(_sharedFolder, ManifestName), ct).ConfigureAwait(false);
            else if (localManifest is null)
            {
                Directory.CreateDirectory(_sharedFolder);
                var salt = RandomNumberGenerator.GetBytes(32);
                var key = Crypto.Derive(passphrase, salt);
                var groupId = NewId();
                var verifier = Crypto.Encrypt(Encoding.UTF8.GetBytes("ClipBridge verified " + groupId), key, "verifier:" + groupId);
                var candidate = new GroupManifest(1, groupId, Convert.ToBase64String(salt), Crypto.Iterations, Convert.ToBase64String(verifier));
                CryptographicOperations.ZeroMemory(key);
                await WriteAtomicAsync(Path.Combine(_sharedFolder, ManifestName), JsonSerializer.SerializeToUtf8Bytes(candidate), false, ct).ConfigureAwait(false);
                sharedManifest = await ReadManifestAsync(Path.Combine(_sharedFolder, ManifestName), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsStorageError(ex) && localManifest is not null) { }
        _manifest = sharedManifest ?? localManifest ?? throw new IOException("共有フォルダーに接続できません。");
        if (localManifest is not null && localManifest.GroupId != _manifest.GroupId)
            throw new InvalidDataException("共有フォルダーが以前の接続先と異なります。別のローカル保存先を使用してください。");
        _key = Crypto.Derive(passphrase, Convert.FromBase64String(_manifest.Salt));
        try
        {
            var verifier = Crypto.Decrypt(Convert.FromBase64String(_manifest.Verifier), _key, "verifier:" + _manifest.GroupId);
            if (!CryptographicOperations.FixedTimeEquals(verifier, Encoding.UTF8.GetBytes("ClipBridge verified " + _manifest.GroupId)))
                throw new InvalidPassphraseException();
        }
        catch (CryptographicException) { throw new InvalidPassphraseException(); }
        await WriteAtomicAsync(localManifestPath, JsonSerializer.SerializeToUtf8Bytes(_manifest), true, ct).ConfigureAwait(false);
        await LoadStateAsync(ct).ConfigureAwait(false);
    }

    private async Task PollInternalAsync(bool initial, CancellationToken ct)
    {
        ClipboardEntry? remote = null;
        var changed = false;
        var invalidCount = 0;
        try
        {
            var manifestPath = Path.Combine(_sharedFolder, ManifestName);
            if (!File.Exists(manifestPath)) throw new IOException("共有フォルダーを待っています。");
            var currentManifest = await ReadManifestAsync(manifestPath, ct).ConfigureAwait(false);
            if (currentManifest.GroupId != _manifest.GroupId || currentManifest.Salt != _manifest.Salt || currentManifest.Verifier != _manifest.Verifier)
                throw new InvalidDataException("共有フォルダーの設定が変更されています。再接続してください。");
            var eventsFolder = Path.Combine(_sharedFolder, "events");
            Directory.CreateDirectory(eventsFolder);
            foreach (var op in _pending.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                var plaintext = JsonSerializer.SerializeToUtf8Bytes(op, JsonOptions);
                var bytes = Crypto.Encrypt(plaintext, _key, "event:" + _manifest.GroupId);
                var eventPath = Path.Combine(eventsFolder, op.Id + ".cbe");
                await WriteAtomicAsync(eventPath, bytes, false, ct).ConfigureAwait(false);
                // A same-name cloud conflict or damaged file must not silently acknowledge our durable outbox.
                var written = await ReadBoundedAsync(eventPath, Crypto.MaxEncryptedBytes, ct).ConfigureAwait(false);
                var confirmed = Crypto.Decrypt(written, _key, "event:" + _manifest.GroupId);
                if (!confirmed.AsSpan().SequenceEqual(plaintext))
                    throw new InvalidDataException("送信先の同期ファイルが一致しません。未同期データを保持して再試行します。");
                lock (_viewLock) _pending.Remove(op);
                changed = true;
            }
            foreach (var path in Directory.EnumerateFiles(eventsFolder, "*.cbe", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                var id = Path.GetFileNameWithoutExtension(path);
                if (!IsId(id) || _seen.Contains(id)) continue;
                try
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { invalidCount++; continue; }
                    var stamp = (info.Length, info.LastWriteTimeUtc.Ticks);
                    if (_badFiles.TryGetValue(id, out var previous) && previous == stamp) { invalidCount++; continue; }
                    if (info.Length > Crypto.MaxEncryptedBytes) throw new InvalidDataException("同期ファイルが大きすぎます。");
                    var ciphertext = await ReadBoundedAsync(path, Crypto.MaxEncryptedBytes, ct).ConfigureAwait(false);
                    var plaintext = Crypto.Decrypt(ciphertext, _key, "event:" + _manifest.GroupId);
                    var op = JsonSerializer.Deserialize<SyncOperation>(plaintext, JsonOptions) ?? throw new InvalidDataException();
                    ValidateOperation(op);
                    if (op.Id != id) throw new InvalidDataException("同期ファイルのIDが一致しません。");
                    _badFiles.Remove(id);
                    // Apply one decrypted payload at a time. A large shared log must never accumulate
                    // all PNG data in memory before the bounded local history can discard old entries.
                    lock (_viewLock)
                    {
                        if (!_seen.Add(op.Id)) continue;
                        var isNewer = IsNewer(op.CreatedUtc, op.Id, _lastClipboardUtc, _lastClipboardId);
                        Apply(op);
                        if (!initial && op.Operation == "clipboard" && op.DeviceId != _deviceId && isNewer &&
                            op.CreatedUtc >= _openedUtc && op.CreatedUtc > _historyClearedUtc && !_deletedHistory.Contains(op.Id))
                            remote = ToEntry(op);
                        changed = true;
                    }
                }
                catch (Exception ex) when (IsDataError(ex))
                {
                    invalidCount++;
                    try { var info = new FileInfo(path); _badFiles[id] = (info.Length, info.LastWriteTimeUtc.Ticks); } catch (IOException) { }
                    if (_badFiles.Count > 1000) _badFiles.Clear();
                }
                catch (FileNotFoundException) { }
            }
            await PruneSharedClipboardAsync(eventsFolder, ct).ConfigureAwait(false);
            if (changed || initial) await SaveStateAsync(ct).ConfigureAwait(false);
            UpdateStatus(true, invalidCount > 0 ? $"同期中 · 読み取れないファイル {invalidCount} 件を保留" : "同期中 · OneDriveの転送を待機");
        }
        catch (Exception ex) when (IsStorageError(ex) || IsDataError(ex))
        {
            // Even an unavailable folder must not prevent captures from entering the durable outbox.
            if (changed) await SaveStateAsync(ct).ConfigureAwait(false);
            UpdateStatus(false, ex is InvalidDataException ? ex.Message : "オフライン · ローカルに保存し、接続後に再送します");
        }
        if (changed) NotifyChanged();
        // A poll can discover many files; apply only the newest global clipboard head.
        var newest = remote;
        if (newest is not null && newest.Id == _lastClipboardId && newest.CreatedUtc > _historyClearedUtc && !_deletedHistory.Contains(newest.Id))
            InvokeSafely(RemoteClipboardReceived, Clone(newest));
    }

    private void Apply(SyncOperation op)
    {
        if (op.CreatedUtc > _lastOperationUtc) _lastOperationUtc = op.CreatedUtc;
        switch (op.Operation)
        {
            case "clipboard":
                if (op.CreatedUtc > _historyClearedUtc && !_deletedHistory.Contains(op.Id) && !_history.Any(x => x.Id == op.Id))
                    _history.Add(ToEntry(op));
                if (IsNewer(op.CreatedUtc, op.Id, _lastClipboardUtc, _lastClipboardId))
                {
                    _lastClipboardUtc = op.CreatedUtc; _lastClipboardId = op.Id;
                    _lastFingerprint = Fingerprint(op.Kind!.Value, op.Text, op.ImagePng);
                }
                TrimHistory();
                break;
            case "snippet-upsert":
            case "snippet-delete":
                if (!_snippets.TryGetValue(op.SnippetId!, out var old) || IsNewer(op.CreatedUtc, op.Id, old.CreatedUtc, old.Id))
                    _snippets[op.SnippetId!] = op;
                break;
            case "group-upsert":
            case "group-delete":
                if (!_groups.TryGetValue(op.GroupId!, out var groupOld) || IsNewer(op.CreatedUtc, op.Id, groupOld.CreatedUtc, groupOld.Id))
                    _groups[op.GroupId!] = op;
                break;
            case "group-order":
            case "snippet-order":
                var orderKey = op.Operation + ":" + op.TargetId;
                if (!_orders.TryGetValue(orderKey, out var orderOld) || IsNewer(op.CreatedUtc, op.Id, orderOld.CreatedUtc, orderOld.Id))
                    _orders[orderKey] = op;
                break;
            case "history-edit":
                _deletedHistory.Add(op.TargetId!);
                _history.RemoveAll(x => x.Id == op.TargetId);
                if (op.CreatedUtc > _historyClearedUtc && !_deletedHistory.Contains(op.Id) && !_history.Any(x => x.Id == op.Id))
                    _history.Add(ToEntry(op));
                TrimHistory();
                break;
            case "history-import":
                // Backfills are never clipboard heads. Preserve stable source IDs but respect explicit deletions.
                if (op.CreatedUtc > _historyClearedUtc && !_deletedHistory.Contains(op.TargetId!) &&
                    (!_historyVersions.TryGetValue(op.TargetId!, out var version) || IsNewer(op.CreatedUtc, op.Id, version.OperationUtc, version.OperationId)))
                {
                    _history.RemoveAll(x => x.Id == op.TargetId);
                    _historyVersions[op.TargetId!] = new(op.TargetId!, op.CreatedUtc, op.Id);
                    _history.Add(new(op.TargetId!, op.Kind!.Value, op.Text, op.ImagePng?.ToArray(), op.HistoryUtc!.Value, op.DeviceId, op.DeviceName));
                }
                TrimHistory();
                break;
            case "history-clear":
                if (op.CreatedUtc > _historyClearedUtc) _historyClearedUtc = op.CreatedUtc;
                _history.RemoveAll(x => (_historyVersions.TryGetValue(x.Id, out var imported) ? imported.OperationUtc : x.CreatedUtc) <= _historyClearedUtc);
                PruneHistoryVersions();
                break;
            case "history-delete":
                _deletedHistory.Add(op.TargetId!);
                _history.RemoveAll(x => x.Id == op.TargetId);
                _historyVersions.Remove(op.TargetId!);
                break;
        }
    }

    private void TrimHistory()
    {
        _history.Sort((a, b) => { var c = b.CreatedUtc.CompareTo(a.CreatedUtc); return c != 0 ? c : StringComparer.Ordinal.Compare(b.Id, a.Id); });
        long bytes = 0;
        var keep = 0;
        foreach (var entry in _history)
        {
            bytes += entry.ImagePng?.Length ?? Encoding.UTF8.GetByteCount(entry.Text ?? "");
            if (keep == MaxHistoryEntries || bytes > MaxHistoryBytes) break;
            keep++;
        }
        if (keep < _history.Count) _history.RemoveRange(keep, _history.Count - keep);
        PruneHistoryVersions();
    }

    private void PruneHistoryVersions()
    {
        var visible = _history.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _historyVersions.Keys.Where(x => !visible.Contains(x)).ToArray()) _historyVersions.Remove(id);
    }

    private async Task SaveStateAsync(CancellationToken ct)
    {
        SavedState state;
        lock (_viewLock)
            state = new(1, _manifest.GroupId, _history.ToList(), _snippets.Values.ToList(), _pending.ToList(),
                _seen.TakeLast(100_000).ToList(), _lastFingerprint, _lastClipboardUtc, _lastClipboardId,
                _historyClearedUtc, _deletedHistory.TakeLast(100_000).ToList(), _groups.Values.ToList(), _orders.Values.ToList(), _historyVersions.Values.ToList());
        // JSON escaping can expand control characters sixfold. Budget the exact serialized bytes,
        // preserving the durable outbox and snippet versions while trimming only oldest history.
        var fixedBytes = JsonSerializer.SerializeToUtf8Bytes(state with { History = [] }, JsonOptions).LongLength;
        var budget = CacheByteLimit - 32L;
        if (fixedBytes > budget)
            throw new InvalidOperationException("ローカル保存容量の上限に達しました。同期を完了し、定型文を整理してください。");
        long used = fixedBytes;
        var keep = 0;
        foreach (var entry in state.History)
        {
            var length = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions).LongLength + (keep > 0 ? 1 : 0);
            if (used + length > budget) break;
            used += length;
            keep++;
        }
        if (keep < state.History.Count)
        {
            state = state with { History = state.History.Take(keep).ToList() };
            lock (_viewLock) _history.RemoveRange(keep, _history.Count - keep);
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (bytes.LongLength + 32 > CacheByteLimit) throw new InvalidDataException("ローカル保存サイズが上限を超えています。");
        await WriteAtomicAsync(Path.Combine(_localFolder, "state.cbc"), Crypto.Encrypt(bytes, _key, "cache:" + _manifest.GroupId), true, ct).ConfigureAwait(false);
    }

    private async Task PruneSharedClipboardAsync(string eventsFolder, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPruneUtc < TimeSpan.FromMinutes(1)) return;
        _lastPruneUtc = now;
        var cutoff = now - SharedClipboardRetention;
        foreach (var path in Directory.EnumerateFiles(eventsFolder, "*.cbe", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(path);
            if (!IsId(id)) continue;
            try
            {
                var info = new FileInfo(path);
                // Recently hydrated files are left for a later pass. Never trust a file's date to authorize removal.
                if (info.LastWriteTimeUtc >= cutoff.UtcDateTime || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    info.Length > Crypto.MaxEncryptedBytes) continue;
                var encrypted = await ReadBoundedAsync(path, Crypto.MaxEncryptedBytes, ct).ConfigureAwait(false);
                var op = JsonSerializer.Deserialize<SyncOperation>(Crypto.Decrypt(encrypted, _key, "event:" + _manifest.GroupId), JsonOptions)
                    ?? throw new InvalidDataException();
                ValidateOperation(op);
                if (op.Id != id || op.Operation != "clipboard" || op.CreatedUtc >= cutoff) continue;
                File.Delete(path);
                _badFiles.Remove(id);
            }
            catch (Exception ex) when (IsStorageError(ex) || IsDataError(ex)) { }
        }
        // The remote log is authoritative for live operations; old IDs may be safely re-read idempotently.
        if (_seen.Count > 100_000)
            foreach (var id in _seen.Take(_seen.Count - 100_000).ToArray()) _seen.Remove(id);
    }

    private async Task LoadStateAsync(CancellationToken ct)
    {
        var path = Path.Combine(_localFolder, "state.cbc");
        if (!File.Exists(path)) return;
        try
        {
            var ciphertext = await ReadBoundedAsync(path, MaxCacheBytes, ct).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize<SavedState>(Crypto.Decrypt(ciphertext, _key, "cache:" + _manifest.GroupId), JsonOptions)
                ?? throw new InvalidDataException();
            if (state.Version != 1 || state.GroupId != _manifest.GroupId || state.History is null || state.Snippets is null ||
                state.Pending is null || state.Seen is null || state.DeletedHistory is null || state.History.Count > MaxHistoryEntries ||
                state.Pending.Count > MaxPendingOperations || state.Seen.Count > 100_000 || state.DeletedHistory.Count > 100_000)
                throw new InvalidDataException("ローカルキャッシュの形式が不正です。");
            foreach (var op in state.Snippets.Concat(state.Pending).Concat(state.Groups ?? []).Concat(state.Orders ?? [])) ValidateOperation(op);
            if ((state.HistoryVersions?.Count ?? 0) > MaxHistoryEntries || (state.HistoryVersions ?? []).Any(x => !IsId(x.Id) || !IsId(x.OperationId) || x.OperationUtc < DateTimeOffset.UnixEpoch || x.OperationUtc > DateTimeOffset.UtcNow.AddMinutes(5))) throw new InvalidDataException();
            foreach (var entry in state.History)
                ValidateOperation(new SyncOperation(entry.Id, "clipboard", entry.CreatedUtc, entry.DeviceId, entry.DeviceName, entry.Kind, entry.Text, entry.ImagePng));
            if (state.Seen.Any(x => !IsId(x)) || state.DeletedHistory.Any(x => !IsId(x))) throw new InvalidDataException();
            lock (_viewLock)
            {
                _history.AddRange(state.History);
                foreach (var op in state.Snippets) _snippets[op.SnippetId!] = op;
                foreach (var op in state.Groups ?? []) _groups[op.GroupId!] = op;
                foreach (var op in state.Orders ?? []) _orders[op.Operation + ":" + op.TargetId] = op;
                foreach (var version in state.HistoryVersions ?? []) _historyVersions[version.Id] = version;
                _pending.AddRange(state.Pending);
                foreach (var id in state.Seen) _seen.Add(id);
                foreach (var id in state.DeletedHistory) _deletedHistory.Add(id);
                _lastFingerprint = state.LastFingerprint; _lastClipboardUtc = state.LastClipboardUtc;
                _lastClipboardId = state.LastClipboardId; _historyClearedUtc = state.HistoryClearedUtc;
                _lastOperationUtc = new[] { _lastClipboardUtc, _historyClearedUtc }
                    .Concat(state.Snippets.Select(x => x.CreatedUtc)).Concat(state.Pending.Select(x => x.CreatedUtc))
                    .Concat((state.Groups ?? []).Select(x => x.CreatedUtc)).Concat((state.Orders ?? []).Select(x => x.CreatedUtc))
                    .Concat((state.HistoryVersions ?? []).Select(x => x.OperationUtc)).Max();
                TrimHistory();
            }
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            // Preserve the encrypted damaged cache for recovery; do not overwrite a possibly unsent outbox.
            throw new InvalidDataException("ローカル保存データを読み取れません。state.cbcを別名で保管してから再接続してください。", ex);
        }
    }

    private static async Task<GroupManifest> ReadManifestAsync(string path, CancellationToken ct)
    {
        var bytes = await ReadBoundedAsync(path, 16 * 1024, ct).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<GroupManifest>(bytes, JsonOptions) ?? throw new InvalidDataException("共有設定が不正です。");
        try
        {
            if (manifest.Version != 1 || !IsId(manifest.GroupId) || manifest.Iterations != Crypto.Iterations ||
                Convert.FromBase64String(manifest.Salt).Length != 32 || Convert.FromBase64String(manifest.Verifier).Length > 256)
                throw new InvalidDataException("共有設定が不正です。");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException) { throw new InvalidDataException("共有設定が不正です。", ex); }
        return manifest;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maxLength, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
        if (stream.Length > maxLength || stream.Length < 1) throw new InvalidDataException("ファイルサイズが不正です。");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, bool overwrite, CancellationToken ct)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + NewId() + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            try { File.Move(temporary, path, overwrite); }
            catch (IOException) when (!overwrite && File.Exists(path)) { }
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }

    private SyncOperation NewOperation(string operation)
    {
        var now = DateTimeOffset.UtcNow;
        if (now <= _lastOperationUtc && _lastOperationUtc < DateTimeOffset.UtcNow.AddMinutes(5)) now = _lastOperationUtc.AddTicks(1);
        if (now <= _historyClearedUtc) now = _historyClearedUtc.AddTicks(1);
        return new(NewId(), operation, now, _deviceId, _deviceName);
    }

    private static void ValidateOperation(SyncOperation op)
    {
        if (!IsId(op.Id)) throw new InvalidDataException("IDが不正です。");
        ValidateShort(op.DeviceId, nameof(op.DeviceId), 128);
        ValidateShort(op.DeviceName, nameof(op.DeviceName), 128);
        if (op.CreatedUtc < DateTimeOffset.UnixEpoch || op.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("日時が不正です。端末の時計を確認してください。");
        if (op.GroupId is not null && (!IsId(op.GroupId) || op.Operation is not ("snippet-upsert" or "group-upsert" or "group-delete")))
            throw new InvalidDataException("グループIDが不正です。");
        if (op.SortOrder is not null && (op.SortOrder < 0 || op.Operation is not ("group-order" or "snippet-order")))
            throw new InvalidDataException("並び順が不正です。");
        if (op.HistoryUtc is not null && (op.Operation != "history-import" || op.HistoryUtc < DateTimeOffset.UnixEpoch || op.HistoryUtc > DateTimeOffset.UtcNow.AddMinutes(5)))
            throw new InvalidDataException("履歴の日時が不正です。");
        switch (op.Operation)
        {
            case "clipboard" when op.Kind == ClipboardKind.Text && op.Text is not null && op.ImagePng is null &&
                op.SnippetId is null && op.Title is null && op.TargetId is null && op.Group is null:
                ValidateText(op.Text, MaxTextBytes, nameof(op.Text)); break;
            case "clipboard" when op.Kind == ClipboardKind.Image && op.ImagePng is not null && op.Text is null &&
                op.SnippetId is null && op.Title is null && op.TargetId is null && op.Group is null:
                ValidatePng(op.ImagePng); break;
            case "snippet-upsert" when IsId(op.SnippetId) && op.Title is not null && op.Text is not null && op.ImagePng is null &&
                op.Kind is null && op.TargetId is null:
                ValidateShort(op.Title, nameof(op.Title), 120); ValidateText(op.Text, 64 * 1024, nameof(op.Text));
                if (op.Group is not null) ValidateShort(op.Group, nameof(op.Group), 60);
                break;
            case "snippet-delete" when IsId(op.SnippetId) && op.Kind is null && op.Text is null && op.ImagePng is null && op.Title is null && op.TargetId is null && op.Group is null: break;
            case "history-delete" when IsId(op.TargetId) && op.Kind is null && op.Text is null && op.ImagePng is null && op.Title is null && op.SnippetId is null && op.Group is null: break;
            case "history-clear" when op.Kind is null && op.Text is null && op.ImagePng is null && op.Title is null && op.SnippetId is null && op.TargetId is null && op.Group is null: break;
            case "group-upsert" when IsId(op.GroupId) && op.Group is not null && op.Kind is null && op.Text is null && op.ImagePng is null && op.Title is null && op.SnippetId is null && op.TargetId is null:
                ValidateShort(op.Group, nameof(op.Group), 60); break;
            case "group-delete" when IsId(op.GroupId) && op.GroupId != DefaultGroupId && op.Group is null && op.Kind is null && op.Text is null && op.ImagePng is null && op.Title is null && op.SnippetId is null && op.TargetId is null: break;
            case "group-order":
            case "snippet-order":
                if (!IsId(op.TargetId) || op.SortOrder is null || op.Kind is not null || op.Text is not null || op.ImagePng is not null || op.Title is not null || op.SnippetId is not null || op.Group is not null)
                    throw new InvalidDataException("並び順の操作が不正です。");
                break;
            case "history-edit" when IsId(op.TargetId) && op.Kind == ClipboardKind.Text && op.Text is not null && op.ImagePng is null && op.Title is null && op.SnippetId is null && op.Group is null:
                ValidateText(op.Text, MaxTextBytes, nameof(op.Text)); break;
            case "history-import" when IsId(op.TargetId) && op.Kind is not null && op.HistoryUtc is not null && op.Title is null && op.SnippetId is null && op.Group is null:
                if (op.Kind == ClipboardKind.Text && op.Text is not null && op.ImagePng is null) ValidateText(op.Text, MaxTextBytes, nameof(op.Text));
                else if (op.Kind == ClipboardKind.Image && op.Text is null && op.ImagePng is not null) ValidatePng(op.ImagePng);
                else throw new InvalidDataException("履歴の内容が不正です。");
                break;
            default: throw new InvalidDataException("操作の形式が不正です。");
        }
    }

    private static bool IsId(string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static void ValidateShort(string text, string name, int max)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text, name);
        if (text.Length > max || text.Any(char.IsControl)) throw new ArgumentException($"{name}が長すぎるか、不正な文字を含みます。", name);
    }
    private static void ValidateText(string text, int maxBytes, string name)
    {
        ArgumentNullException.ThrowIfNull(text, name);
        if (Encoding.UTF8.GetByteCount(text) > maxBytes) throw new ArgumentException($"テキストは{maxBytes / 1024}KB以内で指定してください。", name);
    }
    private static void ValidatePng(byte[] png)
    {
        if (png.Length > MaxImageBytes || png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(PngSignature) ||
            !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new ArgumentException("PNG画像は8MB以内で指定してください。", nameof(png));
        var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (width == 0 || height == 0 || width > 16384 || height > 16384 || (long)width * height > 32_000_000)
            throw new ArgumentException("画像の寸法が大きすぎます。", nameof(png));
    }
    private static string Fingerprint(ClipboardKind kind, string? text, byte[]? png) =>
        ((int)kind).ToString() + Convert.ToHexString(SHA256.HashData(png ?? Encoding.UTF8.GetBytes(text!)));
    private static long OperationBytes(SyncOperation op) => JsonSerializer.SerializeToUtf8Bytes(op, JsonOptions).LongLength;
    private static bool IsNewer(DateTimeOffset time, string id, DateTimeOffset oldTime, string oldId) =>
        time > oldTime || (time == oldTime && StringComparer.Ordinal.Compare(id, oldId) > 0);
    private static ClipboardEntry ToEntry(SyncOperation op) => new(op.Id, op.Kind!.Value, op.Text, op.ImagePng?.ToArray(), op.CreatedUtc, op.DeviceId, op.DeviceName);
    private static ClipboardEntry Clone(ClipboardEntry entry) => entry with { ImagePng = entry.ImagePng?.ToArray() };
    private static bool ContainsPath(string parent, string child) =>
        child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException;
    private static bool IsDataError(Exception ex) => ex is JsonException or CryptographicException or InvalidDataException or ArgumentException or OverflowException;
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void NotifyChanged() { foreach (Action handler in Changed?.GetInvocationList() ?? []) { try { handler(); } catch { } } }
    private static void InvokeSafely<T>(Action<T>? action, T value) { foreach (Action<T> handler in action?.GetInvocationList() ?? []) { try { handler(value); } catch { } } }
    private void UpdateStatus(bool online, string message)
    {
        SyncStatus status;
        lock (_viewLock)
        {
            if (_status.IsOnline == online && _status.Message == message && _status.PendingCount == _pending.Count) return;
            status = _status = new(online, message, DateTimeOffset.UtcNow, _pending.Count);
        }
        InvokeSafely(StatusChanged, status);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            CryptographicOperations.ZeroMemory(_key);
            RemoteClipboardReceived = null; Changed = null; StatusChanged = null;
        }
        finally { _gate.Release(); }
    }
}
