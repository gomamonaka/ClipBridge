using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using ClipBridge.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Two separate OneDrive replicas synchronize encrypted text and PNG", TwoReplicas),
    ("Wrong passwords fail without changing the group", WrongPassword),
    ("Concurrent initialization selects one atomic manifest", ConcurrentInitialization),
    ("Corrupt and oversized files do not stop synchronization", Corruption),
    ("Startup and delayed old clipboard events never replace clipboard", NoStaleReplay),
    ("Out-of-order remote clipboard cannot overwrite newer local capture", OutOfOrder),
    ("Snippet conflicts and deletion converge", SnippetConflicts),
    ("Snippet groups synchronize, converge with content and survive restart", SnippetGroups),
    ("Legacy snippet events without group use the common group", LegacySnippetGroups),
    ("Snippet group validation protects local and imported operations", SnippetGroupValidation),
    ("History delete and clear synchronize and persist", HistoryDeletion),
    ("Offline outbox survives restart and retries", OfflinePersistence),
    ("Capture validation, deduplication and immutable snapshots", ValidationAndDedup),
    ("Single-entry history lookup isolates payloads without copying unrelated images", SingleHistoryLookup),
    ("History is bounded to 2000 entries", BoundedHistory),
    ("Only authenticated clipboard events expire after seven days", Retention),
    ("Escaped control text obeys serialized cache budget and reopens", SerializedCacheBudget),
    ("Corrupt same-ID destination retains and retries the durable outbox", CorruptOutboxDestination),
    ("A streamed poll applies only latest live clipboard and honors later deletes", StreamedClipboardNotifications),
    ("Empty groups, stable renames and independent ordering synchronize", GroupManagement),
    ("Deleting and recreating a group does not resurrect its previous members", DeletedGroupIdentity),
    ("Group-management transactions allow name swaps and reject partial failures", GroupLayoutTransaction),
    ("Group-management editing saves titles, bodies, moves and order together", GroupLayoutEditing),
    ("Group-management editing preserves concurrent changes to untouched items", GroupLayoutUntouchedRemote),
    ("Conflicting group-management edits reject every planned change atomically", GroupLayoutEditConflict),
    ("Group-management editing validates original IDs and complete final content", GroupLayoutOriginalValidation),
    ("Group-management rejects concurrent group additions, renames and deletions", GroupLayoutGroupConflict),
    ("History edits synchronize without changing the live clipboard", HistoryEditing),
    ("Batch migration preserves IDs and dates without replaying history", SnapshotImport),
    ("Older clear events cannot remove newer backfills with old displayed dates", BackfillClearOrder),
    ("Failed imports leave encrypted state and in-memory views unchanged", AtomicImportFailure),
    ("Portable encrypted backups restore across new sharing groups", PortableBackup),
    ("Restoring a backup recovers deleted snippets and history", DeletedBackupRestore),
    ("Text formatting preserves Japanese and previews bounded replacements", TextFormatting),
};
var failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}\n{ex}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} checks passed.");
return failed == 0 ? 0 : 1;

static async Task TwoReplicas()
{
    using var env = new TestEnvironment();
    await using var a = await env.OpenA();
    env.ReplicateAtoB();
    await using var b = await env.OpenB();
    var received = new List<ClipboardEntry>();
    b.RemoteClipboardReceived += received.Add;
    await a.CaptureTextAsync("秘密のテキスト — sync test");
    env.ReplicateAtoB();
    await b.PollAsync();
    Assert(received.Count == 1 && received[0].Text == "秘密のテキスト — sync test", "live clipboard notification");
    Assert(b.GetHistory().Single().Text == received[0].Text, "history text");
    Assert(await b.CaptureTextAsync(received[0].Text!) is null, "remote echo is suppressed");
    await b.PollAsync();
    Assert(received.Count == 1, "poll is idempotent");
    var png = Png();
    await a.CaptureImageAsync(png);
    env.ReplicateAtoB();
    await b.PollAsync();
    Assert(received.Count == 2 && received[1].ImagePng!.SequenceEqual(png), "image payload");
    foreach (var path in Directory.GetFiles(env.SharedA, "*", SearchOption.AllDirectories).Concat(Directory.GetFiles(env.LocalA)))
        Assert(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains("秘密のテキスト"), "no plaintext in shared folder or cache");
}

static async Task WrongPassword()
{
    using var env = new TestEnvironment();
    await using var a = await env.OpenA();
    var before = File.ReadAllBytes(Path.Combine(env.SharedA, "clipbridge.group.json"));
    await Throws<InvalidPassphraseException>(() => SyncEngine.OpenAsync(env.SharedA, "incorrect password", env.LocalB, "b", "端末B"));
    Assert(File.ReadAllBytes(Path.Combine(env.SharedA, "clipbridge.group.json")).SequenceEqual(before), "wrong password cannot mutate manifest");
}

static async Task ConcurrentInitialization()
{
    using var env = new TestEnvironment();
    var tasks = Enumerable.Range(0, 6).Select(i => Task.Run(() => SyncEngine.OpenAsync(env.SharedA, TestEnvironment.Passphrase, Path.Combine(env.Root, "local" + i), "device" + i, "端末" + i))).ToArray();
    var engines = await Task.WhenAll(tasks);
    try
    {
        await engines[0].CaptureTextAsync("atomic initialization");
        foreach (var engine in engines.Skip(1))
        {
            await engine.PollAsync();
            Assert(engine.GetHistory().Single().Text == "atomic initialization", "all concurrent engines use same key");
        }
        Assert(Directory.GetFiles(env.SharedA, "*.tmp").Length == 0, "temporary manifest files cleaned");
    }
    finally { foreach (var engine in engines) await engine.DisposeAsync(); }
}

static async Task Corruption()
{
    using var env = new TestEnvironment();
    await using var a = await env.OpenA();
    env.ReplicateAtoB();
    await using var b = await env.OpenB();
    var events = Path.Combine(env.SharedB, "events");
    Directory.CreateDirectory(events);
    await File.WriteAllTextAsync(Path.Combine(events, Guid.NewGuid().ToString("N") + ".cbe"), "broken encrypted payload");
    await using (var file = File.Create(Path.Combine(events, Guid.NewGuid().ToString("N") + ".cbe"))) file.SetLength(13 * 1024 * 1024);
    await File.WriteAllTextAsync(Path.Combine(events, "..invalid.cbe"), "ignored invalid filename");
    await a.CaptureTextAsync("valid after corrupt files");
    env.ReplicateAtoB();
    await b.PollAsync();
    Assert(b.GetHistory().Single().Text == "valid after corrupt files", "good event still imported");
    Assert(b.Status.IsOnline && b.Status.Message.Contains("2"), "corrupt files reported");
    await b.PollAsync();
    Assert(b.GetHistory().Count == 1, "corrupt retry is safe");
    // Ciphertext copied under a different valid ID cannot be misattributed.
    var source = Directory.GetFiles(Path.Combine(env.SharedA, "events")).Single();
    File.Copy(source, Path.Combine(events, Guid.NewGuid().ToString("N") + ".cbe"));
    await b.PollAsync();
    Assert(b.GetHistory().Count == 1, "filename/payload IDs validated");
}

