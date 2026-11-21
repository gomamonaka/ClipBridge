using System.Security.Cryptography;
using System.Windows;
using Microsoft.Win32;
using ClipBridge.Core;
using ClipBridge.App.Services;

namespace ClipBridge.App;

public partial class App
{
    private async void ManageGroups()
    {
        if (window is null || engine is null || configuring || exiting) return;
        bool previous = window.SuppressAutoHide; window.SuppressAutoHide = true;
        try
        {
            var connected = engine;
            var before = connected.GetSnapshot();
            var beforeGroups = before.Groups;
            var beforeSnippets = before.Snippets;
            var dialog = new GroupManagerWindow(beforeGroups.Select(x => new GroupItem(x.Id, x.Name)),
                beforeSnippets.Select(x => new SnippetItem(x.Id, x.Title, x.Text, x.Group, x.SortOrder))) { Owner = window };
            if (dialog.ShowDialog() != true) return;
            await DataOperationAsync(async () =>
            {
                if (engine != connected) throw new InvalidOperationException("接続先が変わりました。管理画面を開き直してください。");
                await AutomaticBackupAsync("before-group-edit");
                var finalGroups = dialog.Groups.Select((x, index) => new SnippetGroup(x.Id, x.Name, index, DateTimeOffset.UtcNow)).ToArray();
                var finalSnippets = dialog.Snippets.Select((x, index) => new Snippet(x.Id, x.Title, x.Text, DateTimeOffset.UtcNow,
                    x.Group, finalGroups.Single(y => y.Name.Equals(x.Group, StringComparison.OrdinalIgnoreCase)).Id, index)).ToArray();
                await connected.ApplyGroupLayoutAsync(finalGroups, finalSnippets, beforeSnippets, beforeGroups);
                window.ShowNotice("グループ・定型文・並び順を保存しました。");
            });
        }
        catch (Exception ex)
        {
            string detail = ex is ArgumentException or InvalidOperationException ? "\n" + ex.Message : "";
            ShowError("管理画面の変更を保存できませんでした。" + detail, ex);
        }
        finally { window.SuppressAutoHide = previous; }
    }

    private void EditHistory(HistoryItem item)
    {
        if (window is null || engine is null || item.IsImage) return;
        bool previous = window.SuppressAutoHide; window.SuppressAutoHide = true;
        try
        {
            var dialog = new TextEditorWindow(item) { Owner = window };
            if (dialog.ShowDialog() == true)
                _ = GuardAsync(async () => { if (engine is not null) await engine.EditHistoryAsync(item.Id, dialog.EditedText); });
        }
        finally { window.SuppressAutoHide = previous; }
    }

