using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipBridge.Core;

public sealed partial class SyncEngine
{
    public static string DefaultGroupId { get; } = GroupIdForName("共通");
    private const int MaxBackupBytes = 256 * 1024 * 1024;

    public IReadOnlyList<SnippetGroup> GetGroups()
    {
        lock (_viewLock)
        {
            var groups = new Dictionary<string, SnippetGroup>
            {
                [DefaultGroupId] = new(DefaultGroupId, "共通", long.MaxValue, DateTimeOffset.UnixEpoch)
            };
            foreach (var op in _snippets.Values.Where(x => x.Operation == "snippet-upsert"))
            {
                var id = op.GroupId ?? GroupIdForName(op.Group ?? "共通");
                if (!_groups.ContainsKey(id) && !groups.ContainsKey(id))
                    groups[id] = new(id, op.Group ?? "共通", OrderOf("group-order", id), op.CreatedUtc);
            }
            foreach (var op in _groups.Values.Where(x => x.Operation == "group-upsert"))
                groups[op.GroupId!] = new(op.GroupId!, op.Group!, OrderOf("group-order", op.GroupId!), op.CreatedUtc);
            groups[DefaultGroupId] = groups[DefaultGroupId] with { SortOrder = OrderOf("group-order", DefaultGroupId) };
            return groups.Values.OrderBy(x => x.SortOrder).ThenBy(x => x.Id == DefaultGroupId ? 0 : 1)
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        }
    }

    public ClipBridgeSnapshot GetSnapshot()
    {
        lock (_viewLock) return new(1, DateTimeOffset.UtcNow, GetGroups(), GetSnippets(), GetHistory());
    }

