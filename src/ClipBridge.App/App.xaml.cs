using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ClipBridge.App.Services;
using ClipBridge.Core;
using Forms = System.Windows.Forms;

namespace ClipBridge.App;

public partial class App : Application
{
    private MainWindow? window;
    private SyncEngine? engine;
    private ClipboardService? clipboard;
    private PopupInteractionService? interaction;
    private Forms.NotifyIcon? tray;
    private DispatcherTimer? pollTimer;
    private LocalSettings settings = new();
    private Mutex? singleton;
    private EventWaitHandle? showSignal;
    private RegisteredWaitHandle? showRegistration;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private bool polling, configuring, exiting;
    private long interactionVersion;
    private long remoteVersion;
    private bool selectionInProgress;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 2 && e.Args[0] == "--render-demo")
        {
            try { await RenderDemoAsync(e.Args[1]); Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--self-test")
        {
            try { await UiSelfTest.RunAsync(e.Args[1]); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(e.Args[1], ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length >= 3 && e.Args[0] == "--maintenance-import-clibor")
        {
            int result = await MaintenanceImport.RunAsync(e.Args[1], e.Args[2]);
            Shutdown(result); return;
        }
        if (e.Args.Contains("--demo"))
        {
            window = new MainWindow(); window.SetSyntheticDemo();
            if (e.Args.Contains("--paged")) window.ConfigurePaging(5, false);
            window.Closed += (_, _) => Shutdown(); window.ForceClose = true; window.Show(); return;
        }
        singleton = new Mutex(true, @"Local\ClipBridge.App.v1", out bool first);
        if (!first)
        {
            try { using var signal = EventWaitHandle.OpenExisting(@"Local\ClipBridge.Show.v1"); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClipBridge.Show.v1");
        showRegistration = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.BeginInvoke(() => window?.ShowHistory()), null, Timeout.Infinite, false);
        DispatcherUnhandledException += (_, args) => { args.Handled = true; ShowError("処理を完了できませんでした。もう一度操作してください。", args.Exception); };
        window = new MainWindow(); MainWindow = window;
        window.OpeningRequested += () =>
        {
            if (interaction is null) return;
            interaction.OutsideClickDismissalEnabled = true;
            interaction.CaptureTarget(preserveCurrentTarget: window.IsVisible);
            interaction.PlacePopup(window, window.FitToWorkArea);
        };
        window.LayoutPlacementRequested += () => interaction?.PlacePopup(window, window.FitToWorkArea, preserveCurrentPosition: true);
        window.IsVisibleChanged += (_, _) =>
        {
            if (interaction is not null) interaction.OutsideClickDismissalEnabled = window.IsVisible;
        };
        window.CopyHistoryRequested += item => _ = SelectHistoryAsync(item, false);
        window.CopyHistoryOnlyRequested += item => _ = SelectHistoryAsync(item, true);
        window.CopySnippetRequested += item => _ = SelectSnippetAsync(item, false);
        window.CopySnippetOnlyRequested += item => _ = SelectSnippetAsync(item, true);
        window.EditSnippetRequested += EditSnippet;
        window.SaveHistoryAsSnippetRequested += item =>
        {
            if (item.IsImage) { window.ShowNotice("画像は定型文に登録できません。"); return; }
            string title = item.Detail.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "新しい定型文";
            EditSnippet(new SnippetItem("", title[..Math.Min(120, title.Length)], item.Detail));
        };
        window.DeleteSnippetRequested += item =>
        {
            bool wasSuppressed = window.SuppressAutoHide; window.SuppressAutoHide = true;
            try
            {
                if (MessageBox.Show(window, "「" + item.Title + "」を両方の端末から削除しますか？", "定型文を削除", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _ = GuardAsync(async () => { if (engine is not null) await engine.DeleteSnippetAsync(item.Id); });
            }
            finally { window.SuppressAutoHide = wasSuppressed; }
        };
        window.DeleteHistoryRequested += item => _ = GuardAsync(async () => { if (engine is not null) await engine.DeleteHistoryAsync(item.Id); });
        window.PauseRequested += TogglePause;
        window.SettingsRequested += () => _ = ConfigureAsync();
        window.ManageGroupsRequested += ManageGroups;
        window.ImportCsvRequested += () => _ = ImportCsvAsync();
        window.ExportCsvRequested += ExportCsv;
        window.BackupRequested += () => _ = ExportBackupAsync();
        window.RestoreRequested += () => _ = RestoreBackupAsync();
        window.ImportCliborRequested += () => _ = ImportCliborAsync();
        window.EditHistoryRequested += EditHistory;
        window.PagingSettingsRequested += ConfigurePaging;
        try { settings = LocalSettings.Load(); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { ShowError("保存済み設定を読み取れません。接続を設定し直してください。", ex); }
        window.ConfigurePaging(settings.PopupPageSize, settings.LoopPages);
        CreateTray();
        if (string.IsNullOrEmpty(settings.SharedFolder))
        {
            await ConfigureAsync();
            if (engine is null) { await ExitAsync(); return; }
        }
        else
        {
            try { await ConnectAsync(settings, settings.GetPassphrase()); }
            catch (Exception ex) { ShowError("接続を開始できません。OneDriveフォルダーと共通パスフレーズを確認してください。", ex); await ConfigureAsync(); }
        }
        if (engine is null) { await ExitAsync(); return; }
        try
        {
            interaction = new PopupInteractionService(Dispatcher) { DoubleControlEnabled = settings.DoubleControlEnabled };
            interaction.DoubleControlPressed += () =>
            {
                if (configuring || exiting || window.SuppressAutoHide) return;
                window.ShowHistory();
            };
            interaction.OutsidePointerPressed += window.DismissFromOutsideClick;
            if (!interaction.HookAvailable) window.ShowNotice("Ctrl 2回を登録できません。Ctrl + Alt + Vで開けます。");
            if (!interaction.OutsideClickHookAvailable) window.ShowNotice("外側のクリックを検出できません。Escで一覧を閉じられます。");
        }
        catch (Exception ex) { ShowError("Ctrl 2回を登録できません。Ctrl + Alt + Vで開けます。", ex, false); }
        try
        {
            clipboard = new ClipboardService { Paused = settings.Paused };
            clipboard.ShortcutPressed += snippets =>
            {
                if (configuring || exiting || window.SuppressAutoHide) return;
                interaction?.RequestInputRecovery();
                if (snippets) window.ShowSnippets(); else window.ShowHistory();
            };
            clipboard.TextCaptured += text => { interactionVersion++; _ = GuardAsync(async () => { if (engine is not null && !settings.Paused) await engine.CaptureTextAsync(text); }); };
            clipboard.ImageCaptured += png => { interactionVersion++; _ = GuardAsync(async () => { if (engine is not null && !settings.Paused) await engine.CaptureImageAsync(png); }); };
            clipboard.Notice += text => window.ShowNotice(text);
            if (clipboard.ShortcutWarning.Length > 0) window.ShowNotice(clipboard.ShortcutWarning);
        }
        catch (Exception ex) { ShowError("クリップボード監視を開始できません。アプリを再起動してください。", ex); }
        pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        pollTimer.Tick += async (_, _) =>
        {
            if (polling || configuring || engine is null) return;
            polling = true;
            try { await Task.Run(() => engine.PollAsync()); }
            catch (Exception ex) { ShowError("共有フォルダーを確認できません。OneDriveの状態を確認してください。", ex, false); }
            finally { polling = false; }
        };
        pollTimer.Start(); Refresh(); if (!e.Args.Contains("--background")) window.ShowHistory();
    }

    private async Task ConnectAsync(LocalSettings next, string passphrase)
    {
        string folderHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(next.SharedFolder).ToUpperInvariant())))[..16];
        SyncEngine opened = await Task.Run(() => SyncEngine.OpenAsync(next.SharedFolder, passphrase, Path.Combine(LocalSettings.DataFolder, "cache", folderHash), next.DeviceId, next.DeviceName));
        SyncEngine? previous = engine;
        engine = opened; settings = next;
        opened.Changed += () => Dispatcher.BeginInvoke(Refresh);
        opened.StatusChanged += _ => Dispatcher.BeginInvoke(Refresh);
        opened.RemoteClipboardReceived += entry =>
        {
            long observedRemoteVersion = Interlocked.Increment(ref remoteVersion);
            long observedInteraction = Interlocked.Read(ref interactionVersion);
            uint observedSequence = ClipboardService.CurrentSequence;
            Dispatcher.BeginInvoke(async () =>
            {
                if (settings.Paused || configuring || selectionInProgress || clipboard is null || engine != opened) return;
                try
                {
                    await clipboard.CopyAsync(entry, () => !settings.Paused && !configuring && !selectionInProgress && engine == opened && Interlocked.Read(ref remoteVersion) == observedRemoteVersion && Interlocked.Read(ref interactionVersion) == observedInteraction && ClipboardService.CurrentSequence == observedSequence);
                }
                catch (Exception ex) { ShowError("受信した内容をコピーできません。履歴から選び直してください。", ex, false); }
            });
        };
        if (previous is not null) await previous.DisposeAsync();
    }

    private async Task ConfigureAsync()
    {
        if (configuring || window is null) return;
        configuring = true;
        bool wasSuppressed = window.SuppressAutoHide; window.SuppressAutoHide = true;
        if (clipboard is not null) clipboard.Paused = true;
        bool retry = false;
        try
        {
            var dialog = new SetupWindow(string.IsNullOrEmpty(settings.SharedFolder) ? LocalSettings.FindOneDriveFolder() : settings.SharedFolder, settings.DeviceName);
            if (window.IsVisible) dialog.Owner = window;
            if (dialog.ShowDialog() != true) return;
            window.ShowNotice("暗号化の設定と共有フォルダーを確認しています…");
            await operationGate.WaitAsync();
            try
            {
                while (polling) await Task.Delay(30);
                LocalSettings next = settings with { SharedFolder = dialog.SharedFolder, DeviceName = dialog.DeviceName, ProtectedPassphrase = LocalSettings.ProtectPassphrase(dialog.Passphrase) };
                await ConnectAsync(next, dialog.Passphrase);
                settings.Save(); Refresh(); window.ShowHistory();
            }
            finally { operationGate.Release(); }
        }
        catch (Exception ex) { ShowError(ex is InvalidPassphraseException ? "共通の合言葉が一致しません。先に設定した端末と同じ合言葉を入力してください。" : "接続できませんでした。フォルダー、アクセス権、共通パスフレーズを確認してください。", ex); retry = true; }
        finally { configuring = false; window.SuppressAutoHide = wasSuppressed; if (clipboard is not null) clipboard.Paused = settings.Paused; }
        if (retry) await ConfigureAsync();
    }

    private void Refresh()
    {
        if (window is null || engine is null || exiting) return;
        window.Render(engine.GetHistory().Select(x => new HistoryItem(x.Id, x.Kind == ClipboardKind.Image ? "画像" : Preview(x.Text), x.Text ?? "画像を選んでコピーできます。", x.CreatedUtc, x.DeviceName, x.Kind == ClipboardKind.Image, x.ImagePng)),
            engine.GetSnippets().Select(x => new SnippetItem(x.Id, x.Title, x.Text, x.Group, x.SortOrder)),
            settings.Paused ? "自動共有を一時停止中" : engine.Status.Message, settings.Paused);
        window.RenderGroups(engine.GetGroups().Select(x => new GroupItem(x.Id, x.Name)));
        window.SetInteractionMode(settings.AutoPasteEnabled, settings.DoubleControlEnabled);
        if (tray is not null) tray.Text = settings.Paused ? "ClipBridge — 自動共有を一時停止中" : "ClipBridge — OneDriveで共有";
    }
    private static string Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "（空白のテキスト）";
        return text.Replace("\r", "").Replace("\n", "  ").Trim() is string value ? value[..Math.Min(180, value.Length)] : "";
    }
    private async Task SelectHistoryAsync(HistoryItem item, bool copyOnly)
    {
        ClipboardEntry? entry = engine?.GetHistoryEntry(item.Id);
        if (entry is not null) await UseEntryAsync(entry, copyOnly);
    }
    private Task SelectSnippetAsync(SnippetItem item, bool copyOnly) => UseEntryAsync(
        new ClipboardEntry(Guid.NewGuid().ToString("N"), ClipboardKind.Text, item.Text, null, DateTimeOffset.UtcNow, settings.DeviceId, settings.DeviceName), copyOnly);

    private async Task UseEntryAsync(ClipboardEntry entry, bool copyOnly)
    {
        if (selectionInProgress || configuring || exiting || clipboard is null || window is null) return;
        selectionInProgress = true;
        Interlocked.Increment(ref interactionVersion);
        try
        {
            if (!await clipboard.CopyAsync(entry)) throw new IOException("クリップボードを使用中です。");
            string? notice;
            if (interaction is not null) notice = await interaction.PasteToTargetAsync(window.Hide, settings.AutoPasteEnabled && !copyOnly);
            else { window.Hide(); notice = settings.AutoPasteEnabled && !copyOnly ? "コピーしました。貼り付け先でCtrl + Vを押してください。" : null; }
            if (notice is not null) window.ShowNotice(notice);
            if (!settings.Paused)
            {
                _ = GuardAsync(async () =>
                {
                    if (engine is null) return;
                    if (entry.Kind == ClipboardKind.Text) await engine.CaptureTextAsync(entry.Text ?? ""); else await engine.CaptureImageAsync(entry.ImagePng!);
                });
            }
        }
        catch (Exception ex) { ShowError("コピーできませんでした。もう一度選択してください。", ex, false); }
        finally { selectionInProgress = false; }
    }

    private void EditSnippet(SnippetItem? item)
    {
        if (window is null) return;
        bool wasSuppressed = window.SuppressAutoHide; window.SuppressAutoHide = true;
        try
        {
            var groups = engine?.GetGroups().Select(x => x.Name).ToArray() ?? ["共通"];
            var dialog = new SnippetDialog(item, groups) { Owner = window };
            if (dialog.ShowDialog() == true) _ = GuardAsync(async () =>
            {
                if (engine is not null) await engine.UpsertSnippetAsync(string.IsNullOrEmpty(item?.Id) ? null : item.Id, dialog.SnippetTitle, dialog.SnippetText, dialog.SnippetGroup);
            });
        }
        finally { window.SuppressAutoHide = wasSuppressed; }
    }
    private void TogglePause()
    {
        settings = settings with { Paused = !settings.Paused };
        if (clipboard is not null) clipboard.Paused = settings.Paused;
        try { settings.Save(); } catch (IOException ex) { ShowError("設定を保存できません。", ex, false); }
        Refresh();
    }
    private async Task GuardAsync(Func<Task> operation)
    {
        if (exiting || configuring) return;
        await operationGate.WaitAsync();
        try { await operation(); Refresh(); }
        catch (Exception ex) { ShowError("操作を完了できません。共有フォルダーとOneDriveの状態を確認してください。", ex, false); }
        finally { operationGate.Release(); }
    }
    private void ShowError(string message, Exception ex, bool dialog = true)
    {
        // No clipboard contents, passphrases or event payloads in diagnostics.
        window?.ShowNotice(message);
        try { Directory.CreateDirectory(LocalSettings.DataFolder); File.WriteAllText(Path.Combine(LocalSettings.DataFolder, "last-error.txt"), $"{DateTimeOffset.Now:O}\n{ex.GetType().Name}\n{message}"); } catch { }
        if (dialog)
        {
            bool wasSuppressed = window?.SuppressAutoHide ?? false;
            if (window is not null) window.SuppressAutoHide = true;
            try { MessageBox.Show(message, "ClipBridge", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { if (window is not null) window.SuppressAutoHide = wasSuppressed; }
        }
    }
    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("履歴を開く  Ctrl 2回 / Ctrl + Alt + V", null, (_, _) => window?.ShowHistory());
        menu.Items.Add("定型文を開く  Ctrl + Alt + Space", null, (_, _) => window?.ShowSnippets());
        menu.Items.Add(new Forms.ToolStripSeparator());
        var pause = new Forms.ToolStripMenuItem("自動共有を一時停止") { CheckOnClick = false };
        pause.Click += (_, _) => TogglePause(); menu.Items.Add(pause);
        menu.Items.Add("接続設定…", null, (_, _) => _ = ConfigureAsync());
        menu.Items.Add("グループ管理・並び替え…", null, (_, _) => ManageGroups());
        menu.Items.Add("ページ切り替え設定…", null, (_, _) => ConfigurePaging());
        var dataMenu = new Forms.ToolStripMenuItem("データ管理");
        dataMenu.DropDownItems.Add("CSVを取り込む…", null, (_, _) => _ = ImportCsvAsync());
        dataMenu.DropDownItems.Add("CSVを書き出す…", null, (_, _) => ExportCsv());
        dataMenu.DropDownItems.Add("バックアップを作成…", null, (_, _) => _ = ExportBackupAsync());
        dataMenu.DropDownItems.Add("バックアップから復元…", null, (_, _) => _ = RestoreBackupAsync());
        dataMenu.DropDownItems.Add("Cliborの移行データを取り込む…", null, (_, _) => _ = ImportCliborAsync());
        menu.Items.Add(dataMenu);
        var behavior = new Forms.ToolStripMenuItem("操作設定");
        var doubleControl = new Forms.ToolStripMenuItem("Ctrlを2回押して呼び出す") { CheckOnClick = true, Checked = settings.DoubleControlEnabled };
        doubleControl.Click += (_, _) =>
        {
            settings = settings with { DoubleControlEnabled = doubleControl.Checked };
            if (interaction is not null) interaction.DoubleControlEnabled = settings.DoubleControlEnabled;
            SaveBehaviorSettings();
        };
        var autoPaste = new Forms.ToolStripMenuItem("選択した内容を元のアプリに自動貼り付け") { CheckOnClick = true, Checked = settings.AutoPasteEnabled };
        autoPaste.Click += (_, _) => { settings = settings with { AutoPasteEnabled = autoPaste.Checked }; SaveBehaviorSettings(); };
        behavior.DropDownItems.Add(doubleControl); behavior.DropDownItems.Add(autoPaste); menu.Items.Add(behavior);
        menu.Items.Add("共有フォルダーを開く", null, (_, _) => { if (Directory.Exists(settings.SharedFolder)) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { settings.SharedFolder }, UseShellExecute = true }); });
        var startup = new Forms.ToolStripMenuItem("Windowsと一緒に起動") { Checked = IsAutostartEnabled(), CheckOnClick = true };
        startup.Click += (_, _) =>
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (startup.Checked) key.SetValue("ClipBridge", "\"" + Environment.ProcessPath + "\" --background"); else key.DeleteValue("ClipBridge", false);
            }
            catch (Exception ex) { startup.Checked = IsAutostartEnabled(); ShowError("自動起動の設定を保存できません。", ex); }
        };
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("終了", null, (_, _) => _ = ExitAsync());
        menu.Opening += (_, _) => { pause.Checked = settings.Paused; doubleControl.Checked = settings.DoubleControlEnabled; autoPaste.Checked = settings.AutoPasteEnabled; };
        tray = new Forms.NotifyIcon { Text = "ClipBridge", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => window?.ShowHistory();
    }
    private static bool IsAutostartEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("ClipBridge") is string;
    }
    private void SaveBehaviorSettings()
    {
        try { settings.Save(); } catch (IOException ex) { ShowError("操作設定を保存できません。", ex, false); }
        Refresh();
    }
    private async Task ExitAsync()
    {
        if (exiting) return; exiting = true;
        pollTimer?.Stop(); interaction?.Dispose(); clipboard?.Dispose(); tray?.Dispose();
        await operationGate.WaitAsync();
        try { while (polling) await Task.Delay(25); if (engine is not null) await engine.DisposeAsync(); }
        finally { operationGate.Release(); }
        if (window is not null) { window.ForceClose = true; window.Close(); }
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        interaction?.Dispose(); clipboard?.Dispose(); tray?.Dispose(); showRegistration?.Unregister(null); showSignal?.Dispose(); singleton?.Dispose(); base.OnExit(e);
    }
    private async Task RenderDemoAsync(string target)
    {
        window = new MainWindow { Left = -20000, Top = -20000, ShowInTaskbar = false, ForceClose = true, SuppressAutoHide = true };
        window.SetSyntheticDemo(); window.Show();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)window.Content;
        if (content is System.Windows.Controls.Panel panel && panel.Background is null) panel.Background = window.Background;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * 1.5), (int)Math.Ceiling(content.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        using (var stream = File.Create(target)) encoder.Save(stream);
        window.Close();
    }
}
