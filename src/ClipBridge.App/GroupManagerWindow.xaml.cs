using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClipBridge.App;

public partial class GroupManagerWindow : Window
{
    private readonly ObservableCollection<GroupItem> _groups = new();
    private readonly List<SnippetItem> _snippets;
    private bool _ready;
    public IReadOnlyList<GroupItem> Groups => _groups.ToList();
    public IReadOnlyList<SnippetItem> Snippets => _groups.SelectMany(group => _snippets.Where(x => SameGroup(x.Group, group.Name)))
        .Select((item, index) => item with { SortOrder = index }).ToList();
    // Synthetic UI tests replace only the modal editor; production uses SnippetDialog.
    internal Func<SnippetItem, IReadOnlyList<string>, SnippetItem?>? SnippetEditorOverride { get; set; }

    public GroupManagerWindow(IEnumerable<GroupItem> groups, IEnumerable<SnippetItem> snippets)
    {
        InitializeComponent();
        _snippets = snippets.ToList();
        foreach (var group in groups)
            if (!_groups.Any(x => SameGroup(x.Name, group.Name))) _groups.Add(group);
        foreach (var name in _snippets.Select(x => x.Group).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!_groups.Any(x => SameGroup(x.Name, name))) _groups.Add(new GroupItem(Guid.NewGuid().ToString("N"), name));
        if (!_groups.Any(x => SameGroup(x.Name, "共通"))) _groups.Add(new GroupItem(Guid.NewGuid().ToString("N"), "共通"));
        GroupsList.ItemsSource = _groups;
        _ready = true;
        GroupsList.SelectedIndex = 0;
        RefreshSnippets();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { DialogResult = true; e.Handled = true; }
            else if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None && !GroupNameInput.IsKeyboardFocusWithin && !MoveGroupInput.IsKeyboardFocusWithin)
            { EditSelectedSnippet(); e.Handled = true; }
        };
    }

    private static bool SameGroup(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private GroupItem? CurrentGroup => GroupsList.SelectedItem as GroupItem;
    private void Notice(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(error ? System.Windows.Media.Color.FromRgb(177, 58, 58) : System.Windows.Media.Color.FromRgb(52, 119, 118));
    }

    private bool ValidateGroupName(string name, string? excludeId = null)
    {
        if (string.IsNullOrWhiteSpace(name)) { Notice("グループ名を入力してください。", true); GroupNameInput.Focus(); return false; }
        if (name.Length > 60) { Notice("グループ名は 60 文字以内で入力してください。", true); return false; }
        if (_groups.Any(x => x.Id != excludeId && SameGroup(x.Name, name))) { Notice("同じ名前のグループが既にあります。", true); return false; }
        return true;
    }

    internal bool CreateGroup(string name)
    {
        name = name.Trim();
        if (!ValidateGroupName(name)) return false;
        var group = new GroupItem(Guid.NewGuid().ToString("N"), name);
        _groups.Add(group);
        GroupsList.SelectedItem = group;
        Notice($"「{name}」を作成しました。定型文がなくても保存できます。");
        return true;
    }

    internal bool RenameSelectedGroup(string name)
    {
        if (CurrentGroup is not { } old) return false;
        name = name.Trim();
        if (!ValidateGroupName(name, old.Id)) return false;
        if (SameGroup(old.Name, "共通")) { Notice("「共通」は標準グループのため、名前を変更できません。", true); return false; }
        for (int i = 0; i < _snippets.Count; i++) if (SameGroup(_snippets[i].Group, old.Name)) _snippets[i] = _snippets[i] with { Group = name };
        var renamed = old with { Name = name };
        _groups[_groups.IndexOf(old)] = renamed;
        GroupsList.SelectedItem = renamed;
        RefreshSnippets();
        Notice($"「{old.Name}」を「{name}」に変更しました。");
        return true;
    }

    internal bool DeleteSelectedGroup()
    {
        if (CurrentGroup is not { } group) return false;
        if (SameGroup(group.Name, "共通")) { Notice("「共通」は定型文の移動先に使うため削除できません。", true); return false; }
        int count = 0;
        for (int i = 0; i < _snippets.Count; i++)
            if (SameGroup(_snippets[i].Group, group.Name)) { _snippets[i] = _snippets[i] with { Group = "共通" }; count++; }
        int index = _groups.IndexOf(group);
        _groups.Remove(group);
        GroupsList.SelectedIndex = Math.Min(index, _groups.Count - 1);
        RefreshSnippets();
        Notice($"「{group.Name}」を削除しました。定型文 {count} 件は「共通」へ移動しました。");
        return true;
    }

    internal void MoveGroup(int delta)
    {
        int oldIndex = GroupsList.SelectedIndex, next = oldIndex + delta;
        if (oldIndex < 0 || next < 0 || next >= _groups.Count) return;
        _groups.Move(oldIndex, next);
        GroupsList.SelectedIndex = next;
        GroupsList.ScrollIntoView(GroupsList.SelectedItem);
        RefreshMoveGroups();
        Notice("グループの並び順を変更しました。");
    }

    internal void MoveSnippet(int delta)
    {
        if (SnippetsList.SelectedItem is not SnippetItem selected || CurrentGroup is not { } group) return;
        var current = _snippets.Where(x => SameGroup(x.Group, group.Name)).ToList();
        int index = current.FindIndex(x => x.Id == selected.Id), next = index + delta;
        if (next < 0 || next >= current.Count) return;
        int a = _snippets.FindIndex(x => x.Id == current[index].Id), b = _snippets.FindIndex(x => x.Id == current[next].Id);
        (_snippets[a], _snippets[b]) = (_snippets[b], _snippets[a]);
        RefreshSnippets(selected.Id);
        Notice("定型文の並び順を変更しました。");
    }

    internal bool MoveSelectedSnippetTo(string groupName)
    {
        if (SnippetsList.SelectedItem is not SnippetItem selected || !_groups.Any(x => SameGroup(x.Name, groupName))) return false;
        if (SameGroup(selected.Group, groupName)) { Notice("別のグループを選んでください。", true); return false; }
        int index = _snippets.FindIndex(x => x.Id == selected.Id);
        _snippets.RemoveAt(index);
        _snippets.Add(selected with { Group = groupName });
        RefreshSnippets();
        Notice($"「{selected.Title}」を「{groupName}」へ移動しました。");
        return true;
    }

    internal bool ApplySnippetEdit(SnippetItem edited)
    {
        int index = _snippets.FindIndex(x => x.Id == edited.Id);
        if (index < 0) { Notice("編集する定型文が見つかりません。", true); return false; }
        string title = edited.Title.Trim();
        string groupName = string.IsNullOrWhiteSpace(edited.Group) ? "共通" : edited.Group.Trim();
        if (title.Length == 0 || title.Length > 120 || string.IsNullOrWhiteSpace(edited.Text) || Encoding.UTF8.GetByteCount(edited.Text) > 64 * 1024)
        { Notice("タイトルは 1〜120 文字、本文は空欄を除く 64 KiB 以内で入力してください。", true); return false; }
        if (groupName.Length > 60) { Notice("グループ名は 60 文字以内で入力してください。", true); return false; }
        var destination = _groups.FirstOrDefault(x => SameGroup(x.Name, groupName));
        if (destination == null)
        {
            if (_groups.Count >= 256) { Notice("グループは共通を含めて 256 件まで登録できます。", true); return false; }
            destination = new GroupItem(Guid.NewGuid().ToString("N"), groupName);
            _groups.Add(destination);
        }
        var previous = _snippets[index];
        var updated = previous with { Title = title, Text = edited.Text, Group = destination.Name };
        if (SameGroup(previous.Group, destination.Name)) _snippets[index] = updated;
        else { _snippets.RemoveAt(index); _snippets.Add(updated); }
        GroupsList.SelectedItem = destination;
        RefreshSnippets(updated.Id);
        SnippetsList.ScrollIntoView(SnippetsList.SelectedItem);
        SnippetsList.Focus();
        Notice("定型文を編集しました。管理画面の「変更を保存」で確定します。");
        return true;
    }

    internal bool EditSelectedSnippet()
    {
        if (SnippetsList.SelectedItem is not SnippetItem selected) return false;
        var groupNames = _groups.Select(x => x.Name).ToArray();
        SnippetItem? edited;
        if (SnippetEditorOverride is { } editor) edited = editor(selected, groupNames);
        else
        {
            var dialog = new SnippetDialog(selected, groupNames) { Owner = this };
            edited = dialog.ShowDialog() == true ? selected with { Title = dialog.SnippetTitle, Text = dialog.SnippetText, Group = dialog.SnippetGroup } : null;
        }
        if (edited == null)
        {
            SnippetsList.Focus();
            Notice("定型文の編集をキャンセルしました。");
            return false;
        }
        return ApplySnippetEdit(edited with { Id = selected.Id });
    }

    private void RefreshSnippets(string? selectedId = null)
    {
        if (!_ready) return;
        var group = CurrentGroup;
        var entries = group == null ? new List<SnippetItem>() : _snippets.Where(x => SameGroup(x.Group, group.Name)).ToList();
        SnippetsList.ItemsSource = entries;
        SnippetsList.SelectedItem = entries.FirstOrDefault(x => x.Id == selectedId) ?? entries.FirstOrDefault();
        SnippetsHeading.Text = group == null ? "定型文" : $"定型文　{entries.Count} 件";
        NoSnippetsText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EditSnippetButton.IsEnabled = SnippetsList.SelectedItem != null;
        RefreshMoveGroups();
    }

    private void RefreshMoveGroups()
    {
        string? selectedName = (MoveGroupInput.SelectedItem as GroupItem)?.Name;
        var destinations = _groups.Where(x => CurrentGroup == null || x.Id != CurrentGroup.Id).ToList();
        MoveGroupInput.ItemsSource = destinations;
        MoveGroupInput.SelectedItem = destinations.FirstOrDefault(x => SameGroup(x.Name, selectedName ?? "")) ?? destinations.FirstOrDefault();
        MoveSnippetButton.IsEnabled = SnippetsList.SelectedItem != null && destinations.Count > 0;
    }

    private void OnGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        GroupNameInput.Text = CurrentGroup?.Name ?? "";
        RefreshSnippets();
    }
    private void OnSnippetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        SnippetDetailText.Text = SnippetsList.SelectedItem is SnippetItem item ? item.Text.Replace("\r", "").Replace('\n', ' ') : "定型文を選ぶと、別のグループへ移動できます。";
        MoveSnippetButton.IsEnabled = SnippetsList.SelectedItem != null && MoveGroupInput.HasItems;
        EditSnippetButton.IsEnabled = SnippetsList.SelectedItem != null;
    }
    private void OnAddGroup(object sender, RoutedEventArgs e) => CreateGroup(GroupNameInput.Text);
    private void OnRenameGroup(object sender, RoutedEventArgs e) => RenameSelectedGroup(GroupNameInput.Text);
    private void OnDeleteGroup(object sender, RoutedEventArgs e) => DeleteSelectedGroup();
    private void OnGroupUp(object sender, RoutedEventArgs e) => MoveGroup(-1);
    private void OnGroupDown(object sender, RoutedEventArgs e) => MoveGroup(1);
    private void OnSnippetUp(object sender, RoutedEventArgs e) => MoveSnippet(-1);
    private void OnSnippetDown(object sender, RoutedEventArgs e) => MoveSnippet(1);
    private void OnEditSnippet(object sender, RoutedEventArgs e) => EditSelectedSnippet();
    private void OnSnippetDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(SnippetsList, source) is not ListBoxItem row) return;
        SnippetsList.SelectedItem = row.Content;
        EditSelectedSnippet();
        e.Handled = true;
    }
    private void OnMoveSnippet(object sender, RoutedEventArgs e) { if (MoveGroupInput.SelectedItem is GroupItem group) MoveSelectedSnippetTo(group.Name); }
    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
}