static async Task NoStaleReplay()
{
    using var env = new TestEnvironment();
    await using var a = await env.OpenA();
    await a.CaptureTextAsync("old event");
    env.ReplicateAtoB(includeEvents: false);
    await using var b = await env.OpenB();
    var received = 0;
    b.RemoteClipboardReceived += _ => received++;
    env.ReplicateAtoB();
    await b.PollAsync();
    Assert(b.GetHistory().Count == 1 && received == 0, "old files arriving after startup import silently");
    await a.CaptureTextAsync("new live event");
    env.ReplicateAtoB();
    await b.PollAsync();
    Assert(received == 1, "live event arrives");
    await b.DisposeAsync();
    await using var restarted = await env.OpenB();
    restarted.RemoteClipboardReceived += _ => received++;
    await restarted.PollAsync();
    Assert(received == 1 && restarted.GetHistory().Count == 2, "restart preserves history without replay");
}

static async Task OutOfOrder()
{
    using var env = new TestEnvironment();
    await using var a = await env.OpenA();
    env.ReplicateAtoB();
    await using var b = await env.OpenB();
    await a.CaptureTextAsync("older remote");
    await b.CaptureTextAsync("newer local");
    var received = 0;
    b.RemoteClipboardReceived += _ => received++;
    env.ReplicateAtoB();
    await b.PollAsync();
    Assert(received == 0 && b.GetHistory()[0].Text == "newer local", "older event cannot clobber local clipboard");
}

static async Task SnippetConflicts()
{
    using var env = new TestEnvironment();
    await using var a = await env.OpenA();
    env.ReplicateAtoB();
    await using var b = await env.OpenB();
    var snippet = await a.UpsertSnippetAsync(null, "挨拶", "いつもありがとうございます。");
    env.ReplicateAtoB(); await b.PollAsync();
    await a.UpsertSnippetAsync(snippet.Id, "端末A", "first update");
    await b.UpsertSnippetAsync(snippet.Id, "端末B", "second update");
    env.ReplicateAtoB(); env.ReplicateBtoA(); await a.PollAsync(); await b.PollAsync();
    Assert(a.GetSnippets().Single().Text == "second update" && b.GetSnippets().Single().Text == "second update", "last writer converges");
    await a.DeleteSnippetAsync(snippet.Id);
    env.ReplicateAtoB(); await b.PollAsync();
    Assert(a.GetSnippets().Count == 0 && b.GetSnippets().Count == 0, "delete converges");
    await b.DisposeAsync();
    await using var reopened = await env.OpenB();
    Assert(reopened.GetSnippets().Count == 0, "tombstone survives restart");
}

static async Task SnippetGroups()
{
    using var env = new TestEnvironment();
    await using var a = await env.OpenA();
    env.ReplicateAtoB();
    await using var b = await env.OpenB();
    var snippet = await a.UpsertSnippetAsync(null, "挨拶", "initial grouped snippet", "  メール  ");
    Assert(snippet.Group == "メール", "group whitespace is trimmed");
    env.ReplicateAtoB(); await b.PollAsync();
    Assert(b.GetSnippets().Single().Group == "メール", "named group synchronizes");
    await b.UpsertSnippetAsync(snippet.Id, "挨拶の更新", "edit through legacy API", CancellationToken.None);
    Assert(b.GetSnippets().Single().Group == "メール", "legacy overload preserves an existing group");
    env.ReplicateBtoA(); await a.PollAsync();
    var first = await a.UpsertSnippetAsync(snippet.Id, "端末A", "first content", "仕事");
    var second = await b.UpsertSnippetAsync(snippet.Id, "端末B", "second content", "私用");
    Assert(second.UpdatedUtc > first.UpdatedUtc, "fixture has an ordered conflict");
    env.ReplicateAtoB(); env.ReplicateBtoA(); await a.PollAsync(); await b.PollAsync();
    foreach (var engine in new[] { a, b })
    {
        var current = engine.GetSnippets().Single();
        Assert(current.Group == "私用" && current.Title == "端末B" && current.Text == "second content", "group and content converge as one version");
    }
    await b.DisposeAsync();
    await using var reopened = await env.OpenB();
    Assert(reopened.GetSnippets().Single().Group == "私用", "group survives encrypted cache restart");
    foreach (var path in Directory.GetFiles(env.SharedA, "*", SearchOption.AllDirectories).Concat(Directory.GetFiles(env.LocalA)))
        Assert(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains("私用"), "group names remain encrypted");
}