    private async Task ImportCsvAsync()
    {
        if (window is null || engine is null) return;
        bool previous = window.SuppressAutoHide; window.SuppressAutoHide = true;
        try
        {
            var picker = new OpenFileDialog { Title = "定型文CSVを取り込む", Filter = "CSVファイル|*.csv|すべてのファイル|*.*" };
            if (picker.ShowDialog(window) != true) return;
            var document = await Task.Run(() => CsvTransfer.Read(picker.FileName));
            var snapshot = TransferSnapshots.FromCsv(document, engine.GetSnapshot());
            string warnings = document.Warnings.Count > 0 ? "\n\n注意：" + string.Join("\n", document.Warnings.Take(5)) : "";
            if (MessageBox.Show(window, $"グループ {snapshot.Groups.Count} 件、定型文 {snapshot.Snippets.Count} 件を追加・更新します。取り込み前のバックアップも保存します。" + warnings,
                "CSV取り込み", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            await DataOperationAsync(async () =>
            {
                await AutomaticBackupAsync("before-csv-import");
                var result = await engine.ImportSnapshotAsync(snapshot);
                MessageBox.Show(window, $"定型文 {result.Snippets} 件を取り込みました。", "取り込み完了");
            });
        }
        catch (Exception ex) { ShowError("CSVを取り込めませんでした。形式、文字コード、保存上限を確認してください。\n" + SafeDataError(ex), ex); }
        finally { window.SuppressAutoHide = previous; }
    }

    private void ExportCsv()
    {
        if (window is null || engine is null) return;
        bool previous = window.SuppressAutoHide; window.SuppressAutoHide = true;
        try
        {
            var picker = new SaveFileDialog { Title = "定型文CSVを書き出す（CSVは平文です）", Filter = "CSVファイル|*.csv", FileName = "ClipBridge-定型文.csv" };
            if (picker.ShowDialog(window) != true) return;
            var snapshot = engine.GetSnapshot();
            CsvTransfer.Write(picker.FileName, new CsvTransferDocument(
                snapshot.Groups.Select(x => new CsvGroup(x.Id, x.Name, x.SortOrder)).ToArray(),
                snapshot.Snippets.Select(x => new CsvSnippet(x.Id, x.Group, x.Title, x.Text, x.SortOrder)).ToArray(), []));
            window.ShowNotice($"定型文 {snapshot.Snippets.Count} 件とグループを書き出しました。");
        }
        catch (Exception ex) { ShowError("CSVを書き出せませんでした。保存先を確認してください。", ex); }
        finally { window.SuppressAutoHide = previous; }
    }

    private async Task ExportBackupAsync()
    {
        if (window is null || engine is null) return;
        bool previous = window.SuppressAutoHide; window.SuppressAutoHide = true;
        try
        {
            var picker = new SaveFileDialog { Title = "暗号化バックアップを保存", Filter = "ClipBridgeバックアップ|*.cbb", FileName = $"ClipBridge-{DateTime.Now:yyyyMMdd-HHmmss}.cbb" };
            if (picker.ShowDialog(window) != true) return;
            await DataOperationAsync(async () => await engine.ExportBackupAsync(picker.FileName, settings.GetPassphrase()));
            MessageBox.Show(window, "履歴・定型文・グループのバックアップを保存しました。復元には作成時の共通の合言葉が必要です。", "バックアップ完了");
        }
        catch (Exception ex) { ShowError("バックアップを保存できませんでした。保存先を確認してください。", ex); }
        finally { window.SuppressAutoHide = previous; }
    }

    private async Task RestoreBackupAsync()
    {
        if (window is null || engine is null) return;
        bool previous = window.SuppressAutoHide; window.SuppressAutoHide = true;
        try
        {
            var picker = new OpenFileDialog { Title = "バックアップから復元", Filter = "ClipBridgeバックアップ|*.cbb|すべてのファイル|*.*" };
            if (picker.ShowDialog(window) != true) return;
            var phrase = new BackupPassphraseWindow(settings.GetPassphrase()) { Owner = window };
            if (phrase.ShowDialog() != true) return;
            await DataOperationAsync(async () =>
            {
                await AutomaticBackupAsync("before-restore");
                var result = await engine.RestoreBackupAsync(picker.FileName, phrase.Passphrase);
                MessageBox.Show(window, $"グループ {result.Groups} 件、定型文 {result.Snippets} 件、履歴 {result.History} 件を復元しました。", "復元完了");
            });
        }
        catch (Exception ex) { ShowError("復元できませんでした。バックアップ作成時の合言葉とファイルを確認してください。", ex); }
        finally { window.SuppressAutoHide = previous; }
    }

    private async Task ImportCliborAsync()
    {
        if (window is null || engine is null) return;
        bool previous = window.SuppressAutoHide; window.SuppressAutoHide = true;
        try
        {
            var picker = new OpenFileDialog { Title = "CliborのCSV、または変換済み移行JSONを選択", Filter = "移行データ|*.csv;*.json|CSV|*.csv|JSON|*.json" };
            if (picker.ShowDialog(window) != true) return;
            if (Path.GetExtension(picker.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var csv = await Task.Run(() => CsvTransfer.Read(picker.FileName));
                var snapshot = TransferSnapshots.FromCsv(csv, engine.GetSnapshot());
                await ImportPreparedSnapshotAsync(snapshot);
            }
            else
            {
                var snapshot = TransferSnapshots.FromCliborJson(picker.FileName, engine.GetSnapshot());
                await ImportPreparedSnapshotAsync(snapshot);
            }
        }
        catch (Exception ex) { ShowError("Cliborの移行データを取り込めませんでした。\n" + SafeDataError(ex), ex); }
        finally { window.SuppressAutoHide = previous; }
    }

    private async Task ImportPreparedSnapshotAsync(ClipBridgeSnapshot snapshot)
    {
        if (window is null || engine is null) return;
        if (MessageBox.Show(window, $"グループ {snapshot.Groups.Count} 件・定型文 {snapshot.Snippets.Count} 件・履歴 {snapshot.History.Count} 件を取り込みます。", "Cliborから移行", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await DataOperationAsync(async () =>
        {
            await AutomaticBackupAsync("before-clibor-import");
            var result = await engine.ImportSnapshotAsync(snapshot);
            MessageBox.Show(window, $"定型文 {result.Snippets} 件・履歴 {result.History} 件を取り込みました。", "移行完了");
        });
    }

    private async Task DataOperationAsync(Func<Task> action)
    {
        await operationGate.WaitAsync();
        bool oldConfiguring = configuring; configuring = true;
        try { while (polling) await Task.Delay(25); await action(); Refresh(); }
        finally { configuring = oldConfiguring; operationGate.Release(); }
    }

    private async Task<string> AutomaticBackupAsync(string reason)
    {
        if (engine is null) throw new InvalidOperationException("共有接続がありません。");
        string directory = Path.Combine(LocalSettings.DataFolder, "Backups");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{reason}.cbb");
        await engine.ExportBackupAsync(path, settings.GetPassphrase());
        return path;
    }
    private static string SafeDataError(Exception ex) => ex is ArgumentException or InvalidDataException or FormatException ? ex.Message : ex.GetType().Name;
}
