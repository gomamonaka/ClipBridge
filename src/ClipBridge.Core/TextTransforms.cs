using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClipBridge.Core;

/// <summary>Pure text transformations; callers preview the result before saving or copying.</summary>
public static class TextTransforms
{
    private static string[] Lines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }

    public static string Trim(string text) => (text ?? throw new ArgumentNullException(nameof(text))).Trim();
    public static string TrimLines(string text) => string.Join(Environment.NewLine, Lines(text).Select(x => x.Trim()));
    public static string RemoveBlankLines(string text) => string.Join(Environment.NewLine, Lines(text).Where(x => !string.IsNullOrWhiteSpace(x)));
    public static string JoinLines(string text, string separator = " ") => string.Join(separator, Lines(text).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
    public static string UpperCase(string text) => (text ?? throw new ArgumentNullException(nameof(text))).ToUpper(CultureInfo.InvariantCulture);
    public static string LowerCase(string text) => (text ?? throw new ArgumentNullException(nameof(text))).ToLower(CultureInfo.InvariantCulture);

    public static string ToHalfWidth(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Concat(text.Select(x => x == '\u3000' ? ' ' : x is >= '\uff01' and <= '\uff5e' ? (char)(x - 0xfee0) : x));
    }

    public static string ToFullWidth(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Concat(text.Select(x => x == ' ' ? '\u3000' : x is >= '!' and <= '~' ? (char)(x + 0xfee0) : x));
    }

    public static string Replace(string text, string pattern, string replacement, bool regex = false)
    {
        ArgumentNullException.ThrowIfNull(text); ArgumentNullException.ThrowIfNull(pattern); ArgumentNullException.ThrowIfNull(replacement);
        if (pattern.Length == 0) throw new ArgumentException("置換する文字列を入力してください。", nameof(pattern));
        string output;
        if (regex)
            output = Regex.Replace(text, pattern, replacement, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        else
        {
            // Check expansion before allocating a possibly enormous replacement.
            var count = 0;
            for (var index = 0; (index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0; index += pattern.Length) count++;
            if ((long)text.Length + (long)count * (replacement.Length - pattern.Length) > SyncEngine.MaxTextBytes)
                throw new ArgumentException("置換後のテキストが1MBを超えます。", nameof(replacement));
            output = text.Replace(pattern, replacement, StringComparison.Ordinal);
        }
        if (Encoding.UTF8.GetByteCount(output) > SyncEngine.MaxTextBytes) throw new ArgumentException("置換後のテキストが1MBを超えます。", nameof(replacement));
        return output;
    }
}