static async Task LegacySnippetGroups()
{
    using var env = new TestEnvironment();
    await using var a = await env.OpenA();
    var id = Guid.NewGuid().ToString("N");
    var snippetId = Guid.NewGuid().ToString("N");
    WriteSnippetFixture(env, new
    {
        Id = id, Operation = "snippet-upsert", CreatedUtc = DateTimeOffset.UtcNow,
        DeviceId = "legacy", DeviceName = "1.0.0", SnippetId = snippetId, Title = "以前の定型文", Text = "old content"
    }, id);
    await a.PollAsync();
    Assert(a.GetSnippets().Single().Group == "共通", "missing group in a version 1.0.0 event maps to common");
    env.ReplicateAtoB();
    await using var b = await env.OpenB();
    Assert(b.GetSnippets().Single().Group == "共通", "old event imports during replica startup");
    await a.DisposeAsync();
    await using var reopened = await env.OpenA();
    Assert(reopened.GetSnippets().Single().Group == "共通", "old operation without group reopens from cache");
    var edited = await reopened.UpsertSnippetAsync(snippetId, "以前の定型文", "new content");
    Assert(edited.Group == "共通", "editing an old event preserves its effective group");
    Assert(new Snippet(snippetId, "old constructor", "text", DateTimeOffset.UtcNow).Group == "共通", "old constructor remains compatible");

    // A 1.0 outbox may have written its immutable event before acknowledging it locally.
    // Null Group must stay absent on reserialization or the exact-payload confirmation would fail.
    using var migration = new TestEnvironment();
    var beforeUpgrade = await migration.OpenA();
    var hidden = migration.SharedA + "-offline";
    Directory.Move(migration.SharedA, hidden);
    var pending = (await beforeUpgrade.CaptureTextAsync("legacy pending clipboard"))!;
    await beforeUpgrade.DisposeAsync();
    Directory.Move(hidden, migration.SharedA);
    WriteSnippetFixture(migration, new
    {
        Id = pending.Id, Operation = "clipboard", CreatedUtc = pending.CreatedUtc,
        pending.DeviceId, pending.DeviceName, Kind = (ClipboardKind?)pending.Kind, pending.Text,
        ImagePng = (byte[]?)null, SnippetId = (string?)null, Title = (string?)null, TargetId = (string?)null
    }, pending.Id, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    await using var afterUpgrade = await migration.OpenA();
    Assert(afterUpgrade.Status.IsOnline && afterUpgrade.Status.PendingCount == 0 && afterUpgrade.GetHistory().Single().Text == pending.Text,
        "old immutable event confirms and drains upgraded pending outbox");
}

static async Task SnippetGroupValidation()
{
    using var env = new TestEnvironment();
    await using var a = await env.OpenA();
    foreach (var group in new[] { "", "   ", "mail\nwork", "mail\0work", new string('x', 61) })
        await Throws<ArgumentException>(() => a.UpsertSnippetAsync(null, "invalid group", "content", group));
    var longest = await a.UpsertSnippetAsync(null, "valid group", "content", new string('あ', 60));
    Assert(longest.Group.Length == 60, "sixty-character group is supported");
    var common = await a.UpsertSnippetAsync(null, "default group", "content");
    Assert(common.Group == "共通", "legacy creation uses common group");
    var id = Guid.NewGuid().ToString("N");
    WriteSnippetFixture(env, new
    {
        Id = id, Operation = "snippet-upsert", CreatedUtc = DateTimeOffset.UtcNow,
        DeviceId = "invalid-remote", DeviceName = "Fixture", SnippetId = Guid.NewGuid().ToString("N"),
        Title = "malformed group", Text = "content", Group = new string('x', 61)
    }, id);
    await a.PollAsync();
    Assert(a.GetSnippets().Count == 2 && a.Status.IsOnline && a.Status.Message.Contains("1"), "malformed remote group is held without stopping sync");
}

static void WriteSnippetFixture(TestEnvironment env, object payload, string id, JsonSerializerOptions? options = null)
{
    var manifest = JsonSerializer.Deserialize<GroupManifest>(File.ReadAllBytes(Path.Combine(env.SharedA, "clipbridge.group.json")))!;
    var key = Crypto.Derive(TestEnvironment.Passphrase, Convert.FromBase64String(manifest.Salt));
    try
    {
        var encrypted = Crypto.Encrypt(JsonSerializer.SerializeToUtf8Bytes(payload, options), key, "event:" + manifest.GroupId);
        File.WriteAllBytes(Path.Combine(env.SharedA, "events", id + ".cbe"), encrypted);
    }
    finally { CryptographicOperations.ZeroMemory(key); }
}

static async Task HistoryDeletion()
{
    using var env = new TestEnvironment();
    await using var a = await env.OpenA();
    env.ReplicateAtoB();
    await using var b = await env.OpenB();
    var entry = await a.CaptureTextAsync("to delete");
    env.ReplicateAtoB(); await b.PollAsync();
    await b.DeleteHistoryAsync(entry!.Id);
    env.ReplicateBtoA(); await a.PollAsync();
    Assert(a.GetHistory().Count == 0 && b.GetHistory().Count == 0, "single history deletion");
    await a.CaptureTextAsync("to clear");
    env.ReplicateAtoB(); await b.PollAsync();
    await b.ClearHistoryAsync();
    env.ReplicateBtoA(); await a.PollAsync();
    Assert(a.GetHistory().Count == 0 && b.GetHistory().Count == 0, "clear cutoff");
    await a.CaptureTextAsync("new after clear");
    env.ReplicateAtoB(); await b.PollAsync();
    Assert(b.GetHistory().Single().Text == "new after clear", "new history after clear");
}

static async Task OfflinePersistence()
{
    using var env = new TestEnvironment();
    var a = await env.OpenA();
    env.ReplicateAtoB();
    await using var b = await env.OpenB();
    var hidden = env.SharedA + "-unavailable";
    Directory.Move(env.SharedA, hidden);
    await a.CaptureTextAsync("saved offline and after restart");
    await a.UpsertSnippetAsync(null, "offline snippet", "queued snippet");
    Assert(!a.Status.IsOnline && a.Status.PendingCount == 2, "offline queue status");
    await a.DisposeAsync();
    a = await env.OpenA();
    Assert(!a.Status.IsOnline && a.GetHistory().Single().Text == "saved offline and after restart", "open from encrypted cache while offline");
    Assert(a.GetSnippets().Count == 1, "offline snippets survive");
    Directory.Move(hidden, env.SharedA);
    await a.PollAsync();
    Assert(a.Status.IsOnline && a.Status.PendingCount == 0, "outbox drains after reconnect");
    env.ReplicateAtoB(); await b.PollAsync();
    Assert(b.GetHistory().Single().Text == "saved offline and after restart" && b.GetSnippets().Count == 1, "retried entries arrive");
    await a.DisposeAsync();
}

static async Task ValidationAndDedup()
{
    using var env = new TestEnvironment();
    await using var a = await env.OpenA();
    Assert(await a.CaptureTextAsync("") is null, "empty capture ignored");
    await Throws<ArgumentException>(() => a.CaptureTextAsync(new string('x', SyncEngine.MaxTextBytes + 1)));
    await Throws<ArgumentException>(() => a.CaptureImageAsync([1, 2, 3]));
    await Throws<ArgumentException>(() => SyncEngine.OpenAsync(env.SharedA, "short", env.LocalB, "b", "端末B"));
    var largeDimensions = Png();
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(largeDimensions.AsSpan(16, 4), 10000);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(largeDimensions.AsSpan(20, 4), 4000);
    await Throws<ArgumentException>(() => a.CaptureImageAsync(largeDimensions));
    await Throws<ArgumentException>(() => a.UpsertSnippetAsync("../../evil", "bad", "bad"));
    await Throws<ArgumentException>(() => a.DeleteHistoryAsync("../../evil"));
    await a.CaptureTextAsync("same text");
    Assert(await a.CaptureTextAsync("same text") is null && a.GetHistory().Count == 1, "same clipboard value deduplicates");
    var png = Png();
    await a.CaptureImageAsync(png);
    var snapshot = a.GetHistory();
    snapshot[0].ImagePng![0] = 0;
    png[1] = 0;
    Assert(a.GetHistory()[0].ImagePng![0] == 137 && a.GetHistory()[0].ImagePng![1] == 80, "snapshots cannot mutate internal payload");
}

static async Task SingleHistoryLookup()
{
    using var env = new TestEnvironment(); await using var a = await env.OpenA();
    var text = (await a.CaptureTextAsync("lookup target 日本語"))!;
    var png = new byte[1024 * 1024]; Png().CopyTo(png, 0);
    var image = (await a.CaptureImageAsync(png))!;
    var other = (await a.CaptureTextAsync("another lookup target"))!;
    Assert(a.GetHistoryEntry(text.Id) == text && a.GetHistoryEntry(other.Id) == other, "lookup returns the exact requested text record with metadata");
    var firstImage = a.GetHistoryEntry(image.Id)!; var secondImage = a.GetHistoryEntry(image.Id)!;
    Assert(firstImage.Kind == ClipboardKind.Image && firstImage.Id == image.Id && firstImage.CreatedUtc == image.CreatedUtc && firstImage.ImagePng!.SequenceEqual(png),
        "image lookup preserves the complete requested payload and metadata");
    firstImage.ImagePng![0] = 0; png[1] = 0;
    Assert(secondImage.ImagePng![0] == 137 && a.GetHistoryEntry(image.Id)!.ImagePng![0] == 137 && a.GetHistoryEntry(image.Id)!.ImagePng![1] == 80,
        "mutating a returned image or original capture buffer cannot change another snapshot or stored data");
    _ = a.GetHistoryEntry(text.Id); // Warm the synchronous lookup before measuring its allocation cost.
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < 8; index++) _ = a.GetHistoryEntry(text.Id);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Assert(allocated < 64 * 1024, "text lookup does not clone the unrelated one-megabyte image in history");
    Assert(a.GetHistoryEntry(Guid.NewGuid().ToString("N")) is null && a.GetHistoryEntry("missing") is null, "missing IDs return null");
    await a.DeleteHistoryAsync(text.Id);
    Assert(a.GetHistoryEntry(text.Id) is null && a.GetHistoryEntry(image.Id) is not null, "deleted IDs return null without affecting another entry");
    var edited = await a.EditHistoryAsync(other.Id, "edited lookup target");
    Assert(a.GetHistoryEntry(other.Id) is null && a.GetHistoryEntry(edited.Id) == edited, "lookup reflects history replacement and does not return its deleted source");
    await a.ClearHistoryAsync();
    Assert(a.GetHistoryEntry(image.Id) is null && a.GetHistoryEntry(edited.Id) is null, "cleared entries are no longer available by ID");
}