    public async Task<SnippetGroup> UpsertGroupAsync(string? id, string name, CancellationToken ct = default)
    {
        ValidateShort(name, nameof(name), 60);
        name = name.Trim();
        if (id is not null && !IsId(id)) throw new ArgumentException("グループIDが不正です。", nameof(id));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            lock (_viewLock)
            {
                var groups = GetGroups();
                var same = groups.FirstOrDefault(x => SameGroupName(x.Name, name));
                if (id is null && same is not null) return same;
                if (same is not null && same.Id != id) throw new ArgumentException("同じ名前のグループが既にあります。", nameof(name));
                id ??= FindGroupId(name);
                if (id == DefaultGroupId && name != "共通") throw new ArgumentException("共通グループの名前は変更できません。", nameof(name));
                if (groups.All(x => x.Id != id) && groups.Count >= MaxGroups)
                    throw new InvalidOperationException($"グループは{MaxGroups}件まで登録できます。");
            }
            var op = NewOperation("group-upsert") with { GroupId = id, Group = name };
            await CommitAsync(op, ct).ConfigureAwait(false);
            return GetGroups().Single(x => x.Id == id);
        }
        finally { _gate.Release(); }
    }

    public async Task<SnippetGroup> RenameGroupAsync(string id, string name, CancellationToken ct = default)
    {
        if (!GetGroups().Any(x => x.Id == id)) throw new ArgumentException("グループが見つかりません。", nameof(id));
        return await UpsertGroupAsync(id, name, ct).ConfigureAwait(false);
    }

    public async Task DeleteGroupAsync(string id, CancellationToken ct = default)
    {
        if (!IsId(id) || id == DefaultGroupId) throw new ArgumentException("共通グループは削除できません。", nameof(id));
        await MutateAsync(() => NewOperation("group-delete") with { GroupId = id }, ct).ConfigureAwait(false);
    }

    public Task ReorderGroupsAsync(IReadOnlyList<string> ids, CancellationToken ct = default) => ReorderAsync(ids, true, ct);
    public Task ReorderSnippetsAsync(IReadOnlyList<string> ids, CancellationToken ct = default) => ReorderAsync(ids, false, ct);

    /// <summary>Applies a complete group-management dialog in one durable transaction, including name swaps.</summary>
    public Task ApplyGroupLayoutAsync(IReadOnlyList<SnippetGroup> finalGroups, IReadOnlyList<Snippet> finalSnippets, CancellationToken ct = default) =>
        ApplyGroupLayoutCoreAsync(finalGroups, finalSnippets, null, null, ct);

    /// <summary>Also saves explicitly edited titles and text, rejecting concurrent edits to those same items.</summary>
    public Task ApplyGroupLayoutAsync(IReadOnlyList<SnippetGroup> finalGroups, IReadOnlyList<Snippet> finalSnippets,
        IReadOnlyList<Snippet> originalSnippets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(originalSnippets);
        return ApplyGroupLayoutCoreAsync(finalGroups, finalSnippets, originalSnippets, null, ct);
    }

    /// <summary>Also rejects group additions, deletions and renames received while the management dialog was open.</summary>
    public Task ApplyGroupLayoutAsync(IReadOnlyList<SnippetGroup> finalGroups, IReadOnlyList<Snippet> finalSnippets,
        IReadOnlyList<Snippet> originalSnippets, IReadOnlyList<SnippetGroup> originalGroups, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(originalSnippets); ArgumentNullException.ThrowIfNull(originalGroups);
        return ApplyGroupLayoutCoreAsync(finalGroups, finalSnippets, originalSnippets, originalGroups, ct);
    }

    private async Task ApplyGroupLayoutCoreAsync(IReadOnlyList<SnippetGroup> finalGroups, IReadOnlyList<Snippet> finalSnippets,
        IReadOnlyList<Snippet>? originalSnippets, IReadOnlyList<SnippetGroup>? originalGroups, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(finalGroups); ArgumentNullException.ThrowIfNull(finalSnippets);
        // A modal editor passes snapshots. Freeze caller collections before awaiting a busy engine.
        finalGroups = finalGroups.ToArray(); finalSnippets = finalSnippets.ToArray(); originalSnippets = originalSnippets?.ToArray(); originalGroups = originalGroups?.ToArray();
        ValidateSnapshot(new(1, DateTimeOffset.UtcNow, finalGroups, finalSnippets, []));
        Dictionary<string, Snippet>? originals = null;
        if (originalSnippets is not null)
        {
            if (originalSnippets.Count != finalSnippets.Count || originalSnippets.Any(x => x is null || !IsId(x.Id)) ||
                originalSnippets.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != originalSnippets.Count)
                throw new ArgumentException("管理画面を開いたときの定型文一覧が不正です。管理画面を開き直してください。", nameof(originalSnippets));
            originals = originalSnippets.ToDictionary(x => x.Id, StringComparer.Ordinal);
            if (finalSnippets.Any(x => !originals.ContainsKey(x.Id)))
                throw new ArgumentException("管理画面の保存対象と元の定型文が一致しません。管理画面を開き直してください。", nameof(originalSnippets));
        }
        Dictionary<string, SnippetGroup>? groupOriginals = null;
        if (originalGroups is not null)
        {
            if (originalGroups.Any(x => x is null || !IsId(x.Id)) || originalGroups.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != originalGroups.Count)
                throw new ArgumentException("管理画面を開いたときのグループ一覧が不正です。管理画面を開き直してください。", nameof(originalGroups));
            foreach (var group in originalGroups) ValidateShort(group.Name, nameof(originalGroups), 60);
            groupOriginals = originalGroups.ToDictionary(x => x.Id, StringComparer.Ordinal);
        }
        if (!finalGroups.Any(x => x.Id == DefaultGroupId && x.Name == "共通"))
            throw new ArgumentException("共通グループを含めてください。", nameof(finalGroups));
        var byGroup = finalGroups.ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (finalSnippets.Any(x => x.GroupId is null || !byGroup.TryGetValue(x.GroupId, out var group) || !SameGroupName(group.Name, x.Group)))
            throw new ArgumentException("定型文の移動先グループが不正です。", nameof(finalSnippets));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var existingGroups = GetGroups().ToDictionary(x => x.Id, StringComparer.Ordinal);
            var existingSnippets = GetSnippets().ToDictionary(x => x.Id, StringComparer.Ordinal);
            if (groupOriginals is not null && (groupOriginals.Count != existingGroups.Count || groupOriginals.Any(x =>
                !existingGroups.TryGetValue(x.Key, out var currentGroup) || currentGroup.Name != x.Value.Name)))
                throw new InvalidOperationException("管理画面を開いた後に、別の操作または端末でグループが追加・削除・変更されました。管理画面を開き直して、最新のグループを確認してください。今回の変更は保存していません。");
            if (existingSnippets.Count != finalSnippets.Count || finalSnippets.Any(x => !existingSnippets.ContainsKey(x.Id)))
                throw new InvalidOperationException("定型文の件数が別の操作で変わりました。管理画面を開き直してください。データは変更していません。");
            var editedIds = new HashSet<string>(StringComparer.Ordinal);
            if (originals is not null)
            {
                foreach (var planned in finalSnippets)
                {
                    var original = originals[planned.Id];
                    if (planned.Title == original.Title && planned.Text == original.Text) continue;
                    var current = existingSnippets[planned.Id];
                    if (current.Title != original.Title || current.Text != original.Text)
                        throw new InvalidOperationException("編集中の定型文が別の操作または端末で更新されました。管理画面を開き直して、最新の内容を確認してください。今回の変更は保存していません。");
                    editedIds.Add(planned.Id);
                }
            }
            var operations = new List<SyncOperation>();
            foreach (var old in existingGroups.Values.Where(x => !byGroup.ContainsKey(x.Id)))
                operations.Add(NewOperation("group-delete") with { GroupId = old.Id });
            for (var index = 0; index < finalGroups.Count; index++)
            {
                var group = finalGroups[index];
                if (!existingGroups.TryGetValue(group.Id, out var old) || old.Name != group.Name || !_groups.ContainsKey(group.Id))
                    operations.Add(NewOperation("group-upsert") with { GroupId = group.Id, Group = group.Name.Trim() });
                operations.Add(NewOperation("group-order") with { TargetId = group.Id, SortOrder = group.SortOrder == long.MaxValue ? index : group.SortOrder });
            }
            for (var index = 0; index < finalSnippets.Count; index++)
            {
                var planned = finalSnippets[index]; var current = existingSnippets[planned.Id];
                bool edited = editedIds.Contains(planned.Id);
                if (current.GroupId != planned.GroupId || edited)
                    operations.Add(NewOperation("snippet-upsert") with { SnippetId = current.Id, Title = edited ? planned.Title.Trim() : current.Title, Text = edited ? planned.Text : current.Text,
                        GroupId = planned.GroupId, Group = byGroup[planned.GroupId!].Name.Trim() });
                operations.Add(NewOperation("snippet-order") with { TargetId = current.Id, SortOrder = planned.SortOrder == long.MaxValue ? index : planned.SortOrder });
            }
            await CommitBatchAsync(operations, ct, requireAllHistory: true).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task ReorderAsync(IReadOnlyList<string> ids, bool groups, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Any(x => !IsId(x)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new ArgumentException("並び順のIDが重複しているか不正です。", nameof(ids));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var current = groups ? GetGroups().Select(x => x.Id).ToArray() : GetSnippets().Select(x => x.Id).ToArray();
            if (ids.Any(x => !current.Contains(x, StringComparer.Ordinal))) throw new ArgumentException("対象が見つかりません。", nameof(ids));
            var ordered = ids.Concat(current.Where(x => !ids.Contains(x, StringComparer.Ordinal))).ToArray();
            var operations = ordered.Select((id, index) => NewOperation(groups ? "group-order" : "snippet-order") with
                { TargetId = id, SortOrder = index }).ToArray();
            await CommitBatchAsync(operations, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<ClipboardEntry> EditHistoryAsync(string id, string text, CancellationToken ct = default)
    {
        if (!IsId(id)) throw new ArgumentException("履歴IDが不正です。", nameof(id));
        ValidateText(text, MaxTextBytes, nameof(text));
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("空の履歴は保存できません。", nameof(text));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var existing = GetHistory().SingleOrDefault(x => x.Id == id);
            if (existing is null || existing.Kind != ClipboardKind.Text) throw new ArgumentException("編集できるテキスト履歴が見つかりません。", nameof(id));
            var op = NewOperation("history-edit") with { TargetId = id, Kind = ClipboardKind.Text, Text = text };
            await CommitAsync(op, ct).ConfigureAwait(false);
            return ToEntry(op);
        }
        finally { _gate.Release(); }
    }

    public Task<ImportResult> ImportSnapshotAsync(ClipBridgeSnapshot snapshot, CancellationToken ct = default) =>
        ImportSnapshotCoreAsync(snapshot, restore: false, ct);

    private async Task<ImportResult> ImportSnapshotCoreAsync(ClipBridgeSnapshot snapshot, bool restore, CancellationToken ct)
    {
        ValidateSnapshot(snapshot);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var currentGroups = GetGroups().ToDictionary(x => x.Id);
            var currentSnippets = GetSnippets().ToDictionary(x => x.Id);
            var currentHistory = GetHistory().ToDictionary(x => x.Id);
            var groupMap = new Dictionary<string, string>();
            var ops = new List<SyncOperation>();
            foreach (var group in snapshot.Groups)
            {
                var matchingName = currentGroups.Values.FirstOrDefault(x => SameGroupName(x.Name, group.Name));
                var mapped = matchingName?.Id ?? group.Id;
                if (mapped == DefaultGroupId && group.Name != "共通") throw new InvalidDataException("共通グループの名前が不正です。");
                groupMap[group.Id] = mapped;
                ops.Add(NewOperation("group-upsert") with { GroupId = mapped, Group = group.Name.Trim() });
                if (group.SortOrder != long.MaxValue) ops.Add(NewOperation("group-order") with { TargetId = mapped, SortOrder = group.SortOrder });
                currentGroups[mapped] = group with { Id = mapped };
            }
            foreach (var snippet in snapshot.Snippets)
            {
                var groupId = snippet.GroupId is not null && groupMap.TryGetValue(snippet.GroupId, out var mapped) ? mapped :
                    currentGroups.Values.FirstOrDefault(x => SameGroupName(x.Name, snippet.Group))?.Id ?? FindGroupId(snippet.Group);
                if (!currentGroups.ContainsKey(groupId))
                {
                    ops.Add(NewOperation("group-upsert") with { GroupId = groupId, Group = snippet.Group.Trim() });
                    currentGroups[groupId] = new(groupId, snippet.Group.Trim(), long.MaxValue, snippet.UpdatedUtc);
                }
                ops.Add(NewOperation("snippet-upsert") with { SnippetId = snippet.Id, Title = snippet.Title.Trim(), Text = snippet.Text, Group = currentGroups[groupId].Name, GroupId = groupId });
                if (snippet.SortOrder != long.MaxValue) ops.Add(NewOperation("snippet-order") with { TargetId = snippet.Id, SortOrder = snippet.SortOrder });
                currentSnippets[snippet.Id] = snippet;
            }
            var importedHistory = 0;
            foreach (var entry in snapshot.History)
            {
                if (currentHistory.ContainsKey(entry.Id)) continue;
                if (restore && currentHistory.Values.Any(x => x.Kind == entry.Kind && x.CreatedUtc == entry.CreatedUtc &&
                    Fingerprint(x.Kind, x.Text, x.ImagePng) == Fingerprint(entry.Kind, entry.Text, entry.ImagePng))) continue;
                var id = restore && _deletedHistory.Contains(entry.Id) ? NewId() : entry.Id;
                if (_deletedHistory.Contains(id)) continue;
                var op = NewOperation("history-import") with { TargetId = id, Kind = entry.Kind, Text = entry.Text,
                    ImagePng = entry.ImagePng?.ToArray(), HistoryUtc = entry.CreatedUtc };
                ops.Add(op);
                currentHistory[id] = entry with { Id = id };
                importedHistory++;
            }
            if (currentGroups.Count > MaxGroups || currentSnippets.Count > MaxSnippets || currentHistory.Count > MaxHistoryEntries ||
                currentHistory.Values.Sum(x => x.ImagePng?.LongLength ?? Encoding.UTF8.GetByteCount(x.Text ?? "")) > MaxHistoryBytes)
                throw new InvalidOperationException($"取り込み後の保存上限を超えます（グループ{MaxGroups}件、定型文{MaxSnippets}件、履歴{MaxHistoryEntries}件・96MB）。データは変更していません。");
            await CommitBatchAsync(ops, ct, requireAllHistory: true).ConfigureAwait(false);
            return new(snapshot.Groups.Count, snapshot.Snippets.Count, importedHistory);
        }
        finally { _gate.Release(); }
    }

    public Task ExportBackupAsync(string path, CancellationToken ct = default) => ExportBackupCoreAsync(path, null, ct);
    public Task ExportBackupAsync(string path, string passphrase, CancellationToken ct = default) => ExportBackupCoreAsync(path, passphrase, ct);

    private async Task ExportBackupCoreAsync(string path, string? passphrase, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (passphrase is not null) ValidateBackupPassphrase(passphrase);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        byte[] key = [];
        try
        {
            ThrowIfDisposed();
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(GetSnapshot(), JsonOptions);
            if (plaintext.Length > MaxBackupBytes - 68) throw new InvalidOperationException("バックアップが256MBを超えています。");
            byte[] result;
            if (passphrase is not null)
            {
                var salt = RandomNumberGenerator.GetBytes(32);
                key = Crypto.Derive(passphrase, salt);
                var encrypted = Crypto.Encrypt(plaintext, key, "clipbridge-backup:v1:" + Convert.ToBase64String(salt));
                result = new byte[36 + encrypted.Length];
                "CBB1"u8.CopyTo(result); salt.CopyTo(result, 4); encrypted.CopyTo(result, 36);
            }
            else
            {
                var encrypted = Crypto.Encrypt(plaintext, _key, "backup:v1:" + _manifest.GroupId);
                result = new byte[4 + encrypted.Length]; "CBK1"u8.CopyTo(result); encrypted.CopyTo(result, 4);
            }
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await WriteAtomicAsync(fullPath, result, true, ct).ConfigureAwait(false);
        }
        finally { if (key.Length > 0) CryptographicOperations.ZeroMemory(key); _gate.Release(); }
    }

    public Task<ImportResult> RestoreBackupAsync(string path, CancellationToken ct = default) => RestoreBackupCoreAsync(path, null, ct);
    public Task<ImportResult> RestoreBackupAsync(string path, string passphrase, CancellationToken ct = default) => RestoreBackupCoreAsync(path, passphrase, ct);

    private async Task<ImportResult> RestoreBackupCoreAsync(string path, string? passphrase, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (passphrase is not null) ValidateBackupPassphrase(passphrase);
        var encrypted = await ReadBoundedAsync(path, MaxBackupBytes, ct).ConfigureAwait(false);
        byte[] key = [];
        ClipBridgeSnapshot snapshot;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            byte[] plaintext;
            if (encrypted.Length >= 68 && encrypted.AsSpan(0, 4).SequenceEqual("CBB1"u8))
            {
                if (passphrase is null) throw new ArgumentException("このバックアップの合言葉を指定してください。", nameof(passphrase));
                var salt = encrypted.AsSpan(4, 32).ToArray();
                key = Crypto.Derive(passphrase, salt);
                plaintext = Crypto.Decrypt(encrypted[36..], key, "clipbridge-backup:v1:" + Convert.ToBase64String(salt));
            }
            else if (encrypted.Length >= 36 && encrypted.AsSpan(0, 4).SequenceEqual("CBK1"u8))
                plaintext = Crypto.Decrypt(encrypted[4..], _key, "backup:v1:" + _manifest.GroupId);
            else throw new InvalidDataException("ClipBridgeバックアップの形式が不正です。");
            snapshot = JsonSerializer.Deserialize<ClipBridgeSnapshot>(plaintext, JsonOptions) ?? throw new InvalidDataException("バックアップにデータがありません。");
            ValidateSnapshot(snapshot);
        }
        catch (CryptographicException ex) { throw new InvalidDataException("バックアップの合言葉が違うか、ファイルが破損しています。データは変更していません。", ex); }
        finally { if (key.Length > 0) CryptographicOperations.ZeroMemory(key); _gate.Release(); }
        return await ImportSnapshotCoreAsync(snapshot, restore: true, ct).ConfigureAwait(false);
    }

    private static void ValidateBackupPassphrase(string passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);
        if (passphrase.Length < 12 || passphrase.Length > 1024) throw new ArgumentException("合言葉は12〜1024文字で指定してください。", nameof(passphrase));
    }

    private static void ValidateSnapshot(ClipBridgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Version != 1 || snapshot.Groups is null || snapshot.Snippets is null || snapshot.History is null ||
            snapshot.Groups.Count > MaxGroups || snapshot.Snippets.Count > MaxSnippets || snapshot.History.Count > MaxHistoryEntries ||
            snapshot.Groups.Any(x => x is null) || snapshot.Snippets.Any(x => x is null) || snapshot.History.Any(x => x is null))
            throw new InvalidDataException("取り込みデータの形式または件数が不正です。");
        if (snapshot.Groups.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Groups.Count ||
            snapshot.Groups.Select(x => GroupNameKey(x.Name)).Distinct(StringComparer.Ordinal).Count() != snapshot.Groups.Count ||
            snapshot.Snippets.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Snippets.Count ||
            snapshot.History.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.History.Count)
            throw new InvalidDataException("取り込みデータのIDまたはグループ名が重複しています。");
        foreach (var group in snapshot.Groups)
        {
            if (!IsId(group.Id) || group.SortOrder < 0) throw new InvalidDataException("グループIDまたは並び順が不正です。");
            ValidateShort(group.Name, nameof(group.Name), 60);
        }
        foreach (var snippet in snapshot.Snippets)
        {
            if (!IsId(snippet.Id) || (snippet.GroupId is not null && !IsId(snippet.GroupId)) || snippet.SortOrder < 0)
                throw new InvalidDataException("定型文IDまたは並び順が不正です。");
            ValidateShort(snippet.Title, nameof(snippet.Title), 120); ValidateText(snippet.Text, 64 * 1024, nameof(snippet.Text)); ValidateShort(snippet.Group, nameof(snippet.Group), 60);
        }
        foreach (var entry in snapshot.History)
        {
            ValidateOperation(new(entry.Id, "clipboard", entry.CreatedUtc, entry.DeviceId, entry.DeviceName, entry.Kind, entry.Text, entry.ImagePng));
        }
    }

    private async Task CommitBatchAsync(IReadOnlyList<SyncOperation> operations, CancellationToken ct, bool requireAllHistory = false)
    {
        if (operations.Count == 0) return;
        foreach (var operation in operations) ValidateOperation(operation);
        if (_pending.Count + operations.Count > MaxPendingOperations || _pending.Sum(OperationBytes) + operations.Sum(OperationBytes) > MaxPendingBytes)
            throw new InvalidOperationException("未同期データが上限に達しました。OneDriveの接続を確認してから再試行してください。");
        SavedState before;
        DateTimeOffset oldOperationUtc;
        lock (_viewLock)
        {
            before = new(1, _manifest.GroupId, _history.ToList(), _snippets.Values.ToList(), _pending.ToList(), _seen.ToList(),
                _lastFingerprint, _lastClipboardUtc, _lastClipboardId, _historyClearedUtc, _deletedHistory.ToList(), _groups.Values.ToList(), _orders.Values.ToList(), _historyVersions.Values.ToList());
            oldOperationUtc = _lastOperationUtc;
            foreach (var op in operations) { _pending.Add(op); _seen.Add(op.Id); Apply(op); }
        }
        try
        {
            // Imports reject a cache-capacity overflow before SaveState's normal oldest-history trimming.
            if (requireAllHistory)
            {
                SavedState candidate;
                lock (_viewLock) candidate = new(1, _manifest.GroupId, _history.ToList(), _snippets.Values.ToList(), _pending.ToList(), _seen.TakeLast(100_000).ToList(),
                    _lastFingerprint, _lastClipboardUtc, _lastClipboardId, _historyClearedUtc, _deletedHistory.TakeLast(100_000).ToList(), _groups.Values.ToList(), _orders.Values.ToList(), _historyVersions.Values.ToList());
                if (JsonSerializer.SerializeToUtf8Bytes(candidate, JsonOptions).LongLength + 32 > CacheByteLimit)
                    throw new InvalidOperationException("取り込み後のローカル保存容量が上限を超えます。データは変更していません。");
            }
            await SaveStateAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_viewLock)
            {
                _history.Clear(); _history.AddRange(before.History); _snippets.Clear(); foreach (var op in before.Snippets) _snippets[op.SnippetId!] = op;
                _groups.Clear(); foreach (var op in before.Groups!) _groups[op.GroupId!] = op;
                _orders.Clear(); foreach (var op in before.Orders!) _orders[op.Operation + ":" + op.TargetId] = op;
                _historyVersions.Clear(); foreach (var version in before.HistoryVersions!) _historyVersions[version.Id] = version;
                _pending.Clear(); _pending.AddRange(before.Pending); _seen.Clear(); _seen.UnionWith(before.Seen); _deletedHistory.Clear(); _deletedHistory.UnionWith(before.DeletedHistory);
                _lastFingerprint = before.LastFingerprint; _lastClipboardUtc = before.LastClipboardUtc; _lastClipboardId = before.LastClipboardId; _historyClearedUtc = before.HistoryClearedUtc; _lastOperationUtc = oldOperationUtc;
            }
            throw;
        }
        NotifyChanged();
        await PollInternalAsync(false, ct).ConfigureAwait(false);
    }

    private Snippet ToSnippet(SyncOperation op)
    {
        var id = op.GroupId ?? GroupIdForName(op.Group ?? "共通");
        var name = op.Group ?? "共通";
        if (_groups.TryGetValue(id, out var group))
        {
            if (group.Operation == "group-delete") { id = DefaultGroupId; name = "共通"; }
            else name = group.Group!;
        }
        return new(op.SnippetId!, op.Title!, op.Text!, op.CreatedUtc, name, id, OrderOf("snippet-order", op.SnippetId!));
    }

    private long OrderOf(string operation, string id) => _orders.TryGetValue(operation + ":" + id, out var order) ? order.SortOrder!.Value : long.MaxValue;
    private string FindGroupId(string name)
    {
        var existing = GetGroups().FirstOrDefault(x => SameGroupName(x.Name, name));
        if (existing is not null) return existing.Id;
        var id = GroupIdForName(name);
        // A renamed entity retains its ID. Recreating its former name creates a separate identity.
        if (_groups.TryGetValue(id, out var op))
        {
            if (op.Operation == "group-delete")
                return GroupIdForName("deleted:" + op.Id + ":" + name);
            if (!SameGroupName(op.Group!, name)) return GroupIdForName("renamed:" + op.Id + ":" + name);
        }
        return id;
    }
    private static string GroupIdForName(string name) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("clipbridge-group:" + GroupNameKey(name))))[..32].ToLowerInvariant();
    private static string GroupNameKey(string name) => name.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    private static bool SameGroupName(string a, string b) => GroupNameKey(a) == GroupNameKey(b);
}
