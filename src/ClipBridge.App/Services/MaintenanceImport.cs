using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipBridge.Core;

namespace ClipBridge.App.Services;

internal static class MaintenanceImport
{
    internal static async Task<int> RunAsync(string input, string resultPath)
    {
        object result;
        int exitCode = 1;
        try
        {
            using var mutex = new Mutex(true, @"Local\ClipBridge.App.v1", out bool first);
            if (!first) throw new InvalidOperationException("起動中のClipBridgeを通知領域から終了してください。");
            var settings = LocalSettings.Load();
            string phrase = settings.GetPassphrase();
            string folderHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(settings.SharedFolder).ToUpperInvariant())))[..16];
            await using var engine = await SyncEngine.OpenAsync(settings.SharedFolder, phrase, Path.Combine(LocalSettings.DataFolder, "cache", folderHash), settings.DeviceId, settings.DeviceName);
            var before = engine.GetSnapshot();
            string directory = Path.Combine(LocalSettings.DataFolder, "Backups"); Directory.CreateDirectory(directory);
            string backupPath = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}-before-clibor-migration.cbb");
            await engine.ExportBackupAsync(backupPath, phrase);
            var snapshot = TransferSnapshots.FromCliborJson(input, before);
            var imported = await engine.ImportSnapshotAsync(snapshot);
            await engine.PollAsync();
            var verification = TransferSnapshots.VerifyClibor(input, engine.GetSnapshot());
            string verificationJson = JsonSerializer.Serialize(verification);
            bool verified = JsonDocument.Parse(verificationJson).RootElement.GetProperty("Success").GetBoolean();
            // Also reopen the encrypted on-disk state before reporting a completed migration.
            await engine.DisposeAsync();
            await using var reopened = await SyncEngine.OpenAsync(settings.SharedFolder, phrase, Path.Combine(LocalSettings.DataFolder, "cache", folderHash), settings.DeviceId, settings.DeviceName);
            var persisted = TransferSnapshots.VerifyClibor(input, reopened.GetSnapshot());
            bool persistedOk = JsonDocument.Parse(JsonSerializer.Serialize(persisted)).RootElement.GetProperty("Success").GetBoolean();
            exitCode = verified && persistedOk ? 0 : 2;
            result = new { Success = exitCode == 0, BackupPath = backupPath, Before = new { Groups = before.Groups.Count, Snippets = before.Snippets.Count, History = before.History.Count },
                Imported = imported, Verification = verification, PersistedVerification = persisted, Pending = reopened.Status.PendingCount };
        }
        catch (Exception ex)
        {
            // Never include source bodies, secrets, or a stack trace in the report.
            result = new { Success = false, ErrorType = ex.GetType().Name, Message = ex is ArgumentException or InvalidDataException or InvalidOperationException ? ex.Message : "移行処理を完了できませんでした。" };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return exitCode;
    }
}
