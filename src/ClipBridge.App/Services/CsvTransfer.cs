using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ClipBridge.App.Services;

public sealed record CsvGroup(string? Id, string Name, long Order);
public sealed record CsvSnippet(string? Id, string Group, string Title, string Text, long Order);
public sealed record CsvTransferDocument(IReadOnlyList<CsvGroup> Groups, IReadOnlyList<CsvSnippet> Snippets, IReadOnlyList<string> Warnings);

/// <summary>Bounded RFC 4180 transfer. Reading never mutates the application store.</summary>
public static class CsvTransfer
{
    public const long MaximumFileBytes = 50L * 1024 * 1024;
    public const int MaximumRows = 6000;
    private const int MaximumCellCharacters = 65536;
    private const int MaximumGroups = 256;
    private const int MaximumSnippets = 5000;
    private static readonly string[] NativeHeader = ["種別", "ID", "グループ", "タイトル", "本文", "並び順"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static CsvTransferDocument Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length > MaximumFileBytes) throw Error("CSV ファイルは 50 MiB 以下にしてください。");
            using var memory = new MemoryStream((int)input.Length);
            byte[] buffer = new byte[65536];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (memory.Length + read > MaximumFileBytes) throw Error("CSV ファイルは 50 MiB 以下にしてください。");
                memory.Write(buffer, 0, read);
            }
            bytes = memory.ToArray();
        }

        List<string[]> rows = Parse(Decode(bytes));
        if (rows.Count == 0) throw Error("CSV に見出し行がありません。");
        string[] header = rows[0];
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < header.Length; index++)
        {
            string name = header[index].Trim();
            if (name.Length == 0 || !columns.TryAdd(name, index)) throw Error("CSV の見出しが空か重複しています。");
        }
        bool native = columns.ContainsKey("種別");
        int groupColumn;
        if (native)
        {
            if (NativeHeader.Any(name => !columns.ContainsKey(name)) || columns.Count != NativeHeader.Length)
                throw Error("ClipBridge CSV の見出しは「種別,ID,グループ,タイトル,本文,並び順」にしてください。");
            groupColumn = columns["グループ"];
        }
        else
        {
            string[] groupNames = ["定型文グループ", "グループ名", "グループ"];
            var groupMatches = groupNames.Where(columns.ContainsKey).ToArray();
            if (groupMatches.Length != 1 || !columns.ContainsKey("定型文") || !columns.ContainsKey("メモ") || !columns.ContainsKey("ホットキー") || columns.Count != 4)
                throw Error("Clibor CSV の見出しは「定型文グループ,定型文,メモ,ホットキー」にしてください。");
            groupColumn = columns[groupMatches[0]];
        }

        var groups = new List<CsvGroup>();
        var snippets = new List<CsvSnippet>();
        var groupNamesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snippetGroups = new List<string>();
        var snippetOrder = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        int hotkeys = 0;
        int macros = 0;
        for (int index = 1; index < rows.Count; index++)
        {
            string[] row = rows[index];
            if (row.Length == 1 && row[0].Length == 0) continue;
            if (row.Length != header.Length) throw RowError(index + 1, "列の数が見出し行と一致しません。");
            string group = row[groupColumn];
            ValidateGroup(group, index + 1);
            if (native)
            {
                string kind = row[columns["種別"]];
                string? id = EmptyToNull(row[columns["ID"]]);
                ValidateId(id, index + 1);
                if (!long.TryParse(row[columns["並び順"]], NumberStyles.None, CultureInfo.InvariantCulture, out long order))
                    throw RowError(index + 1, "並び順は 0 以上の整数にしてください。");
                string title = row[columns["タイトル"]];
                string text = row[columns["本文"]];
                if (kind.Equals("group", StringComparison.OrdinalIgnoreCase) || kind == "グループ")
                {
                    if (title.Length != 0 || text.Length != 0) throw RowError(index + 1, "グループ行のタイトルと本文は空欄にしてください。");
                    if (!groupNamesSeen.Add(group)) throw RowError(index + 1, "同じグループが複数のグループ行に登録されています。");
                    groups.Add(new(id, group, order));
                }
                else if (kind.Equals("snippet", StringComparison.OrdinalIgnoreCase) || kind == "定型文")
                {
                    ValidateSnippet(title, text, index + 1);
                    snippets.Add(new(id, group, title, text, order));
                    snippetGroups.Add(group);
                }
                else throw RowError(index + 1, "種別は group または snippet にしてください。");
            }
            else
            {
                string text = row[columns["定型文"]];
                string memo = row[columns["メモ"]];
                string title = string.IsNullOrWhiteSpace(memo) ? FallbackTitle(text) : memo;
                ValidateSnippet(title, text, index + 1);
                if (groupNamesSeen.Add(group)) groups.Add(new(null, group, groups.Count));
                long order = snippetOrder.GetValueOrDefault(group);
                snippets.Add(new(null, group, title, text, order));
                snippetOrder[group] = order + 1;
                if (!string.IsNullOrWhiteSpace(row[columns["ホットキー"]])) hotkeys++;
            }
            if (groups.Count > MaximumGroups || snippets.Count > MaximumSnippets) throw Error("CSV はグループ 256 件、定型文 5,000 件まで取り込めます。");
        }

        // Native CSV can omit group rows when produced by a tabular editor.
        // Add those groups after explicitly ordered groups; never discard snippets.
        foreach (string group in snippetGroups)
            if (groupNamesSeen.Add(group)) groups.Add(new(null, group, groups.Count));
        ValidateDocument(groups, snippets);
        macros = snippets.Count(snippet => snippet.Text.Contains("<$CLB_", StringComparison.OrdinalIgnoreCase));
        var warnings = new List<string>();
        if (hotkeys != 0) warnings.Add($"Clibor のホットキー設定 {hotkeys} 件は引き継ぎません。定型文の本文は保持します。");
        if (macros != 0) warnings.Add($"Clibor 独自マクロを含む定型文が {macros} 件あります。マクロは実行せず、文字列として保持します。");
        return new(groups.AsReadOnly(), snippets.AsReadOnly(), warnings.AsReadOnly());
    }

    public static void Write(string path, CsvTransferDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        ValidateDocument(document.Groups, document.Snippets);
        string fullPath = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, ".clipbridge-csv-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true, true), 65536, true))
            {
                WriteRow(writer, NativeHeader);
                foreach (CsvGroup group in document.Groups)
                {
                    WriteRow(writer, ["group", group.Id ?? "", group.Name, "", "", group.Order.ToString(CultureInfo.InvariantCulture)]);
                    CheckOutputLimit(writer, stream);
                }
                foreach (CsvSnippet snippet in document.Snippets)
                {
                    WriteRow(writer, ["snippet", snippet.Id ?? "", snippet.Group, snippet.Title, snippet.Text, snippet.Order.ToString(CultureInfo.InvariantCulture)]);
                    CheckOutputLimit(writer, stream);
                }
                writer.Flush();
                if (stream.Length > MaximumFileBytes) throw Error("書き出す CSV が 50 MiB を超えています。");
                stream.Flush(true);
            }
            File.Move(temporary, fullPath, true);
        }
        catch (EncoderFallbackException) { throw Error("CSV に保存できない文字が含まれています。"); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteRow(TextWriter writer, IEnumerable<string> cells)
    {
        bool first = true;
        foreach (string cell in cells)
        {
            if (!first) writer.Write(',');
            first = false;
            if (cell.IndexOfAny([',', '"', '\r', '\n']) >= 0)
            {
                writer.Write('"');
                writer.Write(cell.Replace("\"", "\"\"", StringComparison.Ordinal));
                writer.Write('"');
            }
            else writer.Write(cell);
        }
        writer.Write("\r\n");
    }

    private static void CheckOutputLimit(TextWriter writer, Stream stream)
    {
        writer.Flush();
        if (stream.Length > MaximumFileBytes) throw Error("書き出す CSV が 50 MiB を超えています。");
    }

    private static string Decode(byte[] bytes)
    {
        try
        {
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return StrictUtf8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2);
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2);
            // UTF-16 exports commonly carry a BOM. Also recognize BOM-less
            // files by the ASCII comma/ID bytes in their required header.
            int evenZero = 0, oddZero = 0, sample = Math.Min(bytes.Length, 256);
            for (int index = 0; index < sample; index++)
                if (bytes[index] == 0) { if (index % 2 == 0) evenZero++; else oddZero++; }
            if (oddZero > sample / 10 && oddZero > evenZero * 3) return new UnicodeEncoding(false, false, true).GetString(bytes);
            if (evenZero > sample / 10 && evenZero > oddZero * 3) return new UnicodeEncoding(true, false, true).GetString(bytes);
            try { return StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
            }
        }
        catch (DecoderFallbackException) { throw Error("CSV の文字コードを読み取れません。UTF-8、UTF-16、または CP932 で保存してください。"); }
    }

    private static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        bool afterQuote = false;
        bool atFieldStart = true;
        bool rowStarted = false;
        void AddCell()
        {
            if (cells.Count >= 32) throw Error("CSV の列数が多すぎます。");
            cells.Add(cell.ToString());
            cell.Clear();
            atFieldStart = true;
            afterQuote = false;
        }
        void AddRow()
        {
            AddCell();
            if (rows.Count >= MaximumRows) throw Error("CSV の行数は 6,000 行以下にしてください。");
            rows.Add(cells.ToArray());
            cells.Clear();
            rowStarted = false;
        }
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (character == '\0') throw Error("CSV に NUL 文字が含まれています。文字コードを確認してください。");
            rowStarted = true;
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"') { cell.Append('"'); index++; }
                    else { quoted = false; afterQuote = true; }
                }
                else cell.Append(character);
            }
            else if (character == ',') AddCell();
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                AddRow();
            }
            else if (afterQuote) throw RowError(rows.Count + 1, "閉じ引用符の後に区切り以外の文字があります。");
            else if (character == '"')
            {
                if (!atFieldStart) throw RowError(rows.Count + 1, "引用符は項目の先頭に置いてください。");
                quoted = true;
                atFieldStart = false;
            }
            else { cell.Append(character); atFieldStart = false; }
            if (cell.Length > MaximumCellCharacters) throw RowError(rows.Count + 1, "項目の文字数が上限を超えています。");
        }
        if (quoted) throw RowError(rows.Count + 1, "引用符が閉じられていません。");
        if (rowStarted || cells.Count != 0 || cell.Length != 0) AddRow();
        return rows;
    }

    private static void ValidateDocument(IReadOnlyList<CsvGroup> groups, IReadOnlyList<CsvSnippet> snippets)
    {
        if (groups is null || snippets is null) throw Error("CSV のグループまたは定型文がありません。");
        if (groups.Count > MaximumGroups || snippets.Count > MaximumSnippets) throw Error("CSV はグループ 256 件、定型文 5,000 件まで取り込めます。");
        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snippetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CsvGroup group in groups)
        {
            if (group is null) throw Error("CSV のグループが不正です。");
            ValidateGroup(group.Name, 0);
            ValidateId(group.Id, 0);
            if (group.Order < 0 || !groupNames.Add(group.Name) || (group.Id is not null && !groupIds.Add(group.Id))) throw Error("グループの並び順、名前、または ID が重複・不正です。");
        }
        foreach (CsvSnippet snippet in snippets)
        {
            if (snippet is null) throw Error("CSV の定型文が不正です。");
            ValidateGroup(snippet.Group, 0);
            ValidateId(snippet.Id, 0);
            ValidateSnippet(snippet.Title, snippet.Text, 0);
            if (snippet.Order < 0 || (snippet.Id is not null && !snippetIds.Add(snippet.Id))) throw Error("定型文の並び順または ID が重複・不正です。");
            groupNames.Add(snippet.Group);
        }
        if (groupNames.Count > MaximumGroups) throw Error("CSV のグループは 256 件まで取り込めます。");
    }

    private static void ValidateGroup(string name, int row)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 60 || name.Any(char.IsControl)) throw RowError(row, "グループ名は制御文字を含まない 1〜60 文字にしてください。");
    }
    private static void ValidateId(string? id, int row)
    {
        if (id is not null && (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl))) throw RowError(row, "ID は制御文字を含まない 128 文字以下にしてください。");
    }
    private static void ValidateSnippet(string title, string text, int row)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120 || title.Any(char.IsControl)) throw RowError(row, "タイトルは制御文字を含まない 1〜120 文字にしてください。");
        if (string.IsNullOrEmpty(text)) throw RowError(row, "定型文の本文を入力してください。");
        try { if (StrictUtf8.GetByteCount(text) > 65536) throw RowError(row, "定型文の本文は UTF-8 で 64 KiB 以下にしてください。"); }
        catch (EncoderFallbackException) { throw RowError(row, "定型文の本文に不正な文字が含まれています。"); }
    }
    private static string FallbackTitle(string text)
    {
        string title = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "定型文";
        if (title.Length > 120) title = title[..(char.IsHighSurrogate(title[119]) ? 119 : 120)];
        return title;
    }
    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;
    private static DataException Error(string message) => new(message);
    private static DataException RowError(int row, string message) => new(row > 0 ? $"CSV {row} 行目: {message}" : "CSV: " + message);
}
