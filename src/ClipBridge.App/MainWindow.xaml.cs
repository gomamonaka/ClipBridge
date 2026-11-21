using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipBridge.App;

public sealed record HistoryItem(string Id, string Preview, string Detail, DateTimeOffset CreatedUtc, string DeviceName, bool IsImage, byte[]? ImagePng);
public sealed record SnippetItem(string Id, string Title, string Text, string Group = "共通", long SortOrder = 0);
public sealed record GroupItem(string Id, string Name);

public partial class MainWindow : Window
{
    private const double RowHeight = 20;
    private const double ListChrome = 6;
    private readonly ObservableCollection<LibraryRow> _rows = new();
    private readonly Dictionary<string, ViewState> _snippetStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unavailableHistory = new(StringComparer.Ordinal);
    private readonly WheelPageGate _wheel = new();
    private ViewState _historyState = new();
    private List<HistoryItem> _history = new();
    private List<HistoryItem> _latestHistory = new();
    private List<SnippetItem> _snippets = new();
    private List<GroupItem> _groups = new();
    private List<string> _groupNames = new();
    private string _activeSnippetGroup = "";
    private bool _showingSnippets, _initialized, _refreshing, _updatingGroups, _restoringState, _changingSection;
    private bool _contextMenuOpen, _autoPaste = true, _sessionActive, _settingHeight;
    private bool _suppressAutoHide;
    private int _presentationVersion;
    private int _requestedPageSize = 25, _effectivePageSize = 25, _filteredItemCount, _pendingHistoryCount;
    private bool _pagingLoop;
    private double _maxHeightDips = double.PositiveInfinity, _lastAutomaticHeight, _nonClientReserve = 20;
    private double? _manualHeightDips;

    public event Action? OpeningRequested;
    public event Action? LayoutPlacementRequested;
    private bool _layoutPlacementQueued;
    public event Action<HistoryItem>? CopyHistoryRequested;
    public event Action<SnippetItem>? CopySnippetRequested;
    public event Action<HistoryItem>? CopyHistoryOnlyRequested;
    public event Action<SnippetItem>? CopySnippetOnlyRequested;
    public event Action<HistoryItem>? SaveHistoryAsSnippetRequested;
    public event Action<SnippetItem?>? EditSnippetRequested;
    public event Action<SnippetItem>? DeleteSnippetRequested;
    public event Action<HistoryItem>? DeleteHistoryRequested;
    public event Action? PauseRequested;
    public event Action? SettingsRequested;
    public event Action? ManageGroupsRequested;
    public event Action? ImportCsvRequested;
    public event Action? ExportCsvRequested;
    public event Action? ImportCliborRequested;
    public event Action? BackupRequested;
    public event Action? RestoreRequested;
    public event Action<HistoryItem>? EditHistoryRequested;
    public event Action? PagingSettingsRequested;
    public bool ForceClose { get; set; }
    public bool SuppressAutoHide
    {
        get => _suppressAutoHide;
        set
        {
            if (_suppressAutoHide == value) return;
            _suppressAutoHide = value;
            if (!value && _initialized) QueueInactiveDismissal();
        }
    }

    internal int RequestedPageSize => _requestedPageSize;
    internal int EffectivePageSize => _effectivePageSize;
    internal int CurrentPageIndex => ActiveState.PageIndex;
    internal int PageCount => Math.Max(1, (_filteredItemCount + _effectivePageSize - 1) / _effectivePageSize);
    internal int FilteredItemCount => _filteredItemCount;
    internal string CurrentGroupName => _activeSnippetGroup;
    internal int PendingHistoryCount => _pendingHistoryCount;
    internal bool IsShowingSnippets => _showingSnippets;
    internal bool PagingLoop => _pagingLoop;

    private sealed class ViewState
    {
        internal string Query = "";
        internal bool SearchOpen;
        internal int PageIndex;
        internal int PageSize = 25;
        internal string? SelectedId;
    }
    private ViewState ActiveState => _showingSnippets ? SnippetState(_activeSnippetGroup) : _historyState;
    private ViewState SnippetState(string group)
    {
        if (!_snippetStates.TryGetValue(group, out var state)) _snippetStates[group] = state = new ViewState { PageSize = _effectivePageSize };
        return state;
    }

