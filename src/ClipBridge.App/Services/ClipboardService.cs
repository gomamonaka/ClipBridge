using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipBridge.Core;

namespace ClipBridge.App.Services;

internal sealed class ClipboardService : IDisposable
{
    private const int WmClipboardUpdate = 0x031D, WmHotkey = 0x0312;
    private const string OriginFormat = "ClipBridge.Origin.v1";
    private readonly string origin = Guid.NewGuid().ToString("N");
    private readonly HwndSource source;
    private readonly DispatcherTimer debounce;
    private int generation;
    private bool disposed;
    private uint ownSequence;
    public bool Paused { get; set; }
    public event Action<string>? TextCaptured;
    public event Action<byte[]>? ImageCaptured;
    public event Action<bool>? ShortcutPressed;
    public event Action<string>? Notice;
    public string ShortcutWarning { get; }

    public ClipboardService()
    {
        source = new HwndSource(new HwndSourceParameters("ClipBridge background") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
        source.AddHook(Hook);
        if (!AddClipboardFormatListener(source.Handle)) throw new InvalidOperationException("クリップボードの監視を開始できません。アプリを再起動してください。");
        var failures = new List<string>();
        if (!RegisterHotKey(source.Handle, 1, 0x4003, 0x56)) failures.Add("Ctrl + Alt + V");
        if (!RegisterHotKey(source.Handle, 2, 0x4003, 0x20)) failures.Add("Ctrl + Alt + Space");
        ShortcutWarning = failures.Count == 0 ? "" : string.Join(" / ", failures) + " は他のアプリが使用中です。トレイから開けます。";
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(65) };
        debounce.Tick += async (_, _) => { debounce.Stop(); await ReadAsync(++generation); };
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey) { ShortcutPressed?.Invoke(wParam.ToInt32() == 2); handled = true; }
        if (msg == WmClipboardUpdate)
        {
            generation++;
            if (!Paused && GetClipboardSequenceNumber() != ownSequence) { debounce.Stop(); debounce.Start(); }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private async Task ReadAsync(int request)
    {
        for (int attempt = 0; attempt < 7 && !disposed && !Paused && generation == request; attempt++)
        {
            try
            {
                uint sequence = GetClipboardSequenceNumber();
                if (sequence == ownSequence) return;
                if (IsPrivateClipboard()) return;
                IDataObject? data = Clipboard.GetDataObject();
                if (data is null) return;
                if (data.GetDataPresent(OriginFormat, false) && Equals(data.GetData(OriginFormat, false), origin)) return;
                // File drops are intentionally excluded, even if they expose a text fallback.
                if (data.GetDataPresent(DataFormats.FileDrop, false)) return;
                if (data.GetDataPresent(DataFormats.UnicodeText, true))
                {
                    string? text = data.GetData(DataFormats.UnicodeText, true) as string;
                    if (sequence != GetClipboardSequenceNumber() || request != generation) return;
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (Encoding.UTF8.GetByteCount(text) > 1024 * 1024) Notice?.Invoke("1 MBを超える文字は共有しません。");
                        else TextCaptured?.Invoke(text);
                        return;
                    }
                }
                if (Clipboard.ContainsImage())
                {
                    BitmapSource? bitmap = Clipboard.GetImage();
                    if (bitmap is null) return;
                    if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 32_000_000) { Notice?.Invoke("画像が大きすぎるため共有しません（最大3,200万画素）。"); return; }
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = new MemoryStream(); encoder.Save(stream);
                    if (sequence != GetClipboardSequenceNumber() || request != generation) return;
                    if (stream.Length > 8 * 1024 * 1024) Notice?.Invoke("8 MBを超える画像は共有しません。");
                    else ImageCaptured?.Invoke(stream.ToArray());
                }
                return;
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException or IOException or System.Runtime.InteropServices.COMException)
            {
                if (attempt == 6) Notice?.Invoke("クリップボードを読み取れませんでした。もう一度コピーしてください。");
                else await Task.Delay(45 + attempt * 20);
            }
        }
    }

    // Treat malformed privacy flags conservatively. Never inspect payload content when an app opts out.
    private bool IsPrivateClipboard()
    {
        if (!OpenClipboard(source.Handle)) throw new ExternalException("Clipboard busy");
        try
        {
            uint exclude = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
            if (IsClipboardFormatAvailable(exclude)) return true;
            foreach (string name in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
            {
                uint format = RegisterClipboardFormat(name);
                if (!IsClipboardFormatAvailable(format)) continue;
                IntPtr handle = GetClipboardData(format);
                if (handle == IntPtr.Zero || GlobalSize(handle).ToUInt64() < 4) return true;
                IntPtr ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero) return true;
                try { if (Marshal.ReadInt32(ptr) != 1) return true; }
                finally { GlobalUnlock(handle); }
            }
            return false;
        }
        finally { CloseClipboard(); }
    }

    public async Task<bool> CopyAsync(ClipboardEntry entry, Func<bool>? canWrite = null)
    {
        var data = new DataObject();
        data.SetData(OriginFormat, origin);
        if (entry.Kind == ClipboardKind.Text) data.SetData(DataFormats.UnicodeText, entry.Text ?? "");
        else
        {
            using var stream = new MemoryStream(entry.ImagePng ?? throw new InvalidDataException("画像がありません。"));
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 32_000_000) throw new InvalidDataException("画像が大きすぎます。");
            data.SetImage(bitmap);
            data.SetData("PNG", new MemoryStream(entry.ImagePng!));
        }
        // Prevent Windows cloud clipboard from creating a second transport path for our writes.
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        for (int attempt = 0; attempt < 7 && !disposed; attempt++)
        {
            if (canWrite is not null && !canWrite()) return false;
            try
            {
                Clipboard.SetDataObject(data, true);
                ownSequence = GetClipboardSequenceNumber();
                generation++;
                return true;
            }
            catch (ExternalException) when (attempt < 6) { await Task.Delay(40 + attempt * 25); }
        }
        return false;
    }

    public static uint CurrentSequence => GetClipboardSequenceNumber();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; generation++; debounce.Stop();
        RemoveClipboardFormatListener(source.Handle); UnregisterHotKey(source.Handle, 1); UnregisterHotKey(source.Handle, 2);
        source.RemoveHook(Hook); source.Dispose();
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
}
