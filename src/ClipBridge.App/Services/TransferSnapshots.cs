using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipBridge.Core;

namespace ClipBridge.App.Services;

internal static class TransferSnapshots
{
    internal static string StableId(string scope, string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "\0" + value)))[..32].ToLowerInvariant();
    private static bool IsId(string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static ClipBridgeSnapshot FromCsv(CsvTransferDocument csv, ClipBridgeSnapshot current)
    {
        var groups = new List<SnippetGroup>();
        foreach (var item in csv.Groups.OrderBy(x => x.Order))
        {
            if (groups.Any(x => x.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))) continue;
            var existing = current.Groups.FirstOrDefault(x => x.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
            groups.Add(new(existing?.Id ?? (IsId(item.Id) ? item.Id! : StableId("csv-group", item.Name)), item.Name, item.Order, DateTimeOffset.UtcNow));
        }
        foreach (string name in csv.Snippets.Select(x => x.Group).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (groups.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            var existing = current.Groups.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            groups.Add(new(existing?.Id ?? StableId("csv-group", name), name, groups.Count, DateTimeOffset.UtcNow));
        }
        var snippets = new List<Snippet>();
        foreach (var item in csv.Snippets)
        {
            var group = groups.Single(x => x.Name.Equals(item.Group, StringComparison.OrdinalIgnoreCase));
            var existing = current.Snippets.FirstOrDefault(x => x.Group.Equals(item.Group, StringComparison.OrdinalIgnoreCase) && x.Title == item.Title && x.Text == item.Text);
            string id = IsId(item.Id) ? item.Id! : existing?.Id ?? StableId("csv-snippet", item.Group + "\0" + item.Title + "\0" + item.Text);
            if (snippets.Any(x => x.Id == id)) continue;
            snippets.Add(new(id, item.Title, item.Text, DateTimeOffset.UtcNow, group.Name, group.Id, item.Order));
        }
        return new(1, DateTimeOffset.UtcNow, groups, snippets, []);
    }

    internal static ClipBridgeSnapshot FromCliborJson(string path, ClipBridgeSnapshot current)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 50 * 1024 * 1024) throw new InvalidDataException("移行データは50MB以内で指定してください。");
        var source = JsonSerializer.Deserialize<CliborSource>(File.ReadAllBytes(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("移行JSONを読み取れません。");
        if (source.Groups is null || source.Snippets is null || source.History is null) throw new InvalidDataException("移行JSONの項目が不足しています。");
        if (source.Groups.Count > SyncEngine.MaxGroups || source.Snippets.Count > SyncEngine.MaxSnippets || source.History.Count > SyncEngine.MaxHistoryEntries)
            throw new InvalidDataException("移行データが保存件数の上限を超えています。");
        var csv = new CsvTransferDocument(source.Groups.Select(x => new CsvGroup(StableId("clibor-group", x.Name), x.Name, x.Order)).ToArray(),
            source.Snippets.Select(x => new CsvSnippet(
                current.Snippets.FirstOrDefault(existing => existing.Group.Equals(x.Group, StringComparison.OrdinalIgnoreCase) && existing.Title == x.Title && existing.Text == x.Text)?.Id
                    ?? StableId("clibor-snippet", x.SourceId),
                x.Group, x.Title, x.Text, x.Order)).ToArray(), []);
        var snapshot = FromCsv(csv, current);
        var history = new List<ClipboardEntry>();
        foreach (var item in source.History)
        {
            if (current.History.Any(x => x.Kind == ClipboardKind.Text && x.Text == item.Text)) continue;
            string id = StableId("clibor-history", item.Text);
            if (history.Any(x => x.Id == id)) continue;
            history.Add(new(id, ClipboardKind.Text, item.Text, null, item.CreatedUtc, "clibor-import", "Cliborから移行"));
        }
        return snapshot with { History = history };
    }

    internal static object VerifyClibor(string path, ClipBridgeSnapshot target)
    {
        var source = JsonSerializer.Deserialize<CliborSource>(File.ReadAllBytes(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        int matchingGroups = source.Groups.Count(x => target.Groups.Any(y => y.Name == x.Name));
        int matchingSnippets = source.Snippets.Count(x => target.Snippets.Any(y => y.Group == x.Group && y.Title == x.Title && y.Text == x.Text));
        int matchingHistory = source.History.Count(x => target.History.Any(y => y.Kind == ClipboardKind.Text && y.Text == x.Text));
        bool historyDatesMatch = source.History.All(item => target.History
            .Where(existing => existing.Kind == ClipboardKind.Text && existing.Text == item.Text &&
                (existing.Id == StableId("clibor-history", item.Text) || existing.DeviceId == "clibor-import"))
            .All(existing => existing.CreatedUtc == item.CreatedUtc));
        var groupNames = target.Groups.Select(x => x.Name).Where(x => source.Groups.Any(y => y.Name == x)).ToArray();
        bool groupOrderMatches = groupNames.SequenceEqual(source.Groups.OrderBy(x => x.Order).Select(x => x.Name));
        bool snippetOrderMatches = source.Groups.All(group =>
        {
            var expected = source.Snippets.Where(x => x.Group == group.Name).OrderBy(x => x.Order).Select(x => (x.Title, x.Text)).ToArray();
            var actual = target.Snippets.Where(x => x.Group == group.Name && expected.Contains((x.Title, x.Text))).Select(x => (x.Title, x.Text)).ToArray();
            return actual.SequenceEqual(expected);
        });
        bool success = matchingGroups == source.Groups.Count && matchingSnippets == source.Snippets.Count && matchingHistory == source.History.Count && groupOrderMatches && snippetOrderMatches && historyDatesMatch;
        return new { Success = success, SourceGroups = source.Groups.Count, MatchingGroups = matchingGroups, SourceSnippets = source.Snippets.Count,
            MatchingSnippets = matchingSnippets, SourceHistory = source.History.Count, MatchingHistory = matchingHistory, GroupOrderMatches = groupOrderMatches, SnippetOrderMatches = snippetOrderMatches, HistoryDatesMatch = historyDatesMatch,
            TargetGroups = target.Groups.Count, TargetSnippets = target.Snippets.Count, TargetHistory = target.History.Count };
    }

    internal sealed record CliborSource(List<SourceGroup> Groups, List<SourceSnippet> Snippets, List<SourceHistory> History);
    internal sealed record SourceGroup(string Name, long Order);
    internal sealed record SourceSnippet(string SourceId, string Group, string Title, string Text, long Order);
    internal sealed record SourceHistory(string SourceId, string Text, DateTimeOffset CreatedUtc);
}