    public MainWindow()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _rows;
        _initialized = true;
        IsVisibleChanged += OnVisibilityChanged;
        SizeChanged += OnSizeChanged;
        GroupFilter.DropDownClosed += (_, _) => QueueInactiveDismissal();
        Loaded += (_, _) =>
        {
            if (Content is FrameworkElement content && content.ActualHeight > 0)
                _nonClientReserve = Math.Ceiling(Math.Max(0, ActualHeight - content.ActualHeight)) + 2;
            RecalculateLayout();
            RefreshRows();
        };
        HistoryNav.IsChecked = true;
        UpdateGroups();
        FitToWorkArea(Math.Max(260, SystemParameters.WorkArea.Height - 16));
        RefreshRows();
    }

    public void ConfigurePaging(int pageSize, bool loop)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ConfigurePaging(pageSize, loop)); return; }
        _requestedPageSize = Math.Clamp(pageSize, 1, 36);
        _pagingLoop = loop;
        _manualHeightDips = null;
        _wheel.Reset();
        RecalculateLayout();
        RefreshRows();
    }

    public void FitToWorkArea(double maxHeightDips)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => FitToWorkArea(maxHeightDips)); return; }
        if (double.IsNaN(maxHeightDips) || maxHeightDips <= 0) return;
        _maxHeightDips = maxHeightDips;
        if (_manualHeightDips is { } manual && manual > maxHeightDips) _manualHeightDips = maxHeightDips;
        RecalculateLayout();
        RefreshRows();
    }

    private void RecalculateLayout(bool resizeWindow = true)
    {
        if (!_initialized) return;
        // Keep the picker close to a native text menu; full text remains in row tooltips.
        PreviewRow.Height = new GridLength(0);
        PreviewPanel.Visibility = Visibility.Collapsed;
        double baseChrome = 24 + 20 + 2 + ListChrome + _nonClientReserve + (NoticeBar.Visibility == Visibility.Visible ? 22 : 0);
        double chrome = baseChrome + (SearchBar.Visibility == Visibility.Visible ? 34 : 0);
        // Visible height changes request placement inside the monitor.
        int itemCount = _showingSnippets ? FilteredSnippets().Count : FilteredHistory().Count;
        int visibleRows = Math.Min(_requestedPageSize, Math.Max(4, itemCount));
        double preferredHeight = chrome + visibleRows * RowHeight;
        double target = _manualHeightDips.HasValue ? Math.Min(_manualHeightDips.Value, _maxHeightDips) : Math.Min(preferredHeight, _maxHeightDips);
        int capacity = Math.Max(1, (int)Math.Floor((target - chrome) / RowHeight));
        _effectivePageSize = Math.Min(_requestedPageSize, capacity);
        if (resizeWindow)
        {
            _settingHeight = true;
            try
            {
                MinHeight = Math.Min(260, Math.Max(1, target));
                _lastAutomaticHeight = target;
                bool changed = Math.Abs(Height - target) > 0.5;
                Height = target;
                if (changed && IsVisible && !_layoutPlacementQueued)
                {
                    _layoutPlacementQueued = true;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        _layoutPlacementQueued = false;
                        if (IsVisible && !SuppressAutoHide) LayoutPlacementRequested?.Invoke();
                    }));
                }
            }
            finally { _settingHeight = false; }
        }
    }

    private void OnPopupSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // A Border's corner radius does not clip its children. Keep the whole picker
        // within the rounded outline, including after page/group/work-area resizing.
        PopupOutline.Rect = new Rect(e.NewSize);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_initialized || _settingHeight || !IsLoaded || !e.HeightChanged || Math.Abs(e.NewSize.Height - _lastAutomaticHeight) < 1) return;
        _manualHeightDips = e.NewSize.Height;
        RecalculateLayout(false);
        RefreshRows();
    }

    public void Render(IEnumerable<HistoryItem> history, IEnumerable<SnippetItem> snippets, string status, bool paused)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Render(history, snippets, status, paused)); return; }
        var nextHistory = history.OrderByDescending(x => x.CreatedUtc).ToList();
        var nextSnippets = snippets.ToList();
        bool snippetsChanged = !_snippets.SequenceEqual(nextSnippets);
        _latestHistory = nextHistory;
        bool historyChanged;
        if (IsVisible && _sessionActive)
        {
            var currentById = nextHistory.ToDictionary(x => x.Id, StringComparer.Ordinal);
            var nextSnapshot = _history.Select(x => currentById.TryGetValue(x.Id, out var fresh) ? fresh : x).ToList();
            var unavailable = _history.Where(x => !currentById.ContainsKey(x.Id)).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            historyChanged = !_history.SequenceEqual(nextSnapshot) || !_unavailableHistory.SetEquals(unavailable);
            _history = nextSnapshot;
            _unavailableHistory.Clear(); _unavailableHistory.UnionWith(unavailable);
        }
        else
        {
            historyChanged = !_history.SequenceEqual(nextHistory) || _unavailableHistory.Count > 0;
            _history = nextHistory.ToList();
            _unavailableHistory.Clear();
        }
        _snippets = nextSnippets;
        AppCaption.Text = paused ? "ClipBridge · 共有停止中" : "ClipBridge";
        AppCaption.ToolTip = status;
        SharingMenuItem.Header = paused ? "自動共有を再開" : "自動共有を一時停止";
        PauseButton.Content = paused ? "共有停止中" : "共有中";
        PauseButton.Foreground = Brush(paused ? "#927239" : "#377F75");
        PauseButton.ToolTip = status + (paused ? "\nクリックで共有を再開" : "\nクリックで共有を一時停止");
        if (snippetsChanged) UpdateGroups();
        UpdatePendingHistory();
        if (historyChanged || snippetsChanged) RefreshRows();
    }

    public void RenderGroups(IEnumerable<GroupItem> groups)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => RenderGroups(groups)); return; }
        var next = groups.ToList();
        if (_groups.SequenceEqual(next)) return;
        _groups = next;
        UpdateGroups();
        RefreshRows();
    }

    public void SetInteractionMode(bool autoPaste, bool doubleControl)
    {
        _autoPaste = autoPaste;
        CopyButton.Content = autoPaste ? "貼り付け ↵" : "コピー ↵";
        ShortcutHint.Text = doubleControl ? "Ctrl を 2 回で呼び出し" : "Ctrl + Alt + V";
        ResetHint();
    }
    public void ShowHistory() { BeginSession(); SelectSection(false); ShowAndFocus(); }
    public void ShowSnippets() { BeginSession(); SelectSection(true); ShowAndFocus(); }
    public void ShowNotice(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ShowNotice(message)); return; }
        NoticeBar.Visibility = Visibility.Visible;
        RecalculateLayout();
        NoticeText.Text = message; NoticeText.ToolTip = message; NoticeText.Foreground = Brush("#377F75");
    }

    private void BeginSession()
    {
        if (_sessionActive) return;
        _sessionActive = true;
        _history = _latestHistory.ToList();
        _unavailableHistory.Clear();
        _historyState = new ViewState { PageSize = _effectivePageSize };
        _snippetStates.Clear();
        _activeSnippetGroup = "";
        _wheel.Reset();
        UpdateGroups();
        _refreshing = true;
        try { ItemsList.SelectedIndex = -1; } finally { _refreshing = false; }
        // Clear the old session's controls before an opposite-tab opening saves them.
        RestoreActiveState();
        UpdatePendingHistory();
    }
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            if (!_sessionActive) { BeginSession(); RestoreActiveState(); RecalculateLayout(); RefreshRows(); }
        }
        else
        {
            _presentationVersion++;
            _sessionActive = false;
            _history = _latestHistory.ToList();
            _unavailableHistory.Clear();
            UpdatePendingHistory();
        }
    }
    private void ShowAndFocus()
    {
        ItemMenu.IsOpen = false; ToolsMenu.IsOpen = false; GroupFilter.IsDropDownOpen = false;
        _presentationVersion++;
        OpeningRequested?.Invoke();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate(); FocusList(); ResetHint();
    }
    private void FocusList()
    {
        ItemsList.Focus();
        if (ItemsList.SelectedItem != null) ItemsList.ScrollIntoView(ItemsList.SelectedItem);
    }
    private void SaveActiveState()
    {
        ActiveState.Query = SearchInput.Text;
        ActiveState.SearchOpen = SearchBar.Visibility == Visibility.Visible;
        ActiveState.SelectedId = (ItemsList.SelectedItem as LibraryRow)?.Id;
    }
    private void RestoreActiveState(bool keepSearchFocus = false)
    {
        _restoringState = true;
        try
        {
            SearchInput.Text = ActiveState.Query;
            if (keepSearchFocus) ActiveState.SearchOpen = true;
            SearchBar.Visibility = ActiveState.SearchOpen || ActiveState.Query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            GroupBar.Visibility = _showingSnippets ? Visibility.Visible : Visibility.Collapsed;
            AppCaption.Visibility = _showingSnippets ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _restoringState = false; }
    }
    private void SelectSection(bool snippets, bool keepSearchFocus = false)
    {
        if (_changingSection) return;
        bool changed = _showingSnippets != snippets;
        bool searchFocused = keepSearchFocus && SearchInput.IsKeyboardFocusWithin;
        if (changed) SaveActiveState();
        _changingSection = true;
        try
        {
            _showingSnippets = snippets;
            HistoryNav.IsChecked = !snippets; SnippetsNav.IsChecked = snippets;
            RestoreActiveState(searchFocused);
        }
        finally { _changingSection = false; }
        RecalculateLayout(); RefreshRows(); ResetHint();
        if (searchFocused) { SearchInput.Focus(); SearchInput.CaretIndex = SearchInput.Text.Length; }
    }

    private void UpdateGroups()
    {
        string previousGroup = _activeSnippetGroup;
        _groupNames = _groups.Select(x => NormalizeGroup(x.Name)).Concat(_snippets.Select(x => NormalizeGroup(x.Group)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _activeSnippetGroup = _groupNames.FirstOrDefault(x => x.Equals(_activeSnippetGroup, StringComparison.OrdinalIgnoreCase))
            ?? _groupNames.FirstOrDefault() ?? "";
        _updatingGroups = true;
        try { GroupFilter.ItemsSource = _groupNames; GroupFilter.SelectedItem = _activeSnippetGroup; }
        finally { _updatingGroups = false; }
        if (previousGroup != _activeSnippetGroup && _showingSnippets) { RestoreActiveState(); RecalculateLayout(); }
    }
    private List<HistoryItem> FilteredHistory() => _history.Where(x => Matches(x.Preview, ActiveState.Query.Trim()) || Matches(x.Detail, ActiveState.Query.Trim()) || Matches(x.DeviceName, ActiveState.Query.Trim())).ToList();
    private List<SnippetItem> FilteredSnippets() => _snippets.Where(x => NormalizeGroup(x.Group).Equals(_activeSnippetGroup, StringComparison.OrdinalIgnoreCase) &&
        (Matches(x.Title, ActiveState.Query.Trim()) || Matches(x.Text, ActiveState.Query.Trim()) || Matches(NormalizeGroup(x.Group), ActiveState.Query.Trim()))).ToList();

    private void RefreshRows()
    {
        if (!_initialized || _restoringState) return;
        var state = ActiveState;
        var history = _showingSnippets ? null : FilteredHistory();
        var snippets = _showingSnippets ? FilteredSnippets() : null;
        _filteredItemCount = history?.Count ?? snippets!.Count;
        if (state.PageSize != _effectivePageSize)
        {
            int anchor = state.PageIndex * state.PageSize;
            int selectedIndex = history != null ? history.FindIndex(x => x.Id == state.SelectedId) : snippets!.FindIndex(x => x.Id == state.SelectedId);
            if (selectedIndex >= 0) anchor = selectedIndex;
            state.PageIndex = anchor / _effectivePageSize;
            state.PageSize = _effectivePageSize;
        }
        state.PageIndex = Math.Clamp(state.PageIndex, 0, PageCount - 1);
        int offset = state.PageIndex * _effectivePageSize;
        _refreshing = true;
        try
        {
            var nextRows = new List<LibraryRow>(_effectivePageSize);
            DateTime displayDate = DateTime.Today;
            if (snippets != null)
            {
                foreach (var item in snippets.Skip(offset).Take(_effectivePageSize))
                {
                    int index = nextRows.Count;
                    var existing = index < _rows.Count ? _rows[index] : null;
                    nextRows.Add(existing is not null && existing.Snippet == item ? existing : new LibraryRow(item, index));
                }
            }
            else
            {
                foreach (var item in history!.Skip(offset).Take(_effectivePageSize))
                {
                    int index = nextRows.Count;
                    bool unavailable = _unavailableHistory.Contains(item.Id);
                    var existing = index < _rows.Count ? _rows[index] : null;
                    nextRows.Add(existing is not null && existing.History == item && existing.IsAvailable == !unavailable && existing.DisplayDate == displayDate
                        ? existing : new LibraryRow(item, index, unavailable));
                }
            }
            // Placement can recalculate the same page more than once. Keeping its rows
            // avoids destroying and regenerating WPF item containers before every show.
            if (!_rows.SequenceEqual(nextRows))
            {
                _rows.Clear();
                foreach (var row in nextRows) _rows.Add(row);
            }
            ResultCount.Text = state.Query.Trim().Length > 0 ? $"{_filteredItemCount:N0} 件の検索結果" : $"{_filteredItemCount:N0} 件";
            EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyHeading.Text = state.Query.Trim().Length > 0 ? "一致する項目がありません" : _showingSnippets ? "このグループには定型文がありません" : "最初のコピーを待っています";
            EmptyDetail.Text = state.Query.Trim().Length > 0 ? "別の言葉で検索してください。" : _showingSnippets ? "「＋ 登録」で追加できます。ホイールで別のグループへ移動できます。" : "テキストや画像をコピーすると、ここに履歴が並びます。";
            ItemsList.SelectedItem = _rows.FirstOrDefault(x => x.Id == state.SelectedId) ?? _rows.FirstOrDefault(x => x.IsAvailable) ?? _rows.FirstOrDefault();
            state.SelectedId = (ItemsList.SelectedItem as LibraryRow)?.Id;
        }
        finally { _refreshing = false; }
        UpdatePreview(); UpdatePageControls();
    }

    private void UpdatePageControls()
    {
        int first = _filteredItemCount == 0 ? 0 : ActiveState.PageIndex * _effectivePageSize + 1;
        int last = Math.Min(_filteredItemCount, (ActiveState.PageIndex + 1) * _effectivePageSize);
        PageIndicator.Text = $"{(_showingSnippets ? "定型文" : "履歴")} {ActiveState.PageIndex + 1}（{first:N0}–{last:N0} / {_filteredItemCount:N0} 件）";
        PageIndicator.ToolTip = $"{ActiveState.PageIndex + 1} / {PageCount} ページ　表示 {_effectivePageSize} 件（設定 {_requestedPageSize} 件）\nホイール・PageUp / PageDown で切り替え";
        PreviousPageButton.IsEnabled = CanAdvancePage(-1); NextPageButton.IsEnabled = CanAdvancePage(1);
    }
    private bool CanAdvancePage(int direction)
    {
        int next = ActiveState.PageIndex + direction;
        if (next >= 0 && next < PageCount) return true;
        if (!_showingSnippets || ActiveState.Query.Trim().Length > 0) return _pagingLoop && PageCount > 1;
        int groupIndex = _groupNames.FindIndex(x => x.Equals(_activeSnippetGroup, StringComparison.OrdinalIgnoreCase));
        if (groupIndex < 0) return false;
        return groupIndex + direction >= 0 && groupIndex + direction < _groupNames.Count || _pagingLoop && (_groupNames.Count > 1 || PageCount > 1);
    }
    internal bool AdvancePage(int direction)
    {
        if (direction == 0) return false;
        direction = Math.Sign(direction);
        int next = ActiveState.PageIndex + direction;
        if (next >= 0 && next < PageCount) { ActiveState.PageIndex = next; ActiveState.SelectedId = null; RefreshRows(); return true; }
        if (_showingSnippets && ActiveState.Query.Trim().Length == 0)
        {
            int index = _groupNames.FindIndex(x => x.Equals(_activeSnippetGroup, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;
            int nextGroup = index + direction;
            if (nextGroup < 0 || nextGroup >= _groupNames.Count)
            {
                if (!_pagingLoop || _groupNames.Count == 0 || _groupNames.Count == 1 && PageCount == 1) return false;
                nextGroup = nextGroup < 0 ? _groupNames.Count - 1 : 0;
            }
            return NavigateGroup(_groupNames[nextGroup], direction);
        }
        if (!_pagingLoop || PageCount <= 1) return false;
        ActiveState.PageIndex = next < 0 ? PageCount - 1 : 0;
        ActiveState.SelectedId = null; RefreshRows(); return true;
    }
    private bool NavigateGroup(string group, int direction)
    {
        SaveActiveState();
        _activeSnippetGroup = group;
        _updatingGroups = true;
        try { GroupFilter.SelectedItem = group; } finally { _updatingGroups = false; }
        RestoreActiveState(); RecalculateLayout();
        ActiveState.PageIndex = 0; ActiveState.SelectedId = null; ActiveState.PageSize = _effectivePageSize;
        RefreshRows();
        if (direction < 0) { ActiveState.PageIndex = PageCount - 1; ActiveState.SelectedId = null; RefreshRows(); }
        return true;
    }
    internal bool HandleWheel(int delta, long timestampMilliseconds) => _wheel.TryStep(delta, timestampMilliseconds, out int direction) && AdvancePage(direction);
    private void OnListMouseWheel(object sender, MouseWheelEventArgs e) { HandleWheel(e.Delta, Environment.TickCount64); e.Handled = true; }

    internal void RefreshHistorySnapshot()
    {
        _history = _latestHistory.ToList(); _unavailableHistory.Clear();
        _historyState.PageIndex = 0; _historyState.SelectedId = null; _historyState.PageSize = _effectivePageSize;
        UpdatePendingHistory(); RefreshRows();
    }
    private void UpdatePendingHistory()
    {
        var snapshotIds = _history.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        _pendingHistoryCount = _sessionActive && IsVisible ? _latestHistory.Count(x => !snapshotIds.Contains(x.Id)) : 0;
        PendingHistoryBar.Visibility = _pendingHistoryCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        PendingHistoryButton.Content = _pendingHistoryCount > 0 ? $"新着 {_pendingHistoryCount:N0} 件" : "履歴を更新";
        PendingHistoryButton.Background = Brush(_pendingHistoryCount > 0 ? "#D9F0F1" : "#00000000");
        PendingHistoryButton.ToolTip = _pendingHistoryCount > 0 ? "表示中の履歴の順番を保持しています。クリックで最新の履歴の先頭へ移動します。" : "最新の履歴を先頭ページから表示します。";
    }

    private static string NormalizeGroup(string? value) => string.IsNullOrWhiteSpace(value) ? "共通" : value.Trim();
    private static bool Matches(string text, string query) => query.Length == 0 || text.Contains(query, StringComparison.OrdinalIgnoreCase);
    private void UpdatePreview()
    {
        var row = ItemsList.SelectedItem as LibraryRow;
        CopyButton.IsEnabled = row?.IsAvailable == true;
        UseSelectionMenu.IsEnabled = CopyButton.IsEnabled;
        PreviewMeta.Text = row == null ? "プレビュー" : row.IsAvailable ? row.Meta : "削除済みの履歴です。更新すると一覧から除かれます。";
        string detail = row?.History?.Detail ?? row?.Snippet?.Text ?? "ホイールでページ切替。↑↓ で選択し、Enter で取り出します。";
        PreviewContent.Text = detail.Length > 1200 ? detail[..1200] + "…" : detail;
        PreviewContent.ToolTip = row?.ToolTipText;
    }
    private void CopySelection(bool onlyCopy = false)
    {
        if (ItemsList.SelectedItem is not LibraryRow row) return;
        if (!row.IsAvailable) { ShowNotice("この履歴は削除済みです。「履歴を更新」で最新の一覧に戻せます。"); return; }
        if (row.History is { } history) { if (onlyCopy) CopyHistoryOnlyRequested?.Invoke(history); else CopyHistoryRequested?.Invoke(history); }
        else if (row.Snippet is { } snippet) { if (onlyCopy) CopySnippetOnlyRequested?.Invoke(snippet); else CopySnippetRequested?.Invoke(snippet); }
    }
    private void DeleteSelection()
    {
        if (ItemsList.SelectedItem is not LibraryRow { IsAvailable: true } row) return;
        WithAutoHideSuppressed(() => { if (row.History is { } history) DeleteHistoryRequested?.Invoke(history); else if (row.Snippet is { } snippet) DeleteSnippetRequested?.Invoke(snippet); });
    }
    private void EditSelection()
    {
        if (ItemsList.SelectedItem is not LibraryRow { IsAvailable: true } row) return;
        if (row.Snippet is { } snippet) WithAutoHideSuppressed(() => EditSnippetRequested?.Invoke(snippet));
        else if (row.History is { IsImage: false } history) WithAutoHideSuppressed(() => EditHistoryRequested?.Invoke(history));
    }
    private void RegisterSelection()
    {
        if (ItemsList.SelectedItem is LibraryRow { IsAvailable: true, History: { IsImage: false } history }) WithAutoHideSuppressed(() => SaveHistoryAsSnippetRequested?.Invoke(history));
    }
    private void WithAutoHideSuppressed(Action action)
    {
        bool previous = SuppressAutoHide; SuppressAutoHide = true;
        try { action(); } finally { SuppressAutoHide = previous; }
    }
    private void ResetHint()
    {
        if (NoticeBar.Visibility == Visibility.Visible) { NoticeBar.Visibility = Visibility.Collapsed; RecalculateLayout(); }
        NoticeText.Text = _autoPaste ? "ホイール / PgUp・PgDn　1–0 / A–Z で貼り付け" : "ホイール / PgUp・PgDn　1–0 / A–Z でコピー";
        NoticeText.ToolTip = "ホイール：履歴はページ、定型文はグループを切替\nEnter：使用　Esc：閉じる　←→：履歴・定型文\nCtrl + F：検索　Ctrl + Shift + N：登録　F2：編集\nCtrl + C：コピーのみ　Delete：削除　Shift + F10：項目メニュー";
        NoticeText.Foreground = Brush("#647A8E");
    }
    private void OpenSearch()
    {
        ActiveState.SearchOpen = true; SearchBar.Visibility = Visibility.Visible;
        RecalculateLayout(); RefreshRows(); SearchInput.Focus(); SearchInput.SelectAll();
    }
    private void PrepareContextMenu()
    {
        var row = ItemsList.SelectedItem as LibraryRow;
        SaveSnippetMenu.Visibility = _showingSnippets ? Visibility.Collapsed : Visibility.Visible;
        SaveSnippetMenu.IsEnabled = row is { IsAvailable: true, History.IsImage: false };
        EditSnippetMenu.Visibility = _showingSnippets ? Visibility.Visible : Visibility.Collapsed;
        EditHistoryMenu.Visibility = _showingSnippets ? Visibility.Collapsed : Visibility.Visible;
        EditHistoryMenu.IsEnabled = row is { IsAvailable: true, History.IsImage: false };
        ItemMenu.IsEnabled = row?.IsAvailable == true;
    }
    private void OnHistoryNavigation(object sender, RoutedEventArgs e) { if (_initialized && !_changingSection) { SelectSection(false); FocusList(); } }
    private void OnSnippetsNavigation(object sender, RoutedEventArgs e) { if (_initialized && !_changingSection) { SelectSection(true); FocusList(); } }
    private void OnSectionHover(object sender, MouseEventArgs e)
    {
        if (!_initialized || _contextMenuOpen || Mouse.LeftButton != MouseButtonState.Released || SuppressAutoHide) return;
        if (sender == HistoryNav && _showingSnippets) SelectSection(false, true);
        else if (sender == SnippetsNav && !_showingSnippets) SelectSection(true, true);
    }
    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized || _restoringState) return;
        ActiveState.Query = SearchInput.Text; ActiveState.PageIndex = 0; ActiveState.SelectedId = null;
        if (SearchInput.Text.Length > 0) { ActiveState.SearchOpen = true; SearchBar.Visibility = Visibility.Visible; }
        RecalculateLayout(); RefreshRows();
    }
    private void OnGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updatingGroups) return;
        SaveActiveState(); _activeSnippetGroup = GroupFilter.SelectedItem as string ?? _groupNames.FirstOrDefault() ?? "";
        RestoreActiveState(); RecalculateLayout(); RefreshRows();
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && !_refreshing) { ActiveState.SelectedId = (ItemsList.SelectedItem as LibraryRow)?.Id; UpdatePreview(); }
    }
    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ItemsList, source) is ListBoxItem item)
        { ItemsList.SelectedItem = item.Content; CopySelection(); e.Handled = true; }
    }
    private void OnItemRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ItemsList, source) is ListBoxItem item) ItemsList.SelectedItem = item.Content;
    }
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e) { PrepareContextMenu(); if (ItemsList.SelectedItem is not LibraryRow { IsAvailable: true }) e.Handled = true; else _contextMenuOpen = true; }
    private void OnContextMenuOpened(object sender, RoutedEventArgs e) => _contextMenuOpen = true;
    private void OnContextMenuClosed(object sender, RoutedEventArgs e)
    {
        _contextMenuOpen = ItemMenu.IsOpen || ToolsMenu.IsOpen;
        QueueInactiveDismissal();
    }
    private void OnCopy(object sender, RoutedEventArgs e) => CopySelection();
    private void OnCopyOnly(object sender, RoutedEventArgs e) => CopySelection(true);
    private void OnSaveHistoryAsSnippet(object sender, RoutedEventArgs e) => RegisterSelection();
    private void OnAddSnippet(object sender, RoutedEventArgs e) => WithAutoHideSuppressed(() => EditSnippetRequested?.Invoke(null));
    private void OnEditSnippet(object sender, RoutedEventArgs e) => EditSelection();
    private void OnEditHistory(object sender, RoutedEventArgs e) => EditSelection();
    private void OnManageGroups(object sender, RoutedEventArgs e) => WithAutoHideSuppressed(() => ManageGroupsRequested?.Invoke());
    private void OnImportCsv(object sender, RoutedEventArgs e) => WithAutoHideSuppressed(() => ImportCsvRequested?.Invoke());
    private void OnExportCsv(object sender, RoutedEventArgs e) => WithAutoHideSuppressed(() => ExportCsvRequested?.Invoke());
    private void OnImportClibor(object sender, RoutedEventArgs e) => WithAutoHideSuppressed(() => ImportCliborRequested?.Invoke());
    private void OnBackup(object sender, RoutedEventArgs e) => WithAutoHideSuppressed(() => BackupRequested?.Invoke());
    private void OnRestore(object sender, RoutedEventArgs e) => WithAutoHideSuppressed(() => RestoreRequested?.Invoke());
    private void OnPagingSettings(object sender, RoutedEventArgs e) => WithAutoHideSuppressed(() => PagingSettingsRequested?.Invoke());
    private void OnTools(object sender, RoutedEventArgs e) { ToolsMenu.PlacementTarget = ToolsButton; ToolsMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; ToolsMenu.IsOpen = true; }
    private void OnPreviousPage(object sender, RoutedEventArgs e) { AdvancePage(-1); FocusList(); }
    private void OnNextPage(object sender, RoutedEventArgs e) { AdvancePage(1); FocusList(); }
    private void OnRefreshHistory(object sender, RoutedEventArgs e) { RefreshHistorySnapshot(); SelectSection(false); FocusList(); }
    private void OnDelete(object sender, RoutedEventArgs e) => DeleteSelection();
    private void OnPause(object sender, RoutedEventArgs e) => PauseRequested?.Invoke();
    private void OnSettings(object sender, RoutedEventArgs e) => WithAutoHideSuppressed(() => SettingsRequested?.Invoke());
    private void OnSearch(object sender, RoutedEventArgs e) => OpenSearch();
    private void OnCloseSearch(object sender, RoutedEventArgs e)
    {
        ActiveState.Query = ""; ActiveState.SearchOpen = false; ActiveState.PageIndex = 0; ActiveState.SelectedId = null;
        RestoreActiveState(); RecalculateLayout(); RefreshRows(); FocusList();
    }
    private void OnHide(object sender, RoutedEventArgs e) => Hide();
    private void OnWindowClosing(object? sender, CancelEventArgs e) { if (!ForceClose) { e.Cancel = true; Hide(); } }
    internal void DismissFromOutsideClick()
    {
        if (!IsVisible || SuppressAutoHide) return;
        ItemMenu.IsOpen = false; ToolsMenu.IsOpen = false; GroupFilter.IsDropDownOpen = false;
        Hide();
    }
    private void OnWindowDeactivated(object? sender, EventArgs e) => QueueInactiveDismissal();
    private void QueueInactiveDismissal()
    {
        int version = _presentationVersion;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (version == _presentationVersion && !SuppressAutoHide && !_contextMenuOpen &&
                !ItemMenu.IsOpen && !ToolsMenu.IsOpen && !GroupFilter.IsDropDownOpen && !IsActive && IsVisible) Hide();
        }));
    }
    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node != null) { if (node is System.Windows.Controls.Primitives.ButtonBase or ComboBox or TextBox) return; node = VisualTreeHelper.GetParent(node); }
        if (e.ChangedButton == MouseButton.Left && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_contextMenuOpen || GroupFilter.IsDropDownOpen) return;
        e.Handled = HandlePopupKey(e.Key, Keyboard.Modifiers, SearchInput.IsKeyboardFocusWithin || GroupFilter.IsKeyboardFocusWithin);
    }
    internal bool HandlePopupKey(Key key, ModifierKeys modifiers, bool editingText = false)
    {
        if (key == Key.Escape) { Hide(); return true; }
        if (key == Key.F && (modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift))) { OpenSearch(); return true; }
        if (key == Key.N && modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { WithAutoHideSuppressed(() => EditSnippetRequested?.Invoke(null)); return true; }
        if (key == Key.PageDown && modifiers == ModifierKeys.None) { AdvancePage(1); return true; }
        if (key == Key.PageUp && modifiers == ModifierKeys.None) { AdvancePage(-1); return true; }
        if (key == Key.Enter && modifiers == ModifierKeys.None) { CopySelection(); return true; }
        if ((key == Key.Up || key == Key.Down) && modifiers == ModifierKeys.None)
        {
            int direction = key == Key.Down ? 1 : -1;
            for (int index = ItemsList.SelectedIndex + direction; index >= 0 && index < _rows.Count; index += direction)
                if (_rows[index].IsAvailable) { ItemsList.SelectedIndex = index; break; }
            return true;
        }
        if (editingText) return false;
        if ((key == Key.Left || key == Key.Right) && modifiers == ModifierKeys.None) { SelectSection(key == Key.Right); FocusList(); return true; }
        if (key == Key.Delete && modifiers == ModifierKeys.None) { DeleteSelection(); return true; }
        if (key == Key.F2 && modifiers == ModifierKeys.None) { EditSelection(); return true; }
        if (key == Key.C && modifiers == ModifierKeys.Control) { CopySelection(true); return true; }
        if (key == Key.F10 && modifiers == ModifierKeys.Shift)
        {
            PrepareContextMenu();
            if (ItemsList.SelectedItem is LibraryRow { IsAvailable: true }) { ItemMenu.PlacementTarget = ItemsList; ItemMenu.IsOpen = true; }
            return true;
        }
        if (modifiers != ModifierKeys.None) return false;
        int indexKey = key >= Key.D1 && key <= Key.D9 ? (int)key - (int)Key.D1 : key == Key.D0 ? 9 :
            key >= Key.A && key <= Key.Z ? (int)key - (int)Key.A + 10 : key >= Key.NumPad1 && key <= Key.NumPad9 ? (int)key - (int)Key.NumPad1 : key == Key.NumPad0 ? 9 : -1;
        if (indexKey < 0 || indexKey >= _rows.Count) return false;
        ItemsList.SelectedIndex = indexKey; CopySelection(); return true;
    }
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    public void SetSyntheticDemo()
    {
        var now = DateTimeOffset.UtcNow;
        Render(new[]
        {
            new HistoryItem("demo-1", "次回ミーティングの確認", "次回ミーティングの確認\n10 月 20 日（火）14:00 からオンラインでお願いします。\n議題：新しい画面のレビュー、リリース日程の確認", now.AddMinutes(-2), "仕事のノート PC", false, null),
            new HistoryItem("demo-2", "https://example.com/design/clipbridge", "https://example.com/design/clipbridge", now.AddMinutes(-8), "自宅のデスクトップ", false, null),
            new HistoryItem("demo-3", "画面キャプチャ", "PNG 画像　1280 × 720", now.AddMinutes(-15), "仕事のノート PC", true, null),
            new HistoryItem("demo-4", "本日の作業メモ", "本日の作業メモ\n・仕様書の確認\n・資料の更新\n・共有フォルダーの整理", now.AddMinutes(-32), "自宅のデスクトップ", false, null),
            new HistoryItem("demo-5", "東京都千代田区丸の内 1-1-1", "東京都千代田区丸の内 1-1-1", now.AddHours(-1), "仕事のノート PC", false, null),
            new HistoryItem("demo-6", "ご確認のほど、よろしくお願いいたします。", "ご確認のほど、よろしくお願いいたします。", now.AddHours(-1.2), "自宅のデスクトップ", false, null),
            new HistoryItem("demo-7", "リリース資料 / 最終版", "リリース資料 / 最終版", now.AddHours(-1.3), "仕事のノート PC", false, null),
            new HistoryItem("demo-8", "会議室 B：10:30 – 11:00", "会議室 B：10:30 – 11:00", now.AddHours(-1.4), "自宅のデスクトップ", false, null),
            new HistoryItem("demo-9", "デザインの修正点をまとめました。", "デザインの修正点をまとめました。\n添付資料をご確認ください。", now.AddHours(-2), "自宅のデスクトップ", false, null),
            new HistoryItem("demo-10", "03-1234-5678", "03-1234-5678", now.AddHours(-2.2), "仕事のノート PC", false, null),
            new HistoryItem("demo-11", "ありがとうございます。承知しました。", "ありがとうございます。承知しました。", now.AddHours(-2.4), "自宅のデスクトップ", false, null),
            new HistoryItem("demo-12", "次の端末でも、続きから。", "次の端末でも、続きから。", now.AddHours(-3), "自宅のデスクトップ", false, null),
        }, new[]
        {
            new SnippetItem("s1", "メールのあいさつ", "いつもお世話になっております。\nご連絡いただき、ありがとうございます。", "メール"),
            new SnippetItem("s2", "日程確認", "以下の日程でご都合はいかがでしょうか。\nご確認のほど、よろしくお願いいたします。", "メール"),
            new SnippetItem("s3", "署名", "山田 太郎\nデザイン事業部\ntaro@example.com", "共通")
        }, "OneDrive フォルダーを監視中", false);
    }
}

