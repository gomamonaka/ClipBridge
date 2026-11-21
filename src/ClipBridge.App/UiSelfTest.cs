using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipBridge.App.Services;

namespace ClipBridge.App;

/// <summary>Uses synthetic data and off-screen windows. Never touches the user's clipboard or settings.</summary>
internal static class UiSelfTest
{
    internal static async Task RunAsync(string resultPath)
    {
        var passed = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            passed.Add("PASS: " + name);
        }
        const string secret = "Synthetic-test-秘密-2026";
        string protectedValue = LocalSettings.ProtectPassphrase(secret);
        byte[] decrypted = WindowsSecret.Unprotect(Convert.FromBase64String(protectedValue));
        Check(Encoding.UTF8.GetString(decrypted) == secret, "Windows DPAPI Unicode round trip");
        CryptographicOperations.ZeroMemory(decrypted);
        Check(!Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue)).Contains(secret), "DPAPI ciphertext contains no test passphrase");
        byte[] tampered = Convert.FromBase64String(protectedValue); tampered[^4] ^= 0x40;
        bool rejected = false;
        try { WindowsSecret.Unprotect(tampered); } catch (CryptographicException) { rejected = true; }
        Check(rejected, "DPAPI rejects tampered secret");
        var gestureFailures = PopupInteractionService.RunGestureChecks();
        Check(gestureFailures.Count == 0, $"Double-Control recognizer: {PopupInteractionService.GestureCheckCount} isolated gesture checks ({string.Join(", ", gestureFailures)})");
        var pasteFailures = PopupInteractionService.RunPasteChecks();
        Check(pasteFailures.Count == 0 && PopupInteractionService.PasteCheckCount > 0,
            $"Paste readiness: {PopupInteractionService.PasteCheckCount} isolated key-state checks ({string.Join(", ", pasteFailures)})");
        var recoveryFailures = PopupInteractionService.RunRecoveryChecks();
        Check(recoveryFailures.Count == 0 && PopupInteractionService.RecoveryCheckCount > 0,
            $"Input recovery: {PopupInteractionService.RecoveryCheckCount} isolated stale-key and hook-health checks ({string.Join(", ", recoveryFailures)})");

        var window = new MainWindow { Left = -20000, Top = -20000, ShowInTaskbar = false, ForceClose = true, SuppressAutoHide = true };
        try
        {
            window.SetSyntheticDemo(); window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var list = (ListBox)window.FindName("ItemsList");
            var search = (TextBox)window.FindName("SearchInput");
            var copy = (Button)window.FindName("CopyButton");
            var groups = (ComboBox)window.FindName("GroupFilter");
            Check(list.Items.Count == 12, "Compact history renders twelve synthetic entries");
            Check(window.Width == 380 && !window.ShowInTaskbar && window.Topmost, "Compact floating popup properties");
            await SaveScreenshotAsync(window, resultPath, "ui-1.2-history.png");
            Check(((FrameworkElement)window.FindName("SearchBar")).Visibility == Visibility.Collapsed, "Search stays hidden until requested");
            HistoryItem? selectedHistory = null; window.CopyHistoryRequested += item => selectedHistory = item;
            SnippetItem? selectedSnippet = null; window.CopySnippetRequested += item => selectedSnippet = item;
            HistoryItem? copyOnlyHistory = null; window.CopyHistoryOnlyRequested += item => copyOnlyHistory = item;
            SnippetItem? copyOnlySnippet = null; window.CopySnippetOnlyRequested += item => copyOnlySnippet = item;

            Check(window.HandlePopupKey(Key.Down, ModifierKeys.None) && list.SelectedIndex == 1, "Down selects the next row");
            window.HandlePopupKey(Key.Enter, ModifierKeys.None);
            Check(selectedHistory?.Id == "demo-2", "Enter uses selected history through the host event");
            window.HandlePopupKey(Key.Up, ModifierKeys.None);
            Check(list.SelectedIndex == 0, "Up selects the previous row");
            window.HandlePopupKey(Key.D0, ModifierKeys.None);
            Check(selectedHistory?.Id == "demo-10", "0 access key selects the tenth item");
            window.HandlePopupKey(Key.A, ModifierKeys.None);
            Check(selectedHistory?.Id == "demo-11", "A access key selects the eleventh item");
            window.HandlePopupKey(Key.NumPad2, ModifierKeys.None);
            Check(selectedHistory?.Id == "demo-2", "Numeric keypad access keys work");
            window.HandlePopupKey(Key.C, ModifierKeys.Control);
            Check(copyOnlyHistory?.Id == "demo-2", "Ctrl+C emits copy-only history action");
            selectedHistory = null;
            Check(!window.HandlePopupKey(Key.D1, ModifierKeys.None, true) && selectedHistory is null, "Typing in search does not invoke numeric access keys");
            Check(!window.HandlePopupKey(Key.A, ModifierKeys.Control) && selectedHistory is null, "Control combinations do not invoke access keys");
            window.HandlePopupKey(Key.F, ModifierKeys.Control | ModifierKeys.Shift);
            Check(((FrameworkElement)window.FindName("SearchBar")).Visibility == Visibility.Visible, "Ctrl+Shift+F exposes search");
            search.Text = "example.com";
            Check(list.Items.Count == 1, "History search filters full text");
            ((MenuItem)window.FindName("UseSelectionMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(selectedHistory?.Id == "demo-2", "Use button emits selected history without touching clipboard");
            search.Text = "missing-synthetic-result";
            Check(list.Items.Count == 0 && !copy.IsEnabled, "Empty search disables use action");
            window.HandlePopupKey(Key.Right, ModifierKeys.None);
            Check(list.Items.Count == 2 && search.Text.Length == 0 && window.CurrentGroupName == "メール", "Right opens the first actual snippet group with its independent empty search");
            Check(((FrameworkElement)window.FindName("GroupBar")).Visibility == Visibility.Visible, "Snippet group selector is visible");
            groups.SelectedItem = "共通";
            Check(list.Items.Count == 1 && ((LibraryRow)list.Items[0]).Snippet?.Id == "s3", "Group selector filters snippets");
            window.HandlePopupKey(Key.Enter, ModifierKeys.None);
            Check(selectedSnippet?.Id == "s3", "Enter emits selected grouped snippet");
            window.HandlePopupKey(Key.C, ModifierKeys.Control);
            Check(copyOnlySnippet?.Id == "s3", "Ctrl+C emits copy-only snippet action");
            groups.SelectedItem = "メール";
            search.Text = "日程"; ((MenuItem)window.FindName("UseSelectionMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(selectedSnippet?.Id == "s2", "Snippet search and use event");
            int addRequested = 0; window.EditSnippetRequested += item => { if (item is null) addRequested++; };
            window.HandlePopupKey(Key.N, ModifierKeys.Control | ModifierKeys.Shift);
            ((Button)window.FindName("ToolsButton")).ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "定型文を登録…")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(addRequested == 2, "Ctrl+Shift+N and registration button open snippet registration");
            SnippetItem? deletedSnippet = null; window.DeleteSnippetRequested += item => deletedSnippet = item;
            window.HandlePopupKey(Key.Delete, ModifierKeys.None);
            Check(deletedSnippet?.Id == "s2", "Delete emits selected snippet deletion");
            window.HandlePopupKey(Key.Left, ModifierKeys.None);
            Check(list.Items.Count == 0 && search.Text == "missing-synthetic-result" && ((FrameworkElement)window.FindName("GroupBar")).Visibility == Visibility.Collapsed, "Left restores history's search independently of snippet search");
            search.Clear();
            Check(list.Items.Count == 12, "Clearing the restored history search reveals the complete first page");
            HistoryItem? snippetSeed = null; window.SaveHistoryAsSnippetRequested += item => snippetSeed = item;
            ((MenuItem)list.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(snippetSeed?.Id == "demo-1", "History context menu registers full-text snippet seed");
            list.SelectedIndex = 2; snippetSeed = null;
            ((MenuItem)list.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(snippetSeed is null, "Image history cannot be registered as a text snippet");
            HistoryItem? deletedHistory = null; window.DeleteHistoryRequested += item => deletedHistory = item;
            window.HandlePopupKey(Key.Delete, ModifierKeys.None);
            Check(deletedHistory?.Id == "demo-3", "Delete emits selected history deletion");
            bool pauseRequested = false; window.PauseRequested += () => pauseRequested = true;
            ((Button)window.FindName("PauseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(pauseRequested, "Pause action");
            bool settingsRequested = false; window.SettingsRequested += () => settingsRequested = true;
            ((Button)window.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(settingsRequested, "Settings action");
            window.SetInteractionMode(false, false);
            Check((string)copy.Content == "コピー ↵" && ((TextBlock)window.FindName("ShortcutHint")).Text == "Ctrl + Alt + V", "Interaction hints match disabled auto-paste and double-Control settings");
            var seedDialog = new SnippetDialog(new SnippetItem("", "登録用", "複数行\n本文", "仕事"), new[] { "メール", "仕事" });
            Check(seedDialog.Title == "定型文を登録" && ((TextBox)seedDialog.FindName("BodyInput")).Text == "複数行\n本文" && ((ComboBox)seedDialog.FindName("GroupInput")).Text == "仕事", "Snippet registration seed preserves full text and group");
            seedDialog.Close();
            // Exercise the real routed hover handlers with synthetic mouse events.
            window.SetInteractionMode(true, true);
            window.RenderGroups(new[] { new GroupItem("g-mail", "メール"), new GroupItem("g-empty", "空のグループ"), new GroupItem("g-common", "共通") });
            window.SuppressAutoHide = false;
            ((RadioButton)window.FindName("SnippetsNav")).RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
            window.SuppressAutoHide = true;
            Check(list.Items.Count == 1 && search.Text == "日程" && ((RadioButton)window.FindName("SnippetsNav")).IsChecked == true, "Mouse hover restores the snippets tab and its prior search");
            search.Clear();
            Check(list.Items.Count == 2, "Clearing snippet search restores only the selected actual group's first page");
            Check(groups.Items.Cast<string>().SequenceEqual(new[] { "メール", "空のグループ", "共通" }), "Group selector contains actual groups in shared order including empty groups");
            groups.SelectedItem = "空のグループ";
            Check(list.Items.Count == 0, "Empty groups are selectable");
            groups.SelectedItem = "メール";
            Check(list.Items.Cast<LibraryRow>().Select(x => x.Id).SequenceEqual(new[] { "s1", "s2" }), "Snippet order is preserved within its actual group without alphabetical resorting");
            await SaveScreenshotAsync(window, resultPath, "ui-1.2-snippets.png");
            window.SuppressAutoHide = false;
            ((RadioButton)window.FindName("HistoryNav")).RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
            window.SuppressAutoHide = true;
            Check(list.Items.Count == 12 && ((RadioButton)window.FindName("HistoryNav")).IsChecked == true, "Mouse hover returns to history");
            Check(((LibraryRow)list.SelectedItem).Id == "demo-3", "Hovering back to history preserves the previously selected row");
            list.SelectedIndex = 0;
            HistoryItem? editedHistory = null;
            window.EditHistoryRequested += item => editedHistory = item;
            window.HandlePopupKey(Key.F2, ModifierKeys.None);
            Check(editedHistory?.Id == "demo-1", "F2 opens text history editor through host event");
            list.SelectedIndex = 2; editedHistory = null;
            window.HandlePopupKey(Key.F2, ModifierKeys.None);
            Check(editedHistory is null, "Image history is excluded from text editing");
            var managementActions = new List<string>();
            window.ManageGroupsRequested += () => managementActions.Add("groups");
            window.ImportCsvRequested += () => managementActions.Add("import");
            window.ExportCsvRequested += () => managementActions.Add("export");
            window.ImportCliborRequested += () => managementActions.Add("clibor");
            window.BackupRequested += () => managementActions.Add("backup");
            window.RestoreRequested += () => managementActions.Add("restore");
            foreach (var menu in ((Button)window.FindName("ToolsButton")).ContextMenu.Items.OfType<MenuItem>().SkipWhile(item => !Equals(item.Header, "グループ管理・並び替え…")).Take(6))
                menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(managementActions.SequenceEqual(new[] { "groups", "import", "export", "clibor", "backup", "restore" }), "Management menu exposes groups CSV migration backup and restore actions");

            var sourceGroups = new[] { new GroupItem("group-work", "仕事"), new GroupItem("group-common", "共通") };
            var sourceSnippets = new[]
            {
                new SnippetItem("s1", "確認のお願い", "ご確認のほど、よろしくお願いいたします。", "仕事"),
                new SnippetItem("s2", "日程の確認", "以下の日程でご都合はいかがでしょうか。", "仕事"),
                new SnippetItem("s3", "メールのあいさつ", "いつもお世話になっております。", "共通")
            };
            var manager = new GroupManagerWindow(sourceGroups, sourceSnippets) { Left = -20000, Top = -20000, ShowInTaskbar = false };
            try
            {
                Check(manager.CreateGroup("空のグループ"), "Group manager creates an empty group");
                Check(manager.Groups.Count == 3 && manager.Snippets.Count == 3, "Creating group preserves all snippets");
                Check(!manager.CreateGroup("仕事"), "Group manager rejects duplicate names");
                Check(manager.RenameSelectedGroup("連絡"), "Group manager renames an empty group");
                manager.MoveGroup(-1);
                Check(manager.Groups[1].Name == "連絡", "Group manager reorders groups");
                var managedGroups = (ListBox)manager.FindName("GroupsList");
                managedGroups.SelectedIndex = 0;
                Check(manager.RenameSelectedGroup("業務"), "Group manager renames populated group");
                Check(manager.Snippets.Count(x => x.Group == "業務") == 2, "Renaming group updates every contained snippet");
                await SaveScreenshotAsync(manager, resultPath, "ui-1.2-groups.png");
                manager.MoveSnippet(1);
                Check(manager.Snippets[0].Id == "s2" && manager.Snippets[1].Id == "s1", "Group manager reorders snippets within group");
                Check(manager.MoveSelectedSnippetTo("連絡"), "Group manager moves selected snippet into another group");
                Check(manager.Snippets.Single(x => x.Id == "s1").Group == "連絡", "Moved snippet has correct destination group");
                Check(manager.DeleteSelectedGroup(), "Group manager deletes populated group");
                Check(manager.Snippets.Single(x => x.Id == "s2").Group == "共通", "Deleting group preserves snippets in common group");
                managedGroups.SelectedItem = manager.Groups.Single(x => x.Name == "共通");
                Check(!manager.DeleteSelectedGroup() && !manager.RenameSelectedGroup("変更不可"), "Default common group is protected from deletion and rename");
                Check(sourceSnippets[0].Group == "仕事" && sourceGroups[0].Name == "仕事", "Group manager edits a working copy before save");
            }
            finally { manager.Close(); }

            var snippetManager = new GroupManagerWindow(sourceGroups, sourceSnippets) { Left = -20000, Top = -20000, ShowInTaskbar = false };
            try
            {
                snippetManager.Show();
                await snippetManager.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var managedSnippets = (ListBox)snippetManager.FindName("SnippetsList");
                var editSnippetButton = (Button)snippetManager.FindName("EditSnippetButton");
                Check(editSnippetButton.IsEnabled, "Group manager exposes enabled edit action for selected snippet");
                var originalIds = snippetManager.Snippets.Select(x => x.Id).ToArray();
                Check(snippetManager.ApplySnippetEdit(sourceSnippets[0] with { Title = "確認のお願いを更新", Text = "編集した本文\n2 行目" }), "Group manager edits title and multiline body");
                var editedSnippet = snippetManager.Snippets.Single(x => x.Id == "s1");
                Check(editedSnippet.Title == "確認のお願いを更新" && editedSnippet.Text == "編集した本文\n2 行目", "Manager result contains edited title and full body");
                Check(snippetManager.Snippets.Select(x => x.Id).SequenceEqual(originalIds), "Text edit preserves snippet IDs and order");
                Check(sourceSnippets[0].Title == "確認のお願い" && sourceSnippets[0].Text == "ご確認のほど、よろしくお願いいたします。", "Management text edits leave source snapshot unchanged before save");
                Check(snippetManager.ApplySnippetEdit(editedSnippet with { Group = "共通" }), "Editing can move snippet to existing group");
                Check(snippetManager.Snippets.Where(x => x.Group == "共通").Select(x => x.Id).SequenceEqual(new[] { "s3", "s1" }), "Editing into existing group appends without reordering destination snippets");
                Check(((GroupItem)((ListBox)snippetManager.FindName("GroupsList")).SelectedItem).Name == "共通" && ((SnippetItem)managedSnippets.SelectedItem).Id == "s1", "Edited snippet remains selected after changing group");
                Check(snippetManager.ApplySnippetEdit(editedSnippet with { Group = "新しいグループ" }), "Editing creates newly entered group");
                Check(snippetManager.Groups.Any(x => x.Name == "新しいグループ") && snippetManager.Snippets.Single(x => x.Id == "s1").Group == "新しいグループ", "New group is included in management result");

                var beforeCancel = snippetManager.Snippets.ToArray();
                var groupsBeforeCancel = snippetManager.Groups.ToArray();
                int requests = 0;
                snippetManager.SnippetEditorOverride = (item, names) => { requests++; return null; };
                editSnippetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(requests == 1 && snippetManager.Snippets.SequenceEqual(beforeCancel) && snippetManager.Groups.SequenceEqual(groupsBeforeCancel), "Edit button opens editor and cancellation keeps working copy unchanged");

                snippetManager.SnippetEditorOverride = (item, names) => { requests++; return item with { Title = "F2 で編集" }; };
                managedSnippets.Focus();
                managedSnippets.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(snippetManager)!, Environment.TickCount, Key.F2) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Check(requests == 2 && snippetManager.Snippets.Single(x => x.Id == "s1").Title == "F2 で編集", "F2 routed key opens management snippet editor");

                snippetManager.SnippetEditorOverride = (item, names) => { requests++; return item with { Text = "ダブルクリックで編集した本文" }; };
                await snippetManager.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var managedRow = (ListBoxItem)managedSnippets.ItemContainerGenerator.ContainerFromIndex(0);
                managedSnippets.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = managedRow });
                Check(requests == 3 && snippetManager.Snippets.Single(x => x.Id == "s1").Text == "ダブルクリックで編集した本文", "Double click routed event opens management snippet editor");

                await SaveScreenshotAsync(snippetManager, resultPath, "ui-1.2.1-groups-edit.png");
                var beforeInvalid = snippetManager.Snippets.ToArray();
                Check(!snippetManager.ApplySnippetEdit(editedSnippet with { Text = "", Group = "作成しない" }) && snippetManager.Snippets.SequenceEqual(beforeInvalid) && snippetManager.Groups.All(x => x.Name != "作成しない"), "Invalid management edit leaves snippets and groups unchanged");
                Check(sourceGroups.Length == 2 && sourceSnippets[0].Group == "仕事" && sourceSnippets[0].Title == "確認のお願い", "Discarding management window cannot mutate original source snapshot");
            }
            finally { snippetManager.Close(); }

            var editor = new TextEditorWindow(new HistoryItem("h1", "編集サンプル", "  Alpha  \r\n\r\n  Beta　 ", DateTimeOffset.UtcNow, "テスト用端末", false, null)) { Left = -20000, Top = -20000, ShowInTaskbar = false };
            try
            {
                editor.Show();
                await editor.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var body = (TextBox)editor.FindName("BodyInput");
                string original = body.Text;
                editor.ApplyTransform("trim");
                Check(body.Text == "Alpha\r\n\r\nBeta", "Text editor applies whitespace trimming");
                body.Undo();
                Check(body.Text == original, "Text formatting supports Undo");
                Check(editor.ReplaceText("Alpha", "Example", false) && body.Text.Contains("Example"), "Literal replacement updates full text");
                string previous = body.Text;
                Check(!editor.ReplaceText("[", "bad", true) && body.Text == previous, "Invalid regex leaves full text unchanged");
                Check(editor.ReplaceText("(Example)", "$1 sample", true) && body.Text.Contains("Example sample"), "Regex replacement supports capture groups");
                body.Undo();
                Check(body.Text == previous, "Regex replacement supports Undo");
                Check(!editor.ReplaceText("", "bad", false) && body.Text == previous, "Empty replacement search is safely rejected");
                ((Button)editor.FindName("ResetButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(body.Text == original, "Restore original button restores text before editing");
                editor.ApplyTransform("removeBlank");
                Check(!body.Text.Contains("\r\n\r\n"), "Remove blank lines operates on editor body");
                editor.ApplyTransform("join");
                Check(body.Text == "Alpha Beta", "Join lines inserts a space");
                editor.ApplyTransform("full");
                Check(body.Text == "Ａｌｐｈａ　Ｂｅｔａ", "Full width formatting handles ASCII letters and spaces");
                editor.ApplyTransform("half");
                Check(body.Text == "Alpha Beta", "Half width formatting returns ASCII letters and spaces");
                editor.ApplyTransform("upper");
                Check(body.Text == "ALPHA BETA", "Uppercase formatting uses invariant case");
                editor.ApplyTransform("lower");
                Check(body.Text == "alpha beta", "Lowercase formatting uses invariant case");
                ((Button)editor.FindName("ResetButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                editor.ApplyTransform("trim");
                await SaveScreenshotAsync(editor, resultPath, "ui-1.2-editor.png");
            }
            finally { editor.Close(); }

            var many = Enumerable.Range(0, 37).Select(i => new HistoryItem("many-" + i, "合成テキスト " + i, "合成テキスト " + i, DateTimeOffset.UtcNow.AddMinutes(-i), "合成端末", false, null)).ToArray();
            window.Hide();
            window.ConfigurePaging(36, false);
            window.FitToWorkArea(2000);
            window.Render(many, [], "合成状態", false);
            window.ShowHistory();
            window.HandlePopupKey(Key.Z, ModifierKeys.None);
            Check(selectedHistory?.Id == "many-35" && list.Items.Count == 36 && ((LibraryRow)list.Items[35]).AccessKey == "z", "A 36-row page assigns every access key through Z without an inaccessible 37th row");
            window.HandlePopupKey(Key.PageDown, ModifierKeys.None);
            window.HandlePopupKey(Key.D1, ModifierKeys.None);
            Check(list.Items.Count == 1 && selectedHistory?.Id == "many-36" && ((LibraryRow)list.Items[0]).AccessKey == "1", "The next page restarts access keys at 1 for the 37th history item");
            window.HandlePopupKey(Key.Escape, ModifierKeys.None);
            Check(!window.IsVisible, "Escape hides popup");
            int openings = 0; window.OpeningRequested += () => openings++;
            window.ShowHistory(); window.ShowHistory();
            Check(openings == 2 && window.IsVisible && list.IsKeyboardFocused && search.Text.Length == 0, "Every invocation requests current placement and keeps the visible picker open");
            window.Hide();
            window.Render([], [], "一時停止中", true);
            window.ShowHistory();
            Check(!copy.IsEnabled && list.Items.Count == 0, "Empty library safe state");
        }
        finally { window.Close(); }
        await RunPresentationChecksAsync(Check);
        await RunHeaderStabilityChecksAsync(Check);
        await RunSurfaceDesignChecksAsync(Check);
        await RunRowReuseChecksAsync(Check);
        await RunPagingSettingsChecksAsync(Check);
        await RunPagingChecksAsync(Check, resultPath);
        await RunActualGroupChecksAsync(Check);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
        await File.WriteAllLinesAsync(resultPath, passed.Append($"{passed.Count} checks passed (includes {PopupInteractionService.GestureCheckCount} isolated native gesture cases, {PopupInteractionService.PasteCheckCount} isolated paste-readiness cases and {PopupInteractionService.RecoveryCheckCount} isolated input-recovery cases); no real clipboard, input injection, OneDrive or persisted settings accessed."));
    }

    private static async Task RunPresentationChecksAsync(Action<bool, string> check)
    {
        var window = new MainWindow
        {
            Left = -20000, Top = -20000, ShowActivated = false,
            ShowInTaskbar = false, ForceClose = true
        };
        try
        {
            window.SetSyntheticDemo();
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(window.IsVisible && !window.IsActive, "Dismissal regression starts with a visible picker that was never activated");
            window.SuppressAutoHide = true;
            window.SuppressAutoHide = false; // Queues an inactive-dismissal check for this presentation.
            window.Hide();
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(window.IsVisible && !window.IsActive,
                "Queued inactive dismissal from an older presentation cannot close a reopened inactive picker");
            window.DismissFromOutsideClick();
            check(!window.IsVisible, "Outside click hides an initially inactive picker without a Deactivated event");
            window.DismissFromOutsideClick();
            check(!window.IsVisible, "Repeated outside notifications safely leave a hidden picker hidden");

            window.SuppressAutoHide = true;
            int openings = 0;
            window.OpeningRequested += () => { openings++; window.Left = -20000 - openings * 20; };
            window.ShowHistory();
            double originalLeft = window.Left;
            window.ShowHistory();
            check(openings == 2 && window.Left == originalLeft - 20 && window.IsVisible,
                "Visible re-invocation applies a fresh position instead of toggling the picker off");
            window.DismissFromOutsideClick();
            check(window.IsVisible, "Outside click does not dismiss a picker protected by an editor or management dialog");

            var menu = (ContextMenu)window.FindName("ToolsMenu");
            var groups = (ComboBox)window.FindName("GroupFilter");
            menu.PlacementTarget = (Button)window.FindName("ToolsButton");
            menu.IsOpen = true;
            window.SuppressAutoHide = false;
            window.DismissFromOutsideClick();
            check(!window.IsVisible && !menu.IsOpen && !groups.IsDropDownOpen,
                "An outside click also closes the picker and its open management menu");

            window.SuppressAutoHide = true;
            window.ShowSnippets();
            groups.IsDropDownOpen = true;
            window.SuppressAutoHide = false;
            window.DismissFromOutsideClick();
            check(!window.IsVisible && !groups.IsDropDownOpen,
                "An outside click also closes a group dropdown and the picker");

        }
        finally { window.Close(); }
    }

    private static async Task RunHeaderStabilityChecksAsync(Action<bool, string> check)
    {
        var window = new MainWindow { Left = -20000, Top = -20000, ShowInTaskbar = false, ForceClose = true, SuppressAutoHide = true };
        try
        {
            window.SetSyntheticDemo(); window.ShowHistory();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var list = (ListBox)window.FindName("ItemsList");
            var historyTab = (RadioButton)window.FindName("HistoryNav");
            var snippetsTab = (RadioButton)window.FindName("SnippetsNav");
            double listTop = list.TranslatePoint(new Point(), window).Y;
            double tabsTop = historyTab.TranslatePoint(new Point(), window).Y;
            bool stable = true;
            for (int i = 0; i < 3; i++)
            {
                window.ShowSnippets();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                stable &= Math.Abs(list.TranslatePoint(new Point(), window).Y - listTop) < 0.5 && Math.Abs(snippetsTab.TranslatePoint(new Point(), window).Y - tabsTop) < 0.5;
                window.ShowHistory();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                stable &= Math.Abs(list.TranslatePoint(new Point(), window).Y - listTop) < 0.5;
            }
            check(stable, "Switching sections with different item counts keeps the tabs and list at the same vertical offset");
            window.ShowSnippets();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var group = (ComboBox)window.FindName("GroupFilter");
            check(group.IsVisible && group.ActualHeight <= 20.5 && group.TranslatePoint(new Point(), window).Y < listTop,
                "Snippet group selection occupies the existing metadata row without adding header height");
        }
        finally { window.Close(); }
    }

    private static async Task RunSurfaceDesignChecksAsync(Action<bool, string> check)
    {
        var window = new MainWindow { Left = -20000, Top = -20000, ShowInTaskbar = false, ForceClose = true, SuppressAutoHide = true };
        try
        {
            window.SetSyntheticDemo(); window.ShowHistory();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var surface = (Border)window.FindName("PopupSurface");
            var list = (ListBox)window.FindName("ItemsList");
            var bitmap = Raster();
            check(ClearCorners(bitmap), "All four window corners are transparent, including the window chrome");
            Point sample = list.TranslatePoint(new Point(list.ActualWidth - 30, 32), window);
            var background = Pixel(bitmap, (int)sample.X, (int)sample.Y);
            check(background[3] >= 230 && background[3] <= 245 && background[1] > background[2],
                "Unselected rows retain a gently translucent mint background");
            check(window.Opacity == 1 && surface.Opacity == 1 && list.Opacity == 1,
                "Text and controls are not faded by whole-window opacity");
            Point selectedSample = list.TranslatePoint(new Point(list.ActualWidth - 30, 12), window);
            var selected = Pixel(bitmap, (int)selectedSample.X, (int)selectedSample.Y);
            check(selected[2] > selected[1] && selected[1] > selected[0],
                "The selected row is distinguished by a warm apricot surface");

            window.Width += 54;
            window.ShowSnippets();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var outline = (RectangleGeometry)surface.Clip;
            check(Math.Abs(outline.Rect.Width - surface.ActualWidth) < 0.5 && Math.Abs(outline.Rect.Height - surface.ActualHeight) < 0.5 && ClearCorners(Raster()),
                "Rounded clipping follows width and section-height changes without square corner leaks");
            var group = (ComboBox)window.FindName("GroupFilter");
            group.Focus();
            group.IsDropDownOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(group.IsKeyboardFocusWithin && group.IsDropDownOpen && group.SelectedItem is not null,
                "The integrated group selector keeps keyboard focus and opens its dropdown");
            group.IsDropDownOpen = false;

            RenderTargetBitmap Raster()
            {
                window.UpdateLayout();
                var result = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                result.Render(window);
                return result;
            }
        }
        finally { window.Close(); }

        static byte[] Pixel(BitmapSource bitmap, int x, int y)
        {
            var result = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), result, 4, 0);
            return result;
        }
        static bool ClearCorners(BitmapSource bitmap) =>
            Pixel(bitmap, 0, 0)[3] == 0 && Pixel(bitmap, bitmap.PixelWidth - 1, 0)[3] == 0 &&
            Pixel(bitmap, 0, bitmap.PixelHeight - 1)[3] == 0 && Pixel(bitmap, bitmap.PixelWidth - 1, bitmap.PixelHeight - 1)[3] == 0;
    }

    private static async Task RunRowReuseChecksAsync(Action<bool, string> check)
    {
        var window = new MainWindow { Left = -20000, Top = -20000, ShowInTaskbar = false, ForceClose = true, SuppressAutoHide = true };
        try
        {
            var now = DateTimeOffset.UtcNow;
            var history = Enumerable.Range(0, 30).Select(i => new HistoryItem("reuse-" + i, "合成履歴 " + i, "合成本文 " + i, now.AddSeconds(-i), "合成端末", false, null)).ToArray();
            var snippets = new[] { new SnippetItem("reuse-snippet", "合成定型文", "合成本文", "合成グループ") };
            window.Render(history, snippets, "行の再利用の合成検証", false);
            window.FitToWorkArea(480); window.ShowHistory();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var list = (ListBox)window.FindName("ItemsList");
            list.SelectedIndex = 1;
            object selected = list.SelectedItem;
            object? container = list.ItemContainerGenerator.ContainerFromIndex(1);
            int changes = 0;
            ((System.Collections.Specialized.INotifyCollectionChanged)list.Items).CollectionChanged += (_, _) => changes++;
            window.ShowHistory(); window.FitToWorkArea(480);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(changes == 0 && container is not null && ReferenceEquals(container, list.ItemContainerGenerator.ContainerFromIndex(1)) && ReferenceEquals(selected, list.SelectedItem),
                "Repositioning an unchanged history page retains its rows, item containers and selection");
            window.Hide(); window.ShowHistory();
            check(changes == 0 && ((LibraryRow)list.SelectedItem).Id == "reuse-0",
                "A fresh session resets selection without rebuilding the unchanged history page");
            history[0] = history[0] with { Preview = "更新した合成履歴", Detail = "更新した合成本文" };
            window.Render(history, snippets, "同じIDの更新", false);
            check(changes > 0 && ((LibraryRow)list.Items[0]).Title == history[0].Preview && ((LibraryRow)list.Items[0]).History?.Detail == history[0].Detail,
                "Reusing rows never hides an edited history entry with the same ID");
            window.ShowSnippets();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            object snippetRow = list.Items[0];
            container = list.ItemContainerGenerator.ContainerFromIndex(0); changes = 0;
            window.ShowSnippets(); window.FitToWorkArea(480);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(changes == 0 && container is not null && ReferenceEquals(snippetRow, list.Items[0]) && ReferenceEquals(container, list.ItemContainerGenerator.ContainerFromIndex(0)),
                "Repositioning an unchanged snippet page retains its rows and item containers");
            snippets[0] = snippets[0] with { Title = "更新した合成定型文", Text = "更新した合成定型文の本文" };
            window.Render(history, snippets, "同じIDの定型文更新", false);
            check(changes > 0 && ((LibraryRow)list.Items[0]).Title == snippets[0].Title && ((LibraryRow)list.Items[0]).Snippet?.Text == snippets[0].Text,
                "Reusing rows never hides an edited snippet with the same ID");
        }
        finally { window.Close(); }
    }

    private static async Task RunPagingSettingsChecksAsync(Action<bool, string> check)
    {
        var defaults = new LocalSettings();
        check(defaults.PopupPageSize == 25 && !defaults.LoopPages, "Paging settings default to 25 entries and disabled looping without reading settings files");
        var legacy = JsonSerializer.Deserialize<LocalSettings>("{\"DeviceId\":\"synthetic-device\",\"DeviceName\":\"Synthetic device\",\"DoubleControlEnabled\":false}")!;
        check(legacy.PopupPageSize == 25 && !legacy.LoopPages && !legacy.DoubleControlEnabled && legacy.DeviceName == "Synthetic device", "Legacy settings JSON gains paging defaults while preserving existing options");
        var custom = defaults with { PopupPageSize = 36, LoopPages = true, DeviceId = "synthetic-device", DeviceName = "Synthetic device" };
        var roundTrip = JsonSerializer.Deserialize<LocalSettings>(JsonSerializer.Serialize(custom))!;
        check(roundTrip.PopupPageSize == 36 && roundTrip.LoopPages && roundTrip.DeviceId == custom.DeviceId, "Customized page size and loop setting survive an in-memory JSON round trip");
        var dialog = new PagingSettingsWindow(25, false) { Left = -20000, Top = -20000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            dialog.Show();
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var input = (TextBox)dialog.FindName("PageSizeInput"); var loop = (CheckBox)dialog.FindName("LoopInput");
            check(input.Text == "25" && loop.IsChecked == false, "Paging settings dialog displays the supplied page size and loop preference");
            var save = VisualDescendants<Button>(dialog).Single(x => x.Content as string == "設定を保存");
            foreach (var invalid in new[] { "", "0", "37", "2.5", "abc", " 5", "-1" })
            {
                input.Text = invalid;
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(dialog.IsVisible && !string.IsNullOrEmpty(((TextBlock)dialog.FindName("ErrorText")).Text) && dialog.PageSize == 0,
                    "Paging settings routed Save rejects invalid value " + (invalid.Length == 0 ? "[empty]" : invalid));
            }
        }
        finally { dialog.Close(); }
        foreach (var size in new[] { 1, 36 })
        {
            var valid = new PagingSettingsWindow(25, false) { Left = -20000, Top = -20000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            Exception? callbackFailure = null;
            _ = valid.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                try
                {
                    ((TextBox)valid.FindName("PageSizeInput")).Text = size.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    ((CheckBox)valid.FindName("LoopInput")).IsChecked = true;
                    VisualDescendants<Button>(valid).Single(x => x.Content as string == "設定を保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { callbackFailure = ex; valid.Close(); }
            }));
            bool? saved = valid.ShowDialog();
            if (callbackFailure is not null) throw new InvalidOperationException("Paging settings modal verification failed", callbackFailure);
            check(saved == true && valid.PageSize == size && valid.LoopPages, $"Paging settings routed Save accepts boundary {size} and returns the selected loop preference");
        }
        var clamped = new PagingSettingsWindow(99, true);
        try { check(((TextBox)clamped.FindName("PageSizeInput")).Text == "36" && ((CheckBox)clamped.FindName("LoopInput")).IsChecked == true, "Paging settings dialog safely bounds an out-of-range stored size"); }
        finally { clamped.Close(); }
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static async Task RunPagingChecksAsync(Action<bool, string> check, string resultPath)
    {
        var window = new MainWindow { Left = -20000, Top = -20000, ShowInTaskbar = false, ForceClose = true, SuppressAutoHide = true };
        try
        {
            var now = DateTimeOffset.UtcNow;
            HistoryItem[] History(string prefix, int count) => Enumerable.Range(0, count).Select(index =>
                new HistoryItem(prefix + index, $"履歴 {index + 1:D3}：作業メモと連絡事項", $"履歴 {index + 1:D3}\nこの項目はページ切り替え検証用の合成データです。", now.AddMinutes(-index), "合成テスト端末", false, null)).ToArray();
            var history = History("page-", 57);
            var snippets = Enumerable.Range(0, 55).Select(index => new SnippetItem("a-" + index, $"定型文 {index + 1:D2}：業務連絡", $"定型文 {index + 1:D2}\nご確認のほど、よろしくお願いいたします。", "仕事", index))
                .Concat(Enumerable.Range(0, 3).Select(index => new SnippetItem("b-" + index, "メールの定型文 " + index, "メール本文 " + index, "メール", 55 + index)))
                .Concat(new[] { new SnippetItem("c-0", "署名", "山田 太郎\n企画部\ntest@example.com", "共通", 58) }).ToArray();
            window.Render(history, snippets, "ページ切り替え検証用の合成データ", false);
            window.RenderGroups(new[] { new GroupItem("group-a", "仕事"), new GroupItem("group-empty", "空のグループ"), new GroupItem("group-b", "メール"), new GroupItem("group-c", "共通") });
            window.FitToWorkArea(1200);
            window.ShowHistory();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var list = (ListBox)window.FindName("ItemsList");
            var search = (TextBox)window.FindName("SearchInput");
            var groups = (ComboBox)window.FindName("GroupFilter");
            var copy = (Button)window.FindName("CopyButton");
            var previous = (Button)window.FindName("PreviousPageButton");
            var next = (Button)window.FindName("NextPageButton");
            HistoryItem? copied = null; window.CopyHistoryRequested += item => copied = item;
            SnippetItem? copiedSnippet = null; window.CopySnippetRequested += item => copiedSnippet = item;
            HistoryItem? copiedOnly = null; window.CopyHistoryOnlyRequested += item => copiedOnly = item;
            HistoryItem? deleted = null; window.DeleteHistoryRequested += item => deleted = item;
            HistoryItem? edited = null; window.EditHistoryRequested += item => edited = item;
            string[] Ids() => list.Items.Cast<LibraryRow>().Select(x => x.Id).ToArray();
            check(window.RequestedPageSize == 25 && window.EffectivePageSize == 25 && !window.PagingLoop, "Paging defaults to 25 rows and stops at the final page on a large work area");
            check(window.PageCount == 3 && window.CurrentPageIndex == 0 && window.FilteredItemCount == 57 && list.Items.Count == 25, "History pagination reports complete count and bounded first page");
            check(Ids().SequenceEqual(Enumerable.Range(0, 25).Select(x => "page-" + x)), "The first history page contains exactly the newest 25 entries");
            check(!previous.IsEnabled && next.IsEnabled && !string.IsNullOrWhiteSpace(((TextBlock)window.FindName("PageIndicator")).Text), "Visible page controls describe the first page and disable backward navigation");
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(window.CurrentPageIndex == 1 && Ids()[0] == "page-25" && previous.IsEnabled, "Next-page button routes to the next bounded history page");
            window.HandlePopupKey(Key.D1, ModifierKeys.None);
            check(copied?.Id == "page-25" && ((LibraryRow)list.Items[0]).AccessKey == "1", "Access key 1 always selects the first item of the current page");
            window.HandlePopupKey(Key.O, ModifierKeys.None);
            check(copied?.Id == "page-49" && ((LibraryRow)list.Items[24]).AccessKey == "o", "A 25-row page exposes its final item through access key O");
            copied = null; window.HandlePopupKey(Key.P, ModifierKeys.None);
            check(copied is null, "An access key beyond the current page cannot select a hidden history item");
            list.Focus();
            var pageDown = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.PageDown) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            list.RaiseEvent(pageDown);
            check(pageDown.Handled && window.CurrentPageIndex == 2 && Ids().SequenceEqual(Enumerable.Range(50, 7).Select(x => "page-" + x)), "Routed PageDown reaches the short final history page");
            check(!next.IsEnabled && !window.AdvancePage(1) && window.CurrentPageIndex == 2, "Non-looping pagination cannot advance beyond the last history page");
            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(window.CurrentPageIndex == 1 && list.Items.Count == 25, "Previous-page button returns from the final short page");

            // Both tabs retain their own query, page, selection and snippet group.
            window.HandlePopupKey(Key.F, ModifierKeys.Control);
            search.Text = "履歴";
            window.AdvancePage(1); list.SelectedIndex = 4;
            string historySelection = ((LibraryRow)list.SelectedItem).Id;
            window.ShowSnippets();
            check(window.CurrentGroupName == "仕事" && window.FilteredItemCount == 55 && list.Items.Count == 25 && groups.Items.Cast<string>().SequenceEqual(new[] { "仕事", "空のグループ", "メール", "共通" }),
                "The snippet tab initially displays the first actual group without a synthetic All entry");
            groups.SelectedItem = "仕事";
            search.Text = "定型文";
            window.AdvancePage(1); list.SelectedIndex = 2;
            string snippetSelection = ((LibraryRow)list.SelectedItem).Id;
            window.HandlePopupKey(Key.Left, ModifierKeys.None);
            check(!window.IsShowingSnippets && search.Text == "履歴" && window.CurrentPageIndex == 1 && ((LibraryRow)list.SelectedItem).Id == historySelection, "Returning to history restores its query, page and selected ID");
            window.HandlePopupKey(Key.Right, ModifierKeys.None);
            check(window.IsShowingSnippets && groups.SelectedItem as string == "仕事" && search.Text == "定型文" && window.CurrentPageIndex == 1 && ((LibraryRow)list.SelectedItem).Id == snippetSelection, "Returning to snippets restores its independent group, query, page and selection");
            search.Clear();
            check(window.CurrentPageIndex == 0 && window.PageCount == 3 && window.FilteredItemCount == 55, "Clearing a snippet query resets its group to the first page");
            check(window.AdvancePage(1) && window.CurrentGroupName == "仕事" && window.CurrentPageIndex == 1 && Ids()[0] == "a-25", "A large snippet group advances through its own internal pages");
            check(window.AdvancePage(1) && window.CurrentGroupName == "仕事" && window.CurrentPageIndex == 2 && list.Items.Count == 5 && Ids()[0] == "a-50", "The last internal snippet page preserves the final five items");
            check(window.AdvancePage(1) && window.CurrentGroupName == "空のグループ" && list.Items.Count == 0, "Snippet navigation visits an empty group after the populated group's final page");
            check(window.AdvancePage(1) && window.CurrentGroupName == "メール" && Ids()[0] == "b-0", "Snippet navigation continues from an empty group to the next shared group");
            check(window.AdvancePage(1) && window.CurrentGroupName == "共通" && Ids().SequenceEqual(new[] { "c-0" }), "Snippet navigation reaches the final shared group");
            window.HandlePopupKey(Key.D1, ModifierKeys.None);
            check(copiedSnippet?.Id == "c-0", "A page-local access key selects the final actual group's snippet");
            check(!window.AdvancePage(1) && window.CurrentGroupName == "共通", "Non-looping snippet navigation stops at the final group");
            window.ConfigurePaging(25, true);
            check(window.AdvancePage(1) && window.CurrentGroupName == "仕事" && window.CurrentPageIndex == 0, "Looping snippet navigation wraps to the first actual group");
            check(window.AdvancePage(-1) && window.CurrentGroupName == "共通", "Reverse looping navigation wraps to the last actual group");
            groups.SelectedItem = "空のグループ";
            check(window.AdvancePage(-1) && window.CurrentGroupName == "仕事" && window.CurrentPageIndex == 2 && Ids()[0] == "a-50", "Backward group navigation enters the preceding group's final internal page");
            window.ConfigurePaging(25, false);
            while (window.CurrentPageIndex > 0) window.AdvancePage(-1);
            check(!window.AdvancePage(-1) && window.CurrentGroupName == "仕事" && window.CurrentPageIndex == 0 && !previous.IsEnabled,
                "Non-looping snippet navigation stops before the first actual group");
            check(!groups.Items.Cast<string>().Contains("すべて") && window.FilteredItemCount == 55,
                "Actual-group pagination never inserts or displays a combined synthetic group");
            groups.SelectedItem = "仕事"; search.Text = "定型文";
            window.ConfigurePaging(25, false); window.AdvancePage(1); window.AdvancePage(1);
            check(!window.AdvancePage(1) && window.CurrentGroupName == "仕事" && window.CurrentPageIndex == 2 && window.FilteredItemCount == 55, "An active search pages only its selected group and stops at its filtered boundary");
            window.ConfigurePaging(25, true);
            check(window.AdvancePage(1) && window.CurrentGroupName == "仕事" && window.CurrentPageIndex == 0, "Looping a search wraps within its filtered group instead of visiting another group");

            // Use a longer history for wheel accumulation and one-transition-per-event checks.
            search.Clear(); window.Hide();
            var wheelHistory = History("wheel-", 137);
            window.ConfigurePaging(25, false); window.Render(wheelHistory, snippets, "ホイール検証用の合成データ", false); window.ShowHistory(); search.Clear();
            check(!window.HandleWheel(-30, 1000) && !window.HandleWheel(-30, 1050) && !window.HandleWheel(-30, 1100) && window.CurrentPageIndex == 0, "High-resolution wheel deltas accumulate without premature page movement");
            check(window.HandleWheel(-30, 1150) && window.CurrentPageIndex == 1, "Accumulated 120 wheel units advance exactly one page");
            check(!window.HandleWheel(-120, 1189) && window.CurrentPageIndex == 1, "A duplicate standard wheel notch inside 40ms does not skip a page");
            check(window.HandleWheel(-120, 1190) && window.CurrentPageIndex == 2, "A standard wheel notch is accepted at its responsive 40ms boundary");
            check(!window.HandleWheel(-30, 1220) && window.HandleWheel(-120, 1250) && window.CurrentPageIndex == 3, "High-resolution burst residuals are ignored while separate standard notches remain responsive");
            check(!window.HandleWheel(-720, 1270) && window.HandleWheel(-720, 1330) && window.CurrentPageIndex == 4, "One large wheel event advances at most one page after its 80ms boundary");
            check(!window.HandleWheel(-720, 1350) && window.HandleWheel(-720, 1410) && window.CurrentPageIndex == 5, "Rapid momentum events are debounced without accumulating extra page jumps");
            check(window.HandleWheel(120, 1430) && window.CurrentPageIndex == 4, "Reversing wheel direction promptly returns to the preceding page");
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = list };
            list.RaiseEvent(wheel);
            check(wheel.Handled && window.CurrentPageIndex == 5 && Ids()[0] == "wheel-125", "Routed mouse wheel changes pages and consumes the native list scroll event");

            // Keep visible history IDs stable, but prevent all actions on a deleted placeholder.
            window.RefreshHistorySnapshot(); list.SelectedIndex = 3;
            var arrivals = Enumerable.Range(0, 3).Select(index => new HistoryItem("arrival-" + index, "新しい共有履歴 " + (index + 1), "別の端末から届いた合成テキスト " + index,
                now.AddMinutes(1).AddSeconds(-index), "合成の別端末", false, null)).ToArray();
            var updated = arrivals.Concat(wheelHistory.Where(x => x.Id != "wheel-5").Select(x => x.Id == "wheel-3" ? x with { Detail = "更新された合成本文" } : x)).ToArray();
            window.Render(updated, snippets, "新しい履歴を受信した合成状態", false);
            check(window.PendingHistoryCount == 3 && Ids()[0] == "wheel-0" && Ids()[5] == "wheel-5" && !Ids().Any(x => x.StartsWith("arrival-", StringComparison.Ordinal)), "New arrivals are queued while visible history positions remain fixed");
            check(((LibraryRow)list.SelectedItem).Id == "wheel-3" && ((LibraryRow)list.Items[3]).History?.Detail == "更新された合成本文", "A visible history update preserves selected ID and refreshes its full text");
            list.SelectedIndex = 5;
            check(!((LibraryRow)list.Items[5]).IsAvailable && !copy.IsEnabled, "A deleted visible history stays as an unavailable placeholder with use disabled");
            copied = null; copiedOnly = null; deleted = null; edited = null;
            window.HandlePopupKey(Key.Enter, ModifierKeys.None); window.HandlePopupKey(Key.C, ModifierKeys.Control);
            window.HandlePopupKey(Key.F2, ModifierKeys.None); window.HandlePopupKey(Key.Delete, ModifierKeys.None); window.HandlePopupKey(Key.D6, ModifierKeys.None);
            check(copied is null && copiedOnly is null && deleted is null && edited is null, "Deleted history placeholders cannot be copied, edited, deleted again or invoked by an access key");
            window.HandlePopupKey(Key.D4, ModifierKeys.None);
            check(copied?.Id == "wheel-3" && copied.Detail == "更新された合成本文", "A still-live frozen row uses its latest full text through the unchanged access key");
            var pending = (Button)window.FindName("PendingHistoryButton");
            check(pending.Visibility == Visibility.Visible && (pending.Content?.ToString()?.Contains("3", StringComparison.Ordinal) ?? false), "A visible new-history button reports the queued arrival count");
            window.AdvancePage(1);
            window.SetInteractionMode(true, true);
            await SaveScreenshotAsync(window, resultPath, "ui-1.3-history-paged.png");
            pending.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(window.PendingHistoryCount == 0 && window.CurrentPageIndex == 0 && Ids()[0] == "arrival-0" && window.FilteredItemCount == 139 && !Ids().Contains("wheel-5"), "The new-history button explicitly adopts arrivals and removes deleted placeholders");
            window.Hide();
            var newest = new HistoryItem("hidden-arrival", "閉じている間の新着", "合成の最新履歴", now.AddMinutes(2), "合成端末", false, null);
            window.Render(new[] { newest }.Concat(updated), snippets, "非表示中の受信", false); window.ShowHistory();
            check(window.PendingHistoryCount == 0 && window.CurrentPageIndex == 0 && Ids()[0] == newest.Id, "Reopening a hidden popup starts with the latest snapshot and first page");
            search.Text = "作業メモ"; window.AdvancePage(1); window.Hide(); window.ShowHistory();
            check(search.Text.Length == 0 && window.CurrentPageIndex == 0 && window.FilteredItemCount == 140 && Ids()[0] == newest.Id && ((FrameworkElement)window.FindName("SearchBar")).Visibility == Visibility.Collapsed,
                "A fresh popup session clears the old query and opens the latest unfiltered first page");
            search.Clear();
            search.Text = "履歴"; window.Hide(); window.ShowSnippets();
            check(window.IsShowingSnippets && search.Text.Length == 0 && window.CurrentGroupName == "仕事" && window.CurrentPageIndex == 0 && window.FilteredItemCount == 55, "Opening snippets after hiding searched history starts at the first actual group");
            window.HandlePopupKey(Key.Left, ModifierKeys.None);
            check(search.Text.Length == 0 && window.FilteredItemCount == 140, "Returning to history cannot restore a query leaked from the previous popup session");
            window.ShowSnippets(); groups.SelectedItem = "仕事"; search.Text = "定型文"; window.Hide(); window.ShowHistory();
            check(!window.IsShowingSnippets && search.Text.Length == 0 && window.CurrentPageIndex == 0 && window.FilteredItemCount == 140, "Opening history after hiding searched snippets starts with fresh unfiltered history");
            window.HandlePopupKey(Key.Right, ModifierKeys.None);
            check(search.Text.Length == 0 && window.CurrentGroupName == "仕事" && window.FilteredItemCount == 55, "Returning to snippets starts at the first actual group without the prior popup's query");

            // The actual WPF layout must fit every displayed row, even with search and group chrome.
            window.ShowHistory();
            window.ConfigurePaging(36, false); window.FitToWorkArea(480);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(window.Height <= 480.5 && window.RequestedPageSize == 36 && window.EffectivePageSize >= 1 && window.EffectivePageSize < 36,
                "A short work area reduces the effective page size without changing the requested size");
            check(list.Items.Count <= window.EffectivePageSize && AllRowsFit(list), "Every history row is generated and fully visible on a short work area");
            window.HandlePopupKey(Key.F, ModifierKeys.Control); window.ShowSnippets(); groups.SelectedItem = "仕事"; window.HandlePopupKey(Key.F, ModifierKeys.Control);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(window.Height <= 480.5 && list.Items.Count <= window.EffectivePageSize && AllRowsFit(list), "Snippet groups and visible search controls do not push page rows outside the work area");
            window.ConfigurePaging(1, false); window.FitToWorkArea(1200);
            check(window.EffectivePageSize == 1 && list.Items.Count == 1 && window.PageCount == 55, "A configured one-row page retains access to every snippet through pagination");
            // Screenshot layout uses the real monitor budget: Windows can cap an off-screen
            // window below a synthetic 1200-DIP work area on a small CI desktop.
            double screenshotMaxHeight = Math.Max(1, SystemParameters.WorkArea.Height - 16);
            window.ConfigurePaging(25, false); window.FitToWorkArea(screenshotMaxHeight); search.Clear(); groups.SelectedItem = "仕事";
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (window.CurrentPageIndex != 0) while (window.CurrentPageIndex > 0) window.AdvancePage(-1);
            window.AdvancePage(1); list.SelectedIndex = Math.Min(4, list.Items.Count - 1);
            await SaveScreenshotAsync(window, resultPath, "ui-1.3-snippets-paged.png");
            int screenshotPageSize = window.EffectivePageSize;
            check(window.CurrentGroupName == "仕事" && window.CurrentPageIndex == 1 && window.RequestedPageSize == 25 && list.Items.Count == screenshotPageSize &&
                Ids().SequenceEqual(Enumerable.Range(screenshotPageSize, screenshotPageSize).Select(index => "a-" + index)) && window.Height <= screenshotMaxHeight + 0.5 && AllRowsFit(list),
                "The synthetic paged-snippets screenshot shows a complete keyboard-accessible second page within the actual monitor work area");
        }
        finally { window.Close(); }
    }

    private static async Task RunActualGroupChecksAsync(Action<bool, string> check)
    {
        var window = new MainWindow { Left = -20000, Top = -20000, ShowInTaskbar = false, ForceClose = true, SuppressAutoHide = true };
        try
        {
            window.ConfigurePaging(2, false); window.FitToWorkArea(1200);
            window.Render([], [], "実グループのみの合成検証", false); window.RenderGroups([]); window.ShowSnippets();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var list = (ListBox)window.FindName("ItemsList"); var groups = (ComboBox)window.FindName("GroupFilter");
            var search = (TextBox)window.FindName("SearchInput"); var copy = (Button)window.FindName("CopyButton");
            string[] Ids() => list.Items.Cast<LibraryRow>().Select(x => x.Id).ToArray();
            check(groups.Items.Count == 0 && groups.SelectedIndex == -1 && window.CurrentGroupName.Length == 0 && list.Items.Count == 0 && !copy.IsEnabled,
                "An empty snippet library has no synthetic group or selectable entry");
            check(!window.AdvancePage(-1) && !window.AdvancePage(1) && window.PageCount == 1,
                "An empty actual-group list safely ignores both navigation directions");
            var realGroups = new[] { new GroupItem("actual-work", "仕事"), new GroupItem("actual-empty", "空のグループ"), new GroupItem("actual-all-name", "すべて"), new GroupItem("actual-mail", "メール") };
            window.RenderGroups(realGroups.Take(1));
            check(groups.Items.Count == 1 && groups.SelectedItem as string == "仕事" && window.CurrentGroupName == "仕事" && list.Items.Count == 0,
                "Adding the first actual group to a zero-group library selects it even while empty");
            var realSnippets = new[]
            {
                new SnippetItem("actual-w0", "仕事の定型文1", "共通語：仕事本文1", "仕事", 0),
                new SnippetItem("actual-w1", "仕事の定型文2", "仕事限定の検索語", "仕事", 1),
                new SnippetItem("actual-w2", "仕事の定型文3", "仕事本文3", "仕事", 2),
                new SnippetItem("actual-l0", "通常グループの定型文1", "共通語：通常本文1", "すべて", 3),
                new SnippetItem("actual-l1", "通常グループの定型文2", "共通語：通常本文2", "すべて", 4),
                new SnippetItem("actual-l2", "通常グループの定型文3", "共通語：通常本文3", "すべて", 5),
                new SnippetItem("actual-m0", "メール署名", "署名の合成本文", "メール", 6)
            };
            window.RenderGroups(realGroups); window.Render([], realSnippets, "登録済み実グループの合成検証", false);
            check(groups.Items.Cast<string>().SequenceEqual(realGroups.Select(x => x.Name)) && groups.Items.Cast<string>().Count(x => x == "すべて") == 1,
                "The selector contains each registered group exactly once in shared order");
            check(window.CurrentGroupName == "仕事" && window.FilteredItemCount == 3 && Ids().SequenceEqual(new[] { "actual-w0", "actual-w1" }),
                "The first actual group displays only its own first page after snippets arrive");
            groups.SelectedItem = "すべて";
            check(window.CurrentGroupName == "すべて" && window.FilteredItemCount == 3 && window.PageCount == 2 && Ids().SequenceEqual(new[] { "actual-l0", "actual-l1" }),
                "A registered group literally named All behaves as one ordinary group instead of a combined view");
            search.Text = "共通語";
            check(window.FilteredItemCount == 3 && Ids().All(x => x.StartsWith("actual-l", StringComparison.Ordinal)),
                "Searching a real group named All cannot include matching text from another group");
            search.Text = "仕事限定";
            check(window.FilteredItemCount == 0 && list.Items.Count == 0 && !copy.IsEnabled,
                "A selected actual group does not expose another group's unique search match");
            search.Clear();
            check(window.AdvancePage(1) && window.CurrentGroupName == "すべて" && window.CurrentPageIndex == 1 && Ids().SequenceEqual(new[] { "actual-l2" }),
                "A real group named All pages through its own remaining items");
            check(window.AdvancePage(1) && window.CurrentGroupName == "メール" && Ids().SequenceEqual(new[] { "actual-m0" }),
                "The final page of a real group named All advances to its actual following group");
            check(window.AdvancePage(-1) && window.CurrentGroupName == "すべて" && window.CurrentPageIndex == 1 && Ids().SequenceEqual(new[] { "actual-l2" }),
                "Backward navigation enters a real group named All at its final internal page");
            groups.SelectedItem = "空のグループ";
            check(window.CurrentGroupName == "空のグループ" && list.Items.Count == 0 && groups.Items.Cast<string>().Contains("空のグループ"),
                "A registered empty group remains selectable between populated actual groups");
            groups.SelectedItem = "仕事";
            check(!window.AdvancePage(-1) && window.CurrentGroupName == "仕事" && window.CurrentPageIndex == 0,
                "Actual groups stop before the first page when looping is disabled");
            groups.SelectedItem = "メール";
            check(!window.AdvancePage(1) && window.CurrentGroupName == "メール",
                "Actual groups stop after the final page when looping is disabled");
            window.ConfigurePaging(2, true);
            check(window.AdvancePage(1) && window.CurrentGroupName == "仕事" && window.CurrentPageIndex == 0,
                "Looping from the final actual group returns directly to the first actual group");
            check(window.AdvancePage(-1) && window.CurrentGroupName == "メール",
                "Reverse looping from the first actual group returns directly to the final group");
            groups.SelectedItem = "すべて"; search.Text = "共通語"; window.AdvancePage(1);
            var remainingSnippets = realSnippets.Where(x => x.Group != "すべて").ToArray();
            window.Render([], remainingSnippets, "選択グループを削除した合成検証", false);
            window.RenderGroups(realGroups.Where(x => x.Name != "すべて"));
            check(window.CurrentGroupName == "仕事" && groups.SelectedItem as string == "仕事" && search.Text.Length == 0 && window.CurrentPageIndex == 0 && Ids().SequenceEqual(new[] { "actual-w0", "actual-w1" }),
                "Deleting the selected actual group falls back to the first remaining actual group");
            check(groups.Items.Cast<string>().SequenceEqual(new[] { "仕事", "空のグループ", "メール" }),
                "Selected-group deletion preserves remaining order and registered empty groups");
            window.RenderGroups(realGroups); window.Render([], realSnippets, "実グループを再登録した合成検証", false);
            groups.SelectedItem = "メール"; search.Text = "署名"; window.Hide(); window.ShowSnippets();
            check(window.CurrentGroupName == "仕事" && window.CurrentPageIndex == 0 && search.Text.Length == 0 && Ids().SequenceEqual(new[] { "actual-w0", "actual-w1" }),
                "Reopening snippets starts at the first actual group and clears the previous group's search");
            window.Render([], [], "実グループをすべて除いた合成検証", false); window.RenderGroups([]);
            check(groups.Items.Count == 0 && window.CurrentGroupName.Length == 0 && list.Items.Count == 0 && !window.AdvancePage(1),
                "Removing every actual group returns to a safe zero-group state");
            window.RenderGroups(new[] { realGroups[1] });
            check(groups.Items.Count == 1 && window.CurrentGroupName == "空のグループ" && list.Items.Count == 0 && !window.AdvancePage(-1) && !window.AdvancePage(1),
                "A sole empty actual group persists without creating synthetic entries or looping into itself");
        }
        finally { window.Close(); }
    }

    private static bool AllRowsFit(ListBox list)
    {
        list.UpdateLayout();
        var viewport = VisualDescendants<ScrollContentPresenter>(list).FirstOrDefault();
        var visibleBounds = viewport is null ? new Rect(0, 0, list.ActualWidth, list.ActualHeight) :
            viewport.TransformToAncestor(list).TransformBounds(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
        for (int index = 0; index < list.Items.Count; index++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem row || row.ActualHeight <= 0) return false;
            var bounds = row.TransformToAncestor(list).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
            if (bounds.Top < visibleBounds.Top - 0.5 || bounds.Bottom > visibleBounds.Bottom + 0.5) return false;
        }
        return true;
    }

    private static async Task SaveScreenshotAsync(Window window, string resultPath, string fileName)
    {
        if (!window.IsVisible) window.Show();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * 1.5), (int)Math.Ceiling(content.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string directory = Path.GetDirectoryName(Path.GetFullPath(resultPath))!;
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }
}