static async Task BoundedHistory()
{
    using var env = new TestEnvironment();
    await using var a = await env.OpenA();
    var older = DateTimeOffset.UtcNow.AddMinutes(-1);
    var imported = Enumerable.Range(0, SyncEngine.MaxHistoryEntries).Select(i => new ClipboardEntry(Guid.NewGuid().ToString("N"), ClipboardKind.Text, "item " + i, null, older.AddTicks(i), "fixture", "Fixture")).ToArray();
    await a.ImportSnapshotAsync(new(1, DateTimeOffset.UtcNow, [], [], imported));
    for (var i = SyncEngine.MaxHistoryEntries; i < SyncEngine.MaxHistoryEntries + 10; i++) await a.CaptureTextAsync("item " + i);
    Assert(a.GetHistory().Count == SyncEngine.MaxHistoryEntries && a.GetHistory()[0].Text == "item " + (SyncEngine.MaxHistoryEntries + 9) && a.GetHistory()[^1].Text == "item 10", "bounded most recent history");
}

static async Task Retention()
{
    using var env = new TestEnvironment();
    await using (var engine = await env.OpenA()) { }
    using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(env.SharedA, "clipbridge.group.json")));
    var groupId = manifest.RootElement.GetProperty("GroupId").GetString()!;
    var salt = Convert.FromBase64String(manifest.RootElement.GetProperty("Salt").GetString()!);
    var key = Rfc2898DeriveBytes.Pbkdf2(TestEnvironment.Passphrase, salt, 210_000, HashAlgorithmName.SHA256, 32);
    var events = Path.Combine(env.SharedA, "events");
    var oldDate = DateTimeOffset.UtcNow.AddDays(-8);
    string MakeFixture(string operation, DateTimeOffset created, string? snippetId = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Id = id, Operation = operation, CreatedUtc = created, DeviceId = "fixture", DeviceName = "Fixture",
            Kind = operation == "clipboard" ? (int?)0 : null,
            Text = operation == "clipboard" ? "old clipboard fixture" : operation == "snippet-upsert" ? "retained snippet" : null,
            SnippetId = snippetId, Title = operation == "snippet-upsert" ? "Retained" : null,
        });
        var bytes = new byte[32 + payload.Length];
        "CB01"u8.CopyTo(bytes);
        RandomNumberGenerator.Fill(bytes.AsSpan(4, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(bytes.AsSpan(4, 12), payload, bytes.AsSpan(32), bytes.AsSpan(16, 16), Encoding.UTF8.GetBytes("event:" + groupId));
        var path = Path.Combine(events, id + ".cbe");
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, oldDate.UtcDateTime);
        return path;
    }
    var oldClipboard = MakeFixture("clipboard", oldDate);
    var recentClipboard = MakeFixture("clipboard", DateTimeOffset.UtcNow.AddHours(-1));
    var oldSnippet = MakeFixture("snippet-upsert", oldDate, Guid.NewGuid().ToString("N"));
    var oldDelete = MakeFixture("snippet-delete", oldDate, Guid.NewGuid().ToString("N"));
    var corrupt = Path.Combine(events, Guid.NewGuid().ToString("N") + ".cbe");
    File.WriteAllText(corrupt, "not an authenticated event"); File.SetLastWriteTimeUtc(corrupt, oldDate.UtcDateTime);
    await using var reopened = await env.OpenA();
    Assert(!File.Exists(oldClipboard), "old authenticated clipboard expires");
    Assert(File.Exists(recentClipboard), "new clipboard survives even if file timestamp is old");
    Assert(File.Exists(oldSnippet) && File.Exists(oldDelete) && File.Exists(corrupt), "snippets, tombstones and unauthenticated files never pruned");
    Assert(reopened.GetSnippets().Count == 1 && reopened.GetHistory().Count == 2, "pruning preserves imported local history and snippets");
    CryptographicOperations.ZeroMemory(key);
}

static async Task SerializedCacheBudget()
{
    using var env = new TestEnvironment();
    var a = await env.OpenA();
    a.CacheByteLimit = 64 * 1024;
    await a.UpsertSnippetAsync(null, "preserved snippet", "durable snippet");
    // Each raw2KiB string expands to roughly12KiB JSON. History should trim before writing an unreadable cache.
    for (var i = 0; i < 12; i++) await a.CaptureTextAsync(i + new string('\u0001', 2048));
    Assert(a.GetHistory().Count is > 0 and < 12, "serialized history budget trims old records");
    Assert(new FileInfo(Path.Combine(env.LocalA, "state.cbc")).Length <= 64 * 1024, "encrypted cache respects exact serialized byte budget");
    var expectedNewest = a.GetHistory()[0].Text;
    await a.DisposeAsync();
    await using var reopened = await env.OpenA();
    Assert(reopened.GetHistory()[0].Text == expectedNewest && reopened.GetSnippets().Single().Text == "durable snippet", "bounded cache decrypts after restart");
}

static async Task CorruptOutboxDestination()
{
    using var env = new TestEnvironment();
    var a = await env.OpenA();
    var hidden = env.SharedA + "-offline";
    Directory.Move(env.SharedA, hidden);
    var entry = await a.CaptureTextAsync("must never be lost from outbox");
    Directory.Move(hidden, env.SharedA);
    var destination = Path.Combine(env.SharedA, "events", entry!.Id + ".cbe");
    await File.WriteAllTextAsync(destination, "cloud conflict corruption");
    await a.PollAsync();
    Assert(!a.Status.IsOnline && a.Status.PendingCount == 1, "corrupt destination cannot acknowledge queued operation");
    await a.DisposeAsync();
    a = await env.OpenA();
    Assert(a.Status.PendingCount == 1 && a.GetHistory().Single().Text == entry.Text, "unsent capture survives restart");
    File.Delete(destination);
    await a.PollAsync();
    Assert(a.Status.IsOnline && a.Status.PendingCount == 0 && File.Exists(destination), "outbox retries after conflict clears");
    await a.DisposeAsync();
}