public sealed class LibraryRow
{
    public HistoryItem? History { get; }
    public SnippetItem? Snippet { get; }
    public string Id { get; }
    public string Title { get; }
    public string Preview { get; }
    public string Meta { get; }
    public string ShortMeta { get; }
    public string KindGlyph { get; }
    public string AccessKey { get; }
    public string ToolTipText { get; }
    public bool IsAvailable { get; } = true;
    internal DateTime DisplayDate { get; } = DateTime.Today;
    private const string AccessKeys = "1234567890abcdefghijklmnopqrstuvwxyz";
    public LibraryRow(HistoryItem item, int index = 0, bool unavailable = false)
    {
        History = item; Id = item.Id; IsAvailable = !unavailable;
        KindGlyph = unavailable ? "\uE74D" : item.IsImage ? "\uEB9F" : "\uE8C8";
        AccessKey = index >= 0 && index < AccessKeys.Length ? AccessKeys[index].ToString() : "";
        Title = item.IsImage ? "画像　" + item.Detail : item.Preview.Replace("\r", "").Replace('\n', ' ');
        if (string.IsNullOrWhiteSpace(Title)) Title = "（空白のテキスト）";
        if (unavailable) Title = "［削除済み］" + Title;
        Preview = item.Detail;
        var local = item.CreatedUtc.ToLocalTime();
        ShortMeta = local.Date == DateTimeOffset.Now.Date ? $"{local:HH:mm}" : $"{local:MM/dd HH:mm}";
        Meta = $"{item.DeviceName}　{local:MM/dd HH:mm:ss}";
        ToolTipText = unavailable ? "削除された履歴です。貼り付けできません。" : item.Detail.Length > 2000 ? item.Detail[..2000] + "…" : item.Detail;
    }
    public LibraryRow(SnippetItem item, int index = 0)
    {
        Snippet = item; Id = item.Id; Title = item.Title;
        AccessKey = index >= 0 && index < AccessKeys.Length ? AccessKeys[index].ToString() : "";
        Preview = item.Text.Replace("\r", "").Replace('\n', ' ');
        ShortMeta = string.IsNullOrWhiteSpace(item.Group) ? "共通" : item.Group;
        Meta = $"{ShortMeta}　{item.Text.Length:N0} 文字";
        KindGlyph = "\uE734";
        ToolTipText = item.Text.Length > 2000 ? item.Text[..2000] + "…" : item.Text;
    }
}
