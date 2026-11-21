using System.Data;
using System.Text;
using System.Text.Json;
using ClipBridge.App.Services;
using ClipBridge.Core;

string folder = Path.Combine(Path.GetTempPath(), "ClipBridgeCsvTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
int count = 0;
var failures = new List<string>();
void Test(string name, Action action)
{
    try { action(); count++; Console.WriteLine("PASS " + name); }
    catch (Exception exception) { failures.Add(name + ": " + exception.GetType().Name + " " + exception.Message); Console.WriteLine("FAIL " + name); }
}
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected value did not match actual value."); }
void Reject(Action action)
{
    try { action(); }
    catch (DataException exception) { if (exception.Message.Length == 0) throw new Exception("Missing readable error."); return; }
    throw new Exception("Malformed or oversized document accepted.");
}
CsvTransferDocument ReadText(string text, Encoding? encoding = null)
{
    string file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".csv");
    File.WriteAllText(file, text, encoding ?? new UTF8Encoding(false));
    return CsvTransfer.Read(file);
}
const string header = "種別,ID,グループ,タイトル,本文,並び順\r\n";
const string clibor = "定型文グループ,定型文,メモ,ホットキー\r\n";
CsvTransferDocument Document(string text = "本文") => new([new("g-empty", "空", 9), new("g-work", "仕事", 2)], [new("s-1", "仕事", "挨拶", text, 7)], []);
try
{
    Test("Native round trip preserves IDs, order, empty groups and quoted CRLF", () =>
    {
        string text = "  先頭,値\r\n\"引用\"\n末尾\r";
        string file = Path.Combine(folder, "roundtrip.csv");
        CsvTransfer.Write(file, Document(text));
        byte[] bytes = File.ReadAllBytes(file);
        Equal(true, bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        var parsed = CsvTransfer.Read(file);
        Equal(Document(text).Groups[0], parsed.Groups[0]);
        Equal(Document(text).Groups[1], parsed.Groups[1]);
        Equal(Document(text).Snippets[0], parsed.Snippets[0]);
    });
    Test("Formula-like cells are not rewritten", () => Equal("=SUM(1,2)", RoundTrip("=SUM(1,2)")));
    Test("Whitespace-only snippet text is preserved", () => Equal(" \t\r\n", RoundTrip(" \t\r\n")));
    Test("Unicode and vertical-tab text is preserved", () => Equal("絵文字😀\v改行\r\n", RoundTrip("絵文字😀\v改行\r\n")));
    Test("Clibor memo, first line titles, group sequence and order", () =>
    {
        var result = ReadText(clibor + "仕事,本文,メモ,\r\n仕事,二件目,,\r\n空間,三件目,,\r\n");
        Equal(2, result.Groups.Count); Equal(3, result.Snippets.Count);
        Equal("メモ", result.Snippets[0].Title); Equal("二件目", result.Snippets[1].Title);
        Equal(1L, result.Snippets[1].Order); Equal(0L, result.Snippets[2].Order);
    });
    Test("Clibor group name alias accepted", () => Equal("仕事", ReadText(clibor.Replace("定型文グループ", "グループ名") + "仕事,本文,メモ,\n").Groups[0].Name));
    Test("Clibor macro and hotkey produce warnings without execution", () =>
    {
        var result = ReadText(clibor + "仕事,<$CLB_DATE/>,マクロ,Ctrl+A\r\n");
        Equal(2, result.Warnings.Count); Equal("<$CLB_DATE/>", result.Snippets[0].Text);
    });
    Test("Clibor fallback title truncates at a Unicode boundary", () =>
    {
        var result = ReadText(clibor + "仕事," + new string('a', 119) + "😀末尾,,\n");
        Equal(119, result.Snippets[0].Title.Length);
    });
    Test("UTF-8 BOM import", () => Equal("本文", ReadText(clibor + "仕事,本文,メモ,\n", new UTF8Encoding(true)).Snippets[0].Text));
    Test("UTF-16 little endian BOM import", () => Equal("本文", ReadText(clibor + "仕事,本文,メモ,\n", Encoding.Unicode).Snippets[0].Text));
    Test("UTF-16 big endian BOM import", () => Equal("本文", ReadText(clibor + "仕事,本文,メモ,\n", Encoding.BigEndianUnicode).Snippets[0].Text));
    Test("UTF-16 without BOM import", () => Equal("本文", ReadText(clibor + "仕事,本文,メモ,\n", new UnicodeEncoding(false, false, true)).Snippets[0].Text));
    Test("CP932 import", () =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Equal("本文", ReadText(clibor + "仕事,本文,メモ,\n", Encoding.GetEncoding(932)).Snippets[0].Text);
    });
    Test("Reordered native header", () => Equal("本文", ReadText("本文,種別,グループ,並び順,ID,タイトル\n本文,snippet,仕事,8,s1,題名\n").Snippets[0].Text));
    Test("Native missing group rows inferred after explicit group rows", () =>
    {
        var result = ReadText(header + "snippet,s1,推測,題名,本文,0\ngroup,g1,空,,,5\n");
        Equal(2, result.Groups.Count); Equal("空", result.Groups[0].Name); Equal("推測", result.Groups[1].Name);
    });
    Test("Final empty cell and no terminal newline", () => Equal(1, ReadText(clibor + "仕事,本文,メモ,").Snippets.Count));
    Test("Blank lines ignored", () => Equal(1, ReadText(clibor + "\n仕事,本文,メモ,\n\n").Snippets.Count));
    Test("Empty native file header is a valid empty export", () => Equal(0, ReadText(header).Groups.Count));
    Test("Unclosed quoted value rejected", () => Reject(() => ReadText(clibor + "仕事,\"本文,メモ,\n")));
    Test("Quote in unquoted field rejected", () => Reject(() => ReadText(clibor + "仕事,本\"文,メモ,\n")));
    Test("Text after closing quote rejected", () => Reject(() => ReadText(clibor + "仕事,\"本文\"x,メモ,\n")));
    Test("Duplicate header rejected", () => Reject(() => ReadText("定型文グループ,定型文,メモ,メモ\n")));
    Test("Missing header rejected", () => Reject(() => ReadText("仕事,本文,メモ,\n")));
    Test("Unexpected extra header rejected", () => Reject(() => ReadText(clibor.TrimEnd() + ",未知\n")));
    Test("Repeated header within rows rejected", () => Reject(() => ReadText(header + header)));
    Test("Wrong column count rejected after valid row", () => Reject(() => ReadText(clibor + "仕事,本文,メモ,\n仕事,不正\n")));
    Test("Invalid row kind rejected", () => Reject(() => ReadText(header + "history,,仕事,題名,本文,0\n")));
    Test("Negative order rejected", () => Reject(() => ReadText(header + "snippet,,仕事,題名,本文,-1\n")));
    Test("Noninteger order rejected", () => Reject(() => ReadText(header + "snippet,,仕事,題名,本文,1.2\n")));
    Test("Duplicate group rejected", () => Reject(() => ReadText(header + "group,g1,仕事,,,0\ngroup,g2,仕事,,,1\n")));
    Test("Duplicate snippet ID rejected", () => Reject(() => ReadText(header + "snippet,s1,仕事,題名,本文,0\nsnippet,s1,仕事,題名,二件目,1\n")));
    Test("Group row cannot hide body text", () => Reject(() => ReadText(header + "group,g1,仕事,,消失させない,0\n")));
    Test("Group title limits rejected", () => Reject(() => ReadText(clibor + new string('あ', 61) + ",本文,題名,\n")));
    Test("Memo title limits rejected", () => Reject(() => ReadText(clibor + "仕事,本文," + new string('あ', 121) + ",\n")));
    Test("Empty snippet body rejected", () => Reject(() => ReadText(clibor + "仕事,,題名,\n")));
    Test("Body UTF-8 byte limit rejected", () => Reject(() => ReadText(clibor + "仕事," + new string('あ', 21846) + ",題名,\n")));
    Test("Exact 64 KiB body is accepted", () => Equal(65536, ReadText(clibor + "仕事," + new string('a', 65536) + ",題名,\n").Snippets[0].Text.Length));
    Test("Cell character bound enforced", () => Reject(() => ReadText(clibor + "仕事," + new string('a', 65537) + ",題名,\n")));
    Test("Too many groups rejected", () => Reject(() => ReadText(header + string.Concat(Enumerable.Range(0, 257).Select(i => $"group,g{i},グループ{i},,,{i}\n")))));
    Test("Too many snippets rejected", () => Reject(() => ReadText(header + string.Concat(Enumerable.Range(0, 5001).Select(i => $"snippet,s{i},仕事,題名,本文,{i}\n")))));
    Test("Row parser bound enforced including blank lines", () => Reject(() => ReadText(header + new string('\n', 6000))));
    Test("Column parser bound enforced", () => Reject(() => ReadText(new string(',', 40))));
    Test("50 MiB file limit enforced before decoding", () =>
    {
        string file = Path.Combine(folder, "oversized.csv");
        using (var stream = File.Create(file)) stream.SetLength(CsvTransfer.MaximumFileBytes + 1);
        Reject(() => CsvTransfer.Read(file));
    });
    Test("Malformed UTF-16 rejected", () =>
    {
        string file = Path.Combine(folder, "bad-utf16.csv"); File.WriteAllBytes(file, [0xFF, 0xFE, 0x61]);
        Reject(() => CsvTransfer.Read(file));
    });
    Test("Invalid export leaves destination unchanged and no staging files", () =>
    {
        string file = Path.Combine(folder, "existing.csv"); File.WriteAllText(file, "unchanged");
        Reject(() => CsvTransfer.Write(file, new([], [new(null, "仕事", "題名", "", 0)], [])));
        Equal("unchanged", File.ReadAllText(file));
        Equal(0, Directory.GetFiles(folder, ".clipbridge-csv-*.tmp").Length);
    });
    Test("Oversized export leaves destination unchanged and removes staging file", () =>
    {
        string file = Path.Combine(folder, "large-existing.csv"); File.WriteAllText(file, "unchanged");
        string body = new('a', 65536);
        var many = Enumerable.Range(0, 801).Select(i => new CsvSnippet("id-" + i, "仕事", "題名", body, i)).ToArray();
        Reject(() => CsvTransfer.Write(file, new([new(null, "仕事", 0)], many, [])));
        Equal("unchanged", File.ReadAllText(file));
        Equal(0, Directory.GetFiles(folder, ".clipbridge-csv-*.tmp").Length);
    });
    Test("CSV snapshot reuses existing group IDs and merges group name case", () =>
    {
        var now = DateTimeOffset.UtcNow;
        var existing = new ClipBridgeSnapshot(1, now, [new("existing-group-id", "Work", 0, now)], [], []);
        var input = new CsvTransferDocument([new(null, "work", 8), new(null, "WORK", 9)], [new(null, "Work", "題名", "本文", 3)], []);
        var snapshot = TransferSnapshots.FromCsv(input, existing);
        Equal(1, snapshot.Groups.Count); Equal("existing-group-id", snapshot.Groups[0].Id);
        Equal("existing-group-id", snapshot.Snippets[0].GroupId); Equal(8L, snapshot.Groups[0].SortOrder);
    });
    Test("CSV snapshot reuses matching snippet IDs without changing text", () =>
    {
        var now = DateTimeOffset.UtcNow;
        var snippet = new Snippet("existing-snippet-id", "題名", "本文\r\n", now, "仕事", "g", 4);
        var existing = new ClipBridgeSnapshot(1, now, [new("g", "仕事", 0, now)], [snippet], []);
        var snapshot = TransferSnapshots.FromCsv(new([], [new(null, "仕事", "題名", "本文\r\n", 2)], []), existing);
        Equal("existing-snippet-id", snapshot.Snippets[0].Id); Equal("本文\r\n", snapshot.Snippets[0].Text);
        Equal(2L, snapshot.Snippets[0].SortOrder);
    });
    Test("CSV snapshot honors valid exported IDs and creates stable fallback IDs", () =>
    {
        var empty = new ClipBridgeSnapshot(1, DateTimeOffset.UtcNow, [], [], []);
        const string groupId = "00000000000000000000000000000001";
        const string snippetId = "00000000000000000000000000000002";
        var input = new CsvTransferDocument([new(groupId, "仕事", 0)], [new(snippetId, "仕事", "題名", "本文", 0), new(null, "仕事", "別件", "別本文", 1)], []);
        var first = TransferSnapshots.FromCsv(input, empty);
        var second = TransferSnapshots.FromCsv(input, empty);
        Equal(groupId, first.Groups[0].Id); Equal(snippetId, first.Snippets[0].Id);
        Equal(first.Snippets[1].Id, second.Snippets[1].Id); Equal(32, first.Snippets[1].Id.Length);
    });
    Test("CSV snapshot orders groups and retains snippet sort keys", () =>
    {
        var empty = new ClipBridgeSnapshot(1, DateTimeOffset.UtcNow, [], [], []);
        var input = new CsvTransferDocument([new(null, "後", 8), new(null, "先", 1), new(null, "空", 4)], [new(null, "先", "二番", "B", 9), new(null, "先", "一番", "A", 3)], []);
        var snapshot = TransferSnapshots.FromCsv(input, empty);
        Equal("先,空,後", string.Join(',', snapshot.Groups.Select(group => group.Name)));
        Equal(9L, snapshot.Snippets[0].SortOrder); Equal(3L, snapshot.Snippets[1].SortOrder);
    });
    Test("Native CSV explicit ID remains authoritative over identical existing content", () =>
    {
        var now = DateTimeOffset.UtcNow;
        const string existingId = "11111111111111111111111111111111";
        const string importedId = "22222222222222222222222222222222";
        var current = new ClipBridgeSnapshot(1, now, [new("g", "仕事", 0, now)], [new(existingId, "題名", "本文", now, "仕事", "g", 0)], []);
        var result = TransferSnapshots.FromCsv(new([], [new(importedId, "仕事", "題名", "本文", 0)], []), current);
        Equal(importedId, result.Snippets[0].Id);
    });
    Test("Clibor synthetic JSON preserves multiline, controls, memo titles and empty groups", () =>
    {
        string file = SyntheticCliborFile();
        var snapshot = TransferSnapshots.FromCliborJson(file, new(1, DateTimeOffset.UtcNow, [], [], []));
        Equal(2, snapshot.Groups.Count); Equal(2, snapshot.Snippets.Count); Equal(2, snapshot.History.Count);
        Equal("本文,\"引用\"\r\n二行目", snapshot.Snippets[0].Text);
        Equal("履歴\v\r\n続き😀", snapshot.History[0].Text);
        Equal(true, VerificationSucceeded(file, snapshot));
    });
    Test("Clibor synthetic conversion has stable IDs and does not re-add existing history", () =>
    {
        string file = SyntheticCliborFile();
        var empty = new ClipBridgeSnapshot(1, DateTimeOffset.UtcNow, [], [], []);
        var first = TransferSnapshots.FromCliborJson(file, empty);
        var second = TransferSnapshots.FromCliborJson(file, first);
        Equal(0, second.History.Count);
        Equal(string.Join(',', first.Groups.Select(group => group.Id)), string.Join(',', second.Groups.Select(group => group.Id)));
        Equal(string.Join(',', first.Snippets.Select(snippet => snippet.Id)), string.Join(',', second.Snippets.Select(snippet => snippet.Id)));
    });
    Test("Clibor verification detects missing content and reordered items", () =>
    {
        string file = SyntheticCliborFile();
        var source = TransferSnapshots.FromCliborJson(file, new(1, DateTimeOffset.UtcNow, [], [], []));
        Equal(false, VerificationSucceeded(file, source with { History = [] }));
        Equal(false, VerificationSucceeded(file, source with { Groups = source.Groups.Reverse().ToArray() }));
        Equal(false, VerificationSucceeded(file, source with { Snippets = source.Snippets.Reverse().ToArray() }));
        Equal(false, VerificationSucceeded(file, source with { Snippets = source.Snippets.Select(item => item with { Text = item.Text.Replace("\r\n", "\n", StringComparison.Ordinal) }).ToArray() }));
    });
    Test("Clibor verification requires exact dates for imported history", () =>
    {
        string file = SyntheticCliborFile();
        var source = TransferSnapshots.FromCliborJson(file, new(1, DateTimeOffset.UtcNow, [], [], []));
        using var correct = JsonDocument.Parse(JsonSerializer.Serialize(TransferSnapshots.VerifyClibor(file, source)));
        Equal(true, correct.RootElement.GetProperty("HistoryDatesMatch").GetBoolean());
        Equal(true, correct.RootElement.GetProperty("Success").GetBoolean());
        var mismatched = source with { History = source.History.Select(item => item with { CreatedUtc = item.CreatedUtc.AddSeconds(1) }).ToArray() };
        using var wrong = JsonDocument.Parse(JsonSerializer.Serialize(TransferSnapshots.VerifyClibor(file, mismatched)));
        Equal(false, wrong.RootElement.GetProperty("HistoryDatesMatch").GetBoolean());
        Equal(false, wrong.RootElement.GetProperty("Success").GetBoolean());
    });
    Test("Clibor verification permits preserved timestamps of existing history from another device", () =>
    {
        string file = SyntheticCliborFile();
        var source = TransferSnapshots.FromCliborJson(file, new(1, DateTimeOffset.UtcNow, [], [], []));
        var existing = source with { History = source.History.Select(item => item with { Id = TransferSnapshots.StableId("pre-existing", item.Text!), CreatedUtc = item.CreatedUtc.AddDays(-1), DeviceId = "existing-device" }).ToArray() };
        using var verification = JsonDocument.Parse(JsonSerializer.Serialize(TransferSnapshots.VerifyClibor(file, existing)));
        Equal(true, verification.RootElement.GetProperty("HistoryDatesMatch").GetBoolean());
        Equal(true, verification.RootElement.GetProperty("Success").GetBoolean());
    });
    Test("Clibor verification checks stable import IDs after Core replaces the device marker", () =>
    {
        string file = SyntheticCliborFile();
        var source = TransferSnapshots.FromCliborJson(file, new(1, DateTimeOffset.UtcNow, [], [], []));
        var persisted = source with { History = source.History.Select(item => item with { DeviceId = "local-pc" }).ToArray() };
        Equal(true, VerificationSucceeded(file, persisted));
        var incorrect = persisted with { History = persisted.History.Select(item => item with { CreatedUtc = item.CreatedUtc.AddSeconds(1) }).ToArray() };
        using var verification = JsonDocument.Parse(JsonSerializer.Serialize(TransferSnapshots.VerifyClibor(file, incorrect)));
        Equal(false, verification.RootElement.GetProperty("HistoryDatesMatch").GetBoolean());
        Equal(false, verification.RootElement.GetProperty("Success").GetBoolean());
    });
    Test("Clibor synthetic import persists exact contents and repeat import keeps the same IDs and counts", () =>
    {
        string file = SyntheticCliborFile();
        string shared = Path.Combine(folder, "synthetic-shared");
        string cache = Path.Combine(folder, "synthetic-cache");
        const string phrase = "synthetic-only-test-passphrase";
        var engine = SyncEngine.OpenAsync(shared, phrase, cache, "synthetic-device", "Synthetic test").GetAwaiter().GetResult();
        try
        {
            var snapshot = TransferSnapshots.FromCliborJson(file, engine.GetSnapshot());
            var result = engine.ImportSnapshotAsync(snapshot).GetAwaiter().GetResult();
            if (result.Groups != 2 || result.Snippets != 2 || result.History != 2) throw new Exception($"Unexpected initial import counts: {result.Groups}/{result.Snippets}/{result.History}.");
            if (!VerificationSucceeded(file, engine.GetSnapshot())) throw new Exception("Initial import verification failed: " + JsonSerializer.Serialize(TransferSnapshots.VerifyClibor(file, engine.GetSnapshot())));
            var before = engine.GetSnapshot();
            var again = engine.ImportSnapshotAsync(TransferSnapshots.FromCliborJson(file, before)).GetAwaiter().GetResult();
            var after = engine.GetSnapshot();
            Equal(0, again.History);
            Equal(before.Groups.Count, after.Groups.Count); Equal(before.Snippets.Count, after.Snippets.Count); Equal(before.History.Count, after.History.Count);
            Equal(string.Join(',', before.Groups.Select(item => item.Id)), string.Join(',', after.Groups.Select(item => item.Id)));
            Equal(string.Join(',', before.Snippets.Select(item => item.Id)), string.Join(',', after.Snippets.Select(item => item.Id)));
            Equal(true, VerificationSucceeded(file, after));
        }
        finally { engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        var reopened = SyncEngine.OpenAsync(shared, phrase, cache, "synthetic-device", "Synthetic test").GetAwaiter().GetResult();
        try { Equal(true, VerificationSucceeded(file, reopened.GetSnapshot())); }
        finally { reopened.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });
    Test("Clibor migration reuses a manually created exact snippet and restores source order", () =>
    {
        string file = SyntheticCliborFile();
        string shared = Path.Combine(folder, "existing-exact-shared");
        string cache = Path.Combine(folder, "existing-exact-cache");
        const string phrase = "synthetic-existing-test-passphrase";
        const string manualId = "33333333333333333333333333333333";
        const string groupId = "44444444444444444444444444444444";
        var now = DateTimeOffset.UtcNow;
        var engine = SyncEngine.OpenAsync(shared, phrase, cache, "synthetic-existing-device", "Synthetic test").GetAwaiter().GetResult();
        try
        {
            var manual = new ClipBridgeSnapshot(1, now, [new(groupId, "仕事", 8, now)], [new(manualId, "メモ一", "本文,\"引用\"\r\n二行目", now, "仕事", groupId, 99)], []);
            engine.ImportSnapshotAsync(manual).GetAwaiter().GetResult();
            var migration = TransferSnapshots.FromCliborJson(file, engine.GetSnapshot());
            Equal(manualId, migration.Snippets.Single(item => item.Title == "メモ一").Id);
            engine.ImportSnapshotAsync(migration).GetAwaiter().GetResult();
            var target = engine.GetSnapshot();
            Equal(2, target.Snippets.Count);
            Equal(1, target.Snippets.Count(item => item.Title == "メモ一" && item.Text == "本文,\"引用\"\r\n二行目"));
            Equal(manualId, target.Snippets.Single(item => item.Title == "メモ一").Id);
            Equal(true, VerificationSucceeded(file, target));
        }
        finally { engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });
}
finally { Directory.Delete(folder, true); }
Console.WriteLine($"CSV transfer tests: {count} passed, {failures.Count} failed.");
foreach (string failure in failures) Console.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

string RoundTrip(string text)
{
    string file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".csv");
    CsvTransfer.Write(file, Document(text));
    return CsvTransfer.Read(file).Snippets[0].Text;
}

string SyntheticCliborFile()
{
    string file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
    var document = new
    {
        groups = new[] { new { name = "仕事", order = 0 }, new { name = "空", order = 1 } },
        snippets = new[]
        {
            new { sourceId = "synthetic:1", group = "仕事", title = "メモ一", text = "本文,\"引用\"\r\n二行目", order = 0 },
            new { sourceId = "synthetic:2", group = "仕事", title = "メモ二", text = "別の本文", order = 1 }
        },
        history = new[]
        {
            new { sourceId = "synthetic:history:1", text = "履歴\v\r\n続き😀", createdUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new { sourceId = "synthetic:history:2", text = " ", createdUtc = DateTimeOffset.UtcNow.AddMinutes(-2) }
        }
    };
    File.WriteAllText(file, JsonSerializer.Serialize(document));
    return file;
}
bool VerificationSucceeded(string file, ClipBridgeSnapshot snapshot)
{
    using var verification = JsonDocument.Parse(JsonSerializer.Serialize(TransferSnapshots.VerifyClibor(file, snapshot)));
    return verification.RootElement.GetProperty("Success").GetBoolean();
}