static async Task StreamedClipboardNotifications()
{
    using var env = new TestEnvironment();
    await using var a = await env.OpenA();
    env.ReplicateAtoB();
    await using var b = await env.OpenB();
    var received = new List<ClipboardEntry>();
    b.RemoteClipboardReceived += received.Add;
    for (var i = 0; i < 8; i++) await a.CaptureTextAsync("batch " + i);
    env.ReplicateAtoB(); await b.PollAsync();
    Assert(received.Count == 1 && received[0].Text == "batch 7" && b.GetHistory().Count == 8, "streaming retains ordered history and only notifies newest");
    var deleted = await a.CaptureTextAsync("deleted before delivery");
    await a.DeleteHistoryAsync(deleted!.Id);
    env.ReplicateAtoB(); await b.PollAsync();
    Assert(received.Count == 1 && b.GetHistory().All(x => x.Id != deleted.Id), "same-batch deleted clipboard does not notify");
    await a.CaptureTextAsync("cleared before delivery");
    await a.ClearHistoryAsync();
    env.ReplicateAtoB(); await b.PollAsync();
    Assert(received.Count == 1 && b.GetHistory().Count == 0, "same-batch history clear suppresses stale notification");
}

static async Task GroupManagement()
{
    using var env = new TestEnvironment();
    await using var a = await env.OpenA(); env.ReplicateAtoB(); await using var b = await env.OpenB();
    var mail = await a.UpsertGroupAsync(null, "メール");
    var empty = await a.UpsertGroupAsync(null, "空のグループ");
    var snippet = await a.UpsertSnippetAsync(null, "A", "mail body", "メール");
    var other = await a.UpsertSnippetAsync(null, "Z", "other body", "空のグループ");
    await a.ReorderGroupsAsync([empty.Id, mail.Id]);
    await a.ReorderSnippetsAsync([other.Id, snippet.Id]);
    env.ReplicateAtoB(); await b.PollAsync();
    Assert(b.GetGroups()[0].Id == empty.Id && b.GetSnippets()[0].Id == other.Id, "group and snippet order arrive");
    await b.RenameGroupAsync(mail.Id, "連絡文");
    // A later content edit made on the other, still-stale replica must keep the renamed identity.
    await a.UpsertSnippetAsync(snippet.Id, "A edited", "edited text", "メール");
    env.ReplicateAtoB(); env.ReplicateBtoA(); await a.PollAsync(); await b.PollAsync();
    foreach (var engine in new[] { a, b })
    {
        Assert(engine.GetSnippets().Single(x => x.Id == snippet.Id).Group == "連絡文", "content edit cannot undo group rename");
        Assert(engine.GetSnippets()[0].Id == other.Id, "edit preserves independent custom order");
    }
    await a.DeleteSnippetAsync(other.Id); env.ReplicateAtoB(); await b.PollAsync();
    Assert(b.GetGroups().Any(x => x.Id == empty.Id), "a last-snippet deletion preserves the group");
    await b.DisposeAsync(); await using var reopened = await env.OpenB();
    Assert(reopened.GetGroups()[0].Id == empty.Id && reopened.GetGroups().Any(x => x.Name == "連絡文"), "groups and ordering survive restart");
    await Throws<ArgumentException>(() => a.RenameGroupAsync(mail.Id, "空のグループ"));
    await Throws<ArgumentException>(() => a.DeleteGroupAsync(SyncEngine.DefaultGroupId));
}

static async Task DeletedGroupIdentity()
{
    using var env = new TestEnvironment(); await using var a = await env.OpenA();
    var group = await a.UpsertGroupAsync(null, "仕事");
    var snippet = await a.UpsertSnippetAsync(null, "仕事の文", "old member", group.Name);
    await a.DeleteGroupAsync(group.Id);
    Assert(a.GetSnippets().Single().Group == "共通" && a.GetGroups().All(x => x.Id != group.Id), "delete moves effective members to common");
    var recreated = await a.UpsertGroupAsync(null, "仕事");
    Assert(recreated.Id != group.Id && a.GetSnippets().Single().Group == "共通", "recreate has a fresh durable identity");
    await a.UpsertSnippetAsync(null, "新しい文", "new member", recreated.Name);
    env.ReplicateAtoB(); await using var b = await env.OpenB();
    Assert(b.GetSnippets().Single(x => x.Id == snippet.Id).Group == "共通" && b.GetSnippets().Single(x => x.Text == "new member").Group == "仕事", "identity semantics survive event replay");
}

static async Task GroupLayoutTransaction()
{
    using var env = new TestEnvironment(); await using var a = await env.OpenA();
    var first = await a.UpsertGroupAsync(null, "A"); var second = await a.UpsertGroupAsync(null, "B"); var removed = await a.UpsertGroupAsync(null, "C");
    var snippet = await a.UpsertSnippetAsync(null, "Original title", "Preserved text", first.Name);
    var common = a.GetGroups().Single(x => x.Id == SyncEngine.DefaultGroupId);
    var finalGroups = new[] { common with { SortOrder = 3 }, first with { Name = "B", SortOrder = 1 }, second with { Name = "A", SortOrder = 2 }, new SnippetGroup(Guid.NewGuid().ToString("N"), "C", 0, DateTimeOffset.UtcNow) };
    var finalSnippets = new[] { snippet with { GroupId = second.Id, Group = "A", Title = "Stale changed title", Text = "must not overwrite actual text", SortOrder = 0 } };
    var changes = 0; a.Changed += () => changes++;
    await a.ApplyGroupLayoutAsync(finalGroups, finalSnippets);
    var actual = a.GetSnippets().Single();
    Assert(actual.GroupId == second.Id && actual.Group == "A" && actual.Title == "Original title" && actual.Text == "Preserved text", "moves preserve current content while swapping names");
    Assert(a.GetGroups()[0].Name == "C" && a.GetGroups().All(x => x.Id != removed.Id), "deleted name can be reused atomically");
    env.ReplicateAtoB(); await using var b = await env.OpenB();
    Assert(b.GetSnippets().Single().GroupId == second.Id && b.GetGroups().Select(x => x.Name).SequenceEqual(a.GetGroups().Select(x => x.Name)), "transaction converges on fresh replay");
    var before = a.GetGroups().Select(x => (x.Id, x.Name)).ToArray(); a.CacheByteLimit = 32;
    await Throws<InvalidOperationException>(() => a.ApplyGroupLayoutAsync(finalGroups.Select(x => x.Id == first.Id ? x with { Name = "Renamed" } : x).ToArray(), a.GetSnippets()));
    Assert(before.SequenceEqual(a.GetGroups().Select(x => (x.Id, x.Name))), "failed transaction preserves all group names and IDs");
    Assert(changes > 0, "successful transaction publishes change notification");
}

