using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace ClipBridge.App;

public partial class SnippetDialog : Window
{
    public string SnippetTitle { get; private set; } = string.Empty;
    public string SnippetText { get; private set; } = string.Empty;
    public string SnippetGroup { get; private set; } = "共通";

    public SnippetDialog(SnippetItem? item = null, IEnumerable<string>? groups = null)
    {
        InitializeComponent();
        GroupInput.ItemsSource = (groups ?? Array.Empty<string>()).Append("共通").Append(item?.Group ?? "共通")
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        GroupInput.Text = string.IsNullOrWhiteSpace(item?.Group) ? "共通" : item.Group;
        if (item != null)
        {
            if (!string.IsNullOrEmpty(item.Id)) { Title = "定型文を編集"; DialogHeading.Text = "定型文を編集"; }
            TitleInput.Text = item.Title;
            BodyInput.Text = item.Text;
        }
        Loaded += (_, _) => { TitleInput.Focus(); TitleInput.SelectAll(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            { Save(); e.Handled = true; }
        };
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save();
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(TitleInput.Text))
        { ErrorText.Text = "見つけやすいタイトルを入力してください。"; TitleInput.Focus(); return; }
        if (string.IsNullOrWhiteSpace(BodyInput.Text))
        { ErrorText.Text = "定型文の本文を入力してください。"; BodyInput.Focus(); return; }
        if (Encoding.UTF8.GetByteCount(BodyInput.Text) > 64 * 1024)
        { ErrorText.Text = "本文は UTF-8 換算で 64 KB 以内で入力してください。"; BodyInput.Focus(); return; }
        string group = string.IsNullOrWhiteSpace(GroupInput.Text) ? "共通" : GroupInput.Text.Trim();
        if (group.Length > 60)
        { ErrorText.Text = "グループ名は 60 文字以内で入力してください。"; GroupInput.Focus(); return; }
        SnippetTitle = TitleInput.Text.Trim();
        SnippetText = BodyInput.Text;
        SnippetGroup = group;
        DialogResult = true;
    }
}
