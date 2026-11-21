using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipBridge.App.Services;

public sealed record LocalSettings
{
    public string DeviceId { get; init; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; init; } = Environment.MachineName;
    public string SharedFolder { get; init; } = "";
    public string ProtectedPassphrase { get; init; } = "";
    public bool Paused { get; init; }
    public bool DoubleControlEnabled { get; init; } = true;
    public bool AutoPasteEnabled { get; init; } = true;
    public int PopupPageSize { get; init; } = 25;
    public bool LoopPages { get; init; }
    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipBridge");
    public static string FileName => Path.Combine(DataFolder, "settings.json");
    public static LocalSettings Load()
    {
        if (!File.Exists(FileName)) return new();
        return JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(FileName)) ?? throw new InvalidDataException("設定を読み取れません。");
    }
    public void Save()
    {
        Directory.CreateDirectory(DataFolder);
        string temporary = FileName + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FileName, true);
    }
    public string GetPassphrase() => Encoding.UTF8.GetString(WindowsSecret.Unprotect(Convert.FromBase64String(ProtectedPassphrase)));
    public static string ProtectPassphrase(string value) => Convert.ToBase64String(WindowsSecret.Protect(Encoding.UTF8.GetBytes(value)));
    public static string? FindOneDriveFolder()
    {
        foreach (string variable in new[] { "OneDriveConsumer", "OneDriveCommercial", "OneDrive" })
        {
            string? path = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) return Path.Combine(path, "ClipBridge");
        }
        return null;
    }
}

internal static class WindowsSecret
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Protect(byte[] value) => Transform(value, true);
    public static byte[] Unprotect(byte[] value) => Transform(value, false);
    private static byte[] Transform(byte[] value, bool protect)
    {
        var input = new Blob { Size = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            bool success = protect ? CryptProtectData(ref input, "ClipBridge", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            // Erase temporary unmanaged plaintext buffers before releasing them.
            if (input.Data != IntPtr.Zero) { Marshal.Copy(new byte[input.Size], 0, input.Data, input.Size); Marshal.FreeHGlobal(input.Data); }
            if (output.Data != IntPtr.Zero) { Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size); LocalFree(output.Data); }
            CryptographicOperations.ZeroMemory(value);
        }
    }
}