static async Task GroupLayoutEditing()
{
    using var env = new TestEnvironment(); var a = await env.OpenA();
    var destination = await a.UpsertGroupAsync(null, "移動先");
    var edited = await a.UpsertSnippetAsync(null, "変更前タイトル", "変更前本文");
    var other = await a.UpsertSnippetAsync(null, "別の定型文", "別の本文");
    env.ReplicateAtoB(); await using var b = await env.OpenB();
    var received = new List<ClipboardEntry>(); b.RemoteClipboardReceived += received.Add;
    var original = a.GetSnapshot();
    var groups = original.Groups.Select((x, index) => x with { SortOrder = index }).ToArray();
    var final = new[]
    {
        edited with { Title = "編集したタイトル", Text = "編集した本文\r\n日本語と絵文字🐈", Group = destination.Name, GroupId = destination.Id, SortOrder = 0 },
        other with { SortOrder = 1 }
    };
    await a.ApplyGroupLayoutAsync(groups, final, original.Snippets, original.Groups);
    var actual = a.GetSnippets()[0];
    Assert(actual.Id == edited.Id && actual.Title == final[0].Title && actual.Text == final[0].Text && actual.GroupId == destination.Id && actual.SortOrder == 0,
        "content edit, move and order are saved together");
    env.ReplicateAtoB(); await b.PollAsync();
    Assert(b.GetSnippets()[0] == actual && received.Count == 0, "edited layout synchronizes without touching live clipboard");
    await a.DisposeAsync(); a = await env.OpenA();
    Assert(a.GetSnippets()[0] == actual, "edited title and body survive encrypted cache restart");
    await a.DisposeAsync();
}

static async Task GroupLayoutUntouchedRemote()
{
    using var env = new TestEnvironment(); await using var a = await env.OpenA();
    var destination = await a.UpsertGroupAsync(null, "移動先");
    var local = await a.UpsertSnippetAsync(null, "ローカルで編集", "before local");
    var remote = await a.UpsertSnippetAsync(null, "別端末で編集", "before remote");
    var original = a.GetSnapshot();
    env.ReplicateAtoB(); await using var b = await env.OpenB();
    await b.UpsertSnippetAsync(remote.Id, "別端末の新タイトル", "new remote content");
    env.ReplicateBtoA(); await a.PollAsync();
    var final = original.Snippets.Select((x, index) => x.Id == local.Id ? x with { Title = "ローカルの新タイトル", Text = "new local content", SortOrder = index } :
        x with { Group = destination.Name, GroupId = destination.Id, SortOrder = index }).ToArray();
    await a.ApplyGroupLayoutAsync(original.Groups, final, original.Snippets, original.Groups);
    Assert(a.GetSnippets().Single(x => x.Id == local.Id).Text == "new local content", "intentionally edited item is saved");
    var unchanged = a.GetSnippets().Single(x => x.Id == remote.Id);
    Assert(unchanged.Title == "別端末の新タイトル" && unchanged.Text == "new remote content" && unchanged.GroupId == destination.Id,
        "layout-only changes preserve concurrent title and body from the other device");
    env.ReplicateAtoB(); await b.PollAsync();
    Assert(b.GetSnippets().Single(x => x.Id == remote.Id) == unchanged, "preserved remote content and local move converge");
}

static async Task GroupLayoutEditConflict()
{
    using var env = new TestEnvironment(); await using var a = await env.OpenA();
    var group = await a.UpsertGroupAsync(null, "元のグループ");
    var changed = await a.UpsertSnippetAsync(null, "競合するタイトル", "original body", group.Name);
    var another = await a.UpsertSnippetAsync(null, "別の文", "keep body");
    var original = a.GetSnapshot();
    env.ReplicateAtoB(); await using var b = await env.OpenB();
    await b.UpsertSnippetAsync(changed.Id, "別端末で変えたタイトル", "original body", group.Name);
    env.ReplicateBtoA(); await a.PollAsync();
    var before = a.GetSnapshot();
    var stateBefore = await File.ReadAllBytesAsync(Path.Combine(env.LocalA, "state.cbc"));
    int eventCount = Directory.GetFiles(Path.Combine(env.SharedA, "events"), "*.cbe").Length;
    var finalGroups = original.Groups.Select((x, index) => x.Id == group.Id ? x with { Name = "保存してはいけない名前", SortOrder = index } : x with { SortOrder = index }).ToArray();
    var final = original.Snippets.Select((x, index) => x.Id == changed.Id ?
        x with { Text = "ローカルの編集本文", Group = "保存してはいけない名前", SortOrder = index } :
        x with { Title = "保存してはいけないタイトル", SortOrder = index }).ToArray();
    string? conflictMessage = null;
    try { await a.ApplyGroupLayoutAsync(finalGroups, final, original.Snippets); }
    catch (InvalidOperationException ex) { conflictMessage = ex.Message; }
    Assert(conflictMessage?.Contains("管理画面を開き直して", StringComparison.Ordinal) == true, "conflict explains how to recover");
    Assert(a.GetGroups().SequenceEqual(before.Groups) && a.GetSnippets().SequenceEqual(before.Snippets), "conflict rejects names, unrelated edits and ordering together");
    var stateAfter = await File.ReadAllBytesAsync(Path.Combine(env.LocalA, "state.cbc"));
    Assert(stateBefore.SequenceEqual(stateAfter) && Directory.GetFiles(Path.Combine(env.SharedA, "events"), "*.cbe").Length == eventCount,
        "conflict leaves encrypted state and shared operation log unchanged");
    Assert(a.GetSnippets().Single(x => x.Id == another.Id).Text == "keep body", "unrelated content remains intact");
}

static async Task GroupLayoutOriginalValidation()
{
    using var env = new TestEnvironment(); await using var a = await env.OpenA();
    await a.UpsertSnippetAsync(null, "A", "original A"); await a.UpsertSnippetAsync(null, "B", "original B");
    var original = a.GetSnapshot();
    await Throws<ArgumentException>(() => a.ApplyGroupLayoutAsync(original.Groups, original.Snippets, original.Snippets.Take(1).ToArray()));
    await Throws<ArgumentException>(() => a.ApplyGroupLayoutAsync(original.Groups, original.Snippets, [original.Snippets[0], original.Snippets[0]]));
    await Throws<ArgumentException>(() => a.ApplyGroupLayoutAsync(original.Groups, original.Snippets,
        original.Snippets.Select((x, index) => index == 0 ? x with { Id = Guid.NewGuid().ToString("N") } : x).ToArray()));
    await Throws<ArgumentException>(() => a.ApplyGroupLayoutAsync(original.Groups,
        original.Snippets.Select((x, index) => index == 0 ? x with { Text = new string('x', 65537) } : x).ToArray(), original.Snippets));
    Assert(a.GetSnippets().SequenceEqual(original.Snippets), "invalid origins and final content never mutate state");
    // A new active item makes a previously opened dialog stale even though every original item still exists.
    await a.UpsertSnippetAsync(null, "C", "created after dialog opened");
    await Throws<InvalidOperationException>(() => a.ApplyGroupLayoutAsync(original.Groups, original.Snippets, original.Snippets));
    Assert(a.GetSnippets().Count == 3, "stale original ID set cannot remove a newly synchronized item");
}

