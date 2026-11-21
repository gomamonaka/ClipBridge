using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClipBridge.Core;

namespace ClipBridge.App;

public partial class TextEditorWindow : Window
{
    private readonly string _original;
    private bool _ready;
    public string EditedText { get; private set; } = string.Empty;
    public bool Changed => EditedText != _original;
    private const int MaximumBytes = SyncEngine.MaxTextBytes;

    public TextEditorWindow(HistoryItem item)
    {
        if (item.IsImage) throw new ArgumentException("画像の履歴はテキストとして編集できません。", nameof(item));
        InitializeComponent();
        _original = item.Detail;
        BodyInput.IsUndoEnabled = false;
        BodyInput.Text = _original;
        BodyInput.IsUndoEnabled = true;
        _ready = true;
        UpdateCount();
        Loaded += (_, _) => { BodyInput.Focus(); BodyInput.CaretIndex = 0; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Save(); e.Handled = true; }
        };
    }

    internal static string NormalizeLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    internal static string TransformText(string text, string operation) => operation switch
    {
        "trim" => TextTransforms.TrimLines(text),
        "removeBlank" => TextTransforms.RemoveBlankLines(text),
        "join" => TextTransforms.JoinLines(text),
        "upper" => TextTransforms.UpperCase(text),
        "lower" => TextTransforms.LowerCase(text),
        "half" => TextTransforms.ToHalfWidth(text),
        "full" => TextTransforms.ToFullWidth(text),
        _ => throw new ArgumentException("指定した整形方法がありません。", nameof(operation))
    };

    internal void ApplyTransform(string operation)
    {
        ApplyText(TransformText(BodyInput.Text, operation));
        Notice("整形しました。Ctrl + Z で元に戻せます。");
    }

    private void ApplyText(string text)
    {
        if (BodyInput.Text == text) return;
        int caret = BodyInput.CaretIndex;
        BodyInput.BeginChange();
        try { BodyInput.SelectAll(); BodyInput.SelectedText = text; BodyInput.Select(Math.Min(caret, text.Length), 0); }
        finally { BodyInput.EndChange(); }
        BodyInput.Focus();
    }

    internal bool ReplaceText(string find, string replacement, bool regex)
    {
        if (find.Length == 0) { Notice("検索する文字列を入力してください。", true); FindInput.Focus(); return false; }
        try
        {
            var text = TextTransforms.Replace(BodyInput.Text, find, replacement, regex);
            if (text.Length > MaximumBytes || Encoding.UTF8.GetByteCount(text) > MaximumBytes)
            { Notice("置換後の本文が 1 MB を超えるため適用できません。", true); return false; }
            bool changed = BodyInput.Text != text;
            ApplyText(text);
            Notice(changed ? "すべて置換しました。Ctrl + Z で元に戻せます。" : "一致する文字列はありませんでした。");
            return true;
        }
        catch (ArgumentException) { Notice("式の指定と、置換後の本文が 1 MB 以内かを確認してください。", true); return false; }
        catch (RegexMatchTimeoutException) { Notice("処理に時間がかかるため中止しました。正規表現を簡単にしてください。", true); return false; }
    }

    private void UpdateCount()
    {
        if (!_ready) return;
        CharacterCount.Text = $"{BodyInput.Text.Length:N0} 文字";
    }
    private void Notice(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(error ? Color.FromRgb(177, 58, 58) : Color.FromRgb(52, 119, 118));
    }
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(BodyInput.Text)) { Notice("本文を入力してください。", true); BodyInput.Focus(); return; }
        if (Encoding.UTF8.GetByteCount(BodyInput.Text) > MaximumBytes) { Notice("本文は UTF-8 換算で 1 MB 以内で入力してください。", true); return; }
        EditedText = BodyInput.Text;
        DialogResult = true;
    }
    private void OnTextChanged(object sender, TextChangedEventArgs e) => UpdateCount();
    private void OnTrimLines(object sender, RoutedEventArgs e) => ApplyTransform("trim");
    private void OnRemoveBlankLines(object sender, RoutedEventArgs e) => ApplyTransform("removeBlank");
    private void OnJoinLines(object sender, RoutedEventArgs e) => ApplyTransform("join");
    private void OnUpperCase(object sender, RoutedEventArgs e) => ApplyTransform("upper");
    private void OnLowerCase(object sender, RoutedEventArgs e) => ApplyTransform("lower");
    private void OnHalfWidth(object sender, RoutedEventArgs e) => ApplyTransform("half");
    private void OnFullWidth(object sender, RoutedEventArgs e) => ApplyTransform("full");
    private void OnReplace(object sender, RoutedEventArgs e) => ReplaceText(FindInput.Text, ReplacementInput.Text, RegexInput.IsChecked == true);
    private void OnReset(object sender, RoutedEventArgs e) { ApplyText(_original); Notice("編集前の内容に戻しました。Ctrl + Z で取り消せます。"); }
    private void OnSave(object sender, RoutedEventArgs e) => Save();
}