static async Task GroupLayoutGroupConflict()
{
    foreach (var change in new[] { "add", "rename", "delete" })
    {
        using var env = new TestEnvironment(); await using var a = await env.OpenA();
        var empty = await a.UpsertGroupAsync(null, "元からある空グループ");
        await a.UpsertSnippetAsync(null, "元タイトル", "元本文");
        var original = a.GetSnapshot();
        env.ReplicateAtoB(); await using var b = await env.OpenB();
        string? addedId = null;
        if (change == "add") addedId = (await b.UpsertGroupAsync(null, "別端末で追加した空グループ")).Id;
        else if (change == "rename") await b.RenameGroupAsync(empty.Id, "別端末で変更したグループ名");
        else await b.DeleteGroupAsync(empty.Id);
        env.ReplicateBtoA(); await a.PollAsync();
        var before = a.GetSnapshot();
        var stateBefore = await File.ReadAllBytesAsync(Path.Combine(env.LocalA, "state.cbc"));
        int eventCount = Directory.GetFiles(Path.Combine(env.SharedA, "events"), "*.cbe").Length;
        var finalGroups = original.Groups.Select((x, index) => x.Id == empty.Id ? x with { Name = "保存してはいけない名前", SortOrder = index } : x with { SortOrder = index }).ToArray();
        var final = original.Snippets.Select((x, index) => x with { Title = "保存してはいけないタイトル", Text = "保存してはいけない本文", SortOrder = index }).ToArray();
        string? conflictMessage = null;
        try { await a.ApplyGroupLayoutAsync(finalGroups, final, original.Snippets, original.Groups); }
        catch (InvalidOperationException ex) { conflictMessage = ex.Message; }
        Assert(conflictMessage?.Contains("最新のグループ", StringComparison.Ordinal) == true, change + " explains group baseline conflict");
        Assert(a.GetGroups().SequenceEqual(before.Groups) && a.GetSnippets().SequenceEqual(before.Snippets), change + " rejects complete batch and preserves all remote data");
        if (addedId is not null) Assert(a.GetGroups().Any(x => x.Id == addedId), "new remote empty group is never deleted by stale manager");
        var stateAfter = await File.ReadAllBytesAsync(Path.Combine(env.LocalA, "state.cbc"));
        Assert(stateBefore.SequenceEqual(stateAfter) && Directory.GetFiles(Path.Combine(env.SharedA, "events"), "*.cbe").Length == eventCount,
            change + " does not write cache or shared operations");
    }
}

static async Task HistoryEditing()
{
    using var env = new TestEnvironment(); await using var a = await env.OpenA(); env.ReplicateAtoB(); await using var b = await env.OpenB();
    var original = (await a.CaptureTextAsync("before edit"))!;
    env.ReplicateAtoB(); await b.PollAsync();
    var received = new List<ClipboardEntry>(); b.RemoteClipboardReceived += received.Add;
    var edited = await a.EditHistoryAsync(original.Id, "編集後\r\n日本語");
    env.ReplicateAtoB(); await b.PollAsync();
    Assert(a.GetHistory().Single().Id == edited.Id && b.GetHistory().Single().Text == edited.Text && received.Count == 0, "history edit replaces and synchronizes without clipboard replay");
    await b.DisposeAsync(); await using var reopened = await env.OpenB();
    Assert(reopened.GetHistory().Single().Id == edited.Id, "history edit deletion tombstone survives restart");
    await Throws<ArgumentException>(() => a.EditHistoryAsync(original.Id, "missing"));
    await Throws<ArgumentException>(() => a.EditHistoryAsync(edited.Id, ""));
}

static async Task SnapshotImport()
{
    using var env = new TestEnvironment(); await using var a = await env.OpenA(); env.ReplicateAtoB(); await using var b = await env.OpenB();
    var received = new List<ClipboardEntry>(); b.RemoteClipboardReceived += received.Add;
    var now = DateTimeOffset.UtcNow; var groupId = Guid.NewGuid().ToString("N"); var snippetId = Guid.NewGuid().ToString("N"); var historyId = Guid.NewGuid().ToString("N");
    var snapshot = new ClipBridgeSnapshot(1, now,
        [new(groupId, "移行元", 0, now), new(Guid.NewGuid().ToString("N"), "中身なし", 1, now)],
        [new(snippetId, "移行した文", "quoted,\r\n日本語", now, "移行元", groupId, 2)],
        [new(historyId, ClipboardKind.Text, "以前の履歴", null, now.AddYears(-1), "clibor", "Clibor")]);
    var result = await a.ImportSnapshotAsync(snapshot);
    Assert(result == new ImportResult(2, 1, 1), "migration counts");
    await a.ImportSnapshotAsync(snapshot);
    Assert(a.GetSnippets().Count == 1 && a.GetHistory().Count == 1 && a.GetGroups().Count == 3, "repeat migration is idempotent by identity");
    env.ReplicateAtoB(); await b.PollAsync();
    Assert(received.Count == 0 && b.GetHistory().Single().CreatedUtc == now.AddYears(-1) && b.GetHistory().Single().Id == historyId, "backfill keeps IDs and dates without live notification");
    Assert(b.GetGroups().Any(x => x.Name == "中身なし"), "empty group migrates");
}

static async Task AtomicImportFailure()
{
    using var env = new TestEnvironment(); await using var a = await env.OpenA();
    await a.CaptureTextAsync("keep this"); var before = await File.ReadAllBytesAsync(Path.Combine(env.LocalA, "state.cbc"));
    var now = DateTimeOffset.UtcNow;
    var valid = new ClipBridgeSnapshot(1, now, [], [new(Guid.NewGuid().ToString("N"), "imported", new string('x', 16384), now)], []);
    a.CacheByteLimit = 4096;
    await Throws<InvalidOperationException>(() => a.ImportSnapshotAsync(valid));
    Assert(a.GetSnippets().Count == 0 && a.GetHistory().Single().Text == "keep this", "capacity failure restores views");
    var after = await File.ReadAllBytesAsync(Path.Combine(env.LocalA, "state.cbc"));
    Assert(before.SequenceEqual(after), "capacity failure leaves persisted ciphertext byte-identical");
    a.CacheByteLimit = 256 * 1024 * 1024;
    var invalid = valid with { Snippets = [valid.Snippets[0], valid.Snippets[0]] };
    await Throws<InvalidDataException>(() => a.ImportSnapshotAsync(invalid));
    Assert(a.GetSnippets().Count == 0, "duplicate identity rejected before mutation");
    // Force an atomic file move failure after the entire import has been applied in memory.
    var statePath = Path.Combine(env.LocalA, "state.cbc"); var preservedPath = statePath + ".preserved";
    File.Move(statePath, preservedPath); Directory.CreateDirectory(statePath);
    try { await a.ImportSnapshotAsync(valid); throw new InvalidOperationException("Expected a persistence failure"); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    Assert(a.GetSnippets().Count == 0 && a.GetHistory().Single().Text == "keep this", "disk failure rolls back every view and outbox");
    Directory.Delete(statePath); File.Move(preservedPath, statePath);
}

static async Task BackfillClearOrder()
{
    using var env = new TestEnvironment(); var a = await env.OpenA();
    var now = DateTimeOffset.UtcNow; var entryId = Guid.NewGuid().ToString("N");
    await a.ImportSnapshotAsync(new(1, now, [], [], [new(entryId, ClipboardKind.Text, "old displayed date", null, now.AddYears(-1), "migration", "Clibor")]));
    await a.DisposeAsync(); a = await env.OpenA();
    var clearId = Guid.NewGuid().ToString("N");
    WriteSnippetFixture(env, new SyncOperation(clearId, "history-clear", now.AddMinutes(-1), "old-device", "Fixture"), clearId);
    await a.PollAsync();
    Assert(a.GetHistory().Single().Id == entryId, "causally older clear does not delete newer import after restart");
    await a.ClearHistoryAsync(); Assert(a.GetHistory().Count == 0, "a newer clear still deletes the imported entry");
    await a.DisposeAsync();
}

static async Task PortableBackup()
{
    using var env = new TestEnvironment(); await using var a = await env.OpenA();
    await a.UpsertGroupAsync(null, "空のバックアップグループ");
    await a.UpsertSnippetAsync(null, "秘密", "バックアップに残す日本語"); await a.CaptureTextAsync("backup history");
    var path = Path.Combine(env.Root, "portable.cbb");
    await a.ExportBackupAsync(path, TestEnvironment.Passphrase);
    var encrypted = await File.ReadAllBytesAsync(path);
    Assert(!Encoding.UTF8.GetString(encrypted).Contains("バックアップに残す日本語"), "backup content is encrypted");
    // Open B as a completely new group rather than copying A's manifest.
    await using var b = await env.OpenB(); var received = new List<ClipboardEntry>(); b.RemoteClipboardReceived += received.Add;
    await Throws<InvalidDataException>(() => b.RestoreBackupAsync(path, "wrong backup password"));
    Assert(b.GetSnippets().Count == 0 && b.GetHistory().Count == 0, "wrong password is non-mutating");
    encrypted[^1] ^= 1; var damaged = Path.Combine(env.Root, "damaged.cbb"); await File.WriteAllBytesAsync(damaged, encrypted);
    await Throws<InvalidDataException>(() => b.RestoreBackupAsync(damaged, TestEnvironment.Passphrase));
    await b.RestoreBackupAsync(path, TestEnvironment.Passphrase);
    Assert(b.GetSnippets().Single().Text == "バックアップに残す日本語" && b.GetHistory().Single().Text == "backup history" && b.GetGroups().Count == 2 && received.Count == 0, "portable restore recovers all views without live clipboard changes");
    await b.DisposeAsync(); await using var reopened = await env.OpenB(); Assert(reopened.GetSnippets().Count == 1 && reopened.GetHistory().Count == 1, "portable restore survives restart");
}

static async Task DeletedBackupRestore()
{
    using var env = new TestEnvironment(); await using var a = await env.OpenA();
    var group = await a.UpsertGroupAsync(null, "復元するグループ");
    var snippet = await a.UpsertSnippetAsync(null, "復元する文", "content", group.Name);
    var history = (await a.CaptureTextAsync("restore deleted history"))!;
    var path = Path.Combine(env.Root, "backup.cbb"); await a.ExportBackupAsync(path, TestEnvironment.Passphrase);
    await a.DeleteSnippetAsync(snippet.Id); await a.DeleteGroupAsync(group.Id); await a.DeleteHistoryAsync(history.Id);
    await a.RestoreBackupAsync(path, TestEnvironment.Passphrase);
    Assert(a.GetSnippets().Single().Id == snippet.Id && a.GetSnippets().Single().Group == group.Name && a.GetHistory().Single().Text == history.Text, "restore revives deleted content as fresh synchronized operations");
    env.ReplicateAtoB(); await using var b = await env.OpenB(); Assert(b.GetSnippets().Count == 1 && b.GetHistory().Count == 1, "restored records converge from unsorted immutable log");
}

static Task TextFormatting()
{
    Assert(TextTransforms.TrimLines(" a \r\n b \n") == "a" + Environment.NewLine + "b" + Environment.NewLine, "line trim normalizes line endings");
    Assert(TextTransforms.RemoveBlankLines("A\n \n\nB") == "A" + Environment.NewLine + "B", "remove blank lines");
    Assert(TextTransforms.JoinLines(" A \r\n\r\n B ", ",") == "A,B", "join lines");
    Assert(TextTransforms.ToHalfWidth("ＡＢＣ　１２３・日本語") == "ABC 123・日本語", "half-width ASCII only preserves Japanese");
    Assert(TextTransforms.ToFullWidth("Ab1 日本語") == "Ａｂ１　日本語", "full-width ASCII only");
    Assert(TextTransforms.Replace("ab ab", "ab", "XY") == "XY XY", "literal replace");
    Assert(TextTransforms.Replace("ab12", "([a-z]+)([0-9]+)", "$2:$1", true) == "12:ab", "regex captures");
    Assert(TextTransforms.UpperCase("ab日本語") == "AB日本語" && TextTransforms.LowerCase("AB日本語") == "ab日本語", "case transforms");
    return Task.CompletedTask;
}

static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6n8AAAAASUVORK5CYII=");
static void Assert(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

sealed class TestEnvironment : IDisposable
{
    public const string Passphrase = "同じ共有の合言葉1234";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ClipBridge-core-tests-" + Guid.NewGuid().ToString("N"));
    public string SharedA => Path.Combine(Root, "OneDriveA");
    public string SharedB => Path.Combine(Root, "OneDriveB");
    public string LocalA => Path.Combine(Root, "LocalA");
    public string LocalB => Path.Combine(Root, "LocalB");
    public TestEnvironment() => Directory.CreateDirectory(Root);
    public Task<SyncEngine> OpenA() => SyncEngine.OpenAsync(SharedA, Passphrase, LocalA, "a", "端末A");
    public Task<SyncEngine> OpenB() => SyncEngine.OpenAsync(SharedB, Passphrase, LocalB, "b", "端末B");
    public void ReplicateAtoB(bool includeEvents = true) => Copy(SharedA, SharedB, includeEvents);
    public void ReplicateBtoA() => Copy(SharedB, SharedA, true);
    private static void Copy(string source, string destination, bool includeEvents)
    {
        Directory.CreateDirectory(destination);
        File.Copy(Path.Combine(source, "clipbridge.group.json"), Path.Combine(destination, "clipbridge.group.json"), true);
        if (!includeEvents || !Directory.Exists(Path.Combine(source, "events"))) return;
        Directory.CreateDirectory(Path.Combine(destination, "events"));
        foreach (var file in Directory.GetFiles(Path.Combine(source, "events"), "*.cbe"))
            File.Copy(file, Path.Combine(destination, "events", Path.GetFileName(file)), true);
    }
    public void Dispose()
    {
        // This class owns a freshly generated exact temp child; never remove a caller-supplied path.
        var parent = Path.GetFullPath(Path.GetTempPath());
        var resolved = Path.GetFullPath(Root);
        if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("ClipBridge-core-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe test cleanup path");
        Directory.Delete(resolved, true);
    }
}
