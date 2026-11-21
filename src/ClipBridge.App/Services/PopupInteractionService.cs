using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ClipBridge.App.Services;

/// <summary>Native popup placement, clean double-Control detection, and guarded paste-back.</summary>
public sealed class PopupInteractionService : IDisposable
{
    private const int WhKeyboardLl = 13, WhMouseLl = 14;
    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const uint WmTimer = 0x0113, WmRecoverInput = 0x8001, WmCheckInputHealth = 0x8002;
    private const int VkControl = 0x11, VkLControl = 0xA2, VkRControl = 0xA3;
    private const uint LlkhfInjected = 0x10, LlkhfLowerInjected = 0x02, LlkhfExtended = 0x01;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint SwpNoActivate = 0x0010, SwpNoZorder = 0x0004, SwpNoSize = 0x0001;
    private readonly Dispatcher _dispatcher;
    private readonly HookProc _hookCallback;
    private readonly HookProc _mouseHookCallback;
    private readonly Thread _hookThread;
    private readonly ManualResetEventSlim _hookReady = new(false);
    private readonly ControlTapGesture _gesture = new();
    private readonly Func<int, bool> _keyIsDown;
    private readonly Func<long> _clock;
    private readonly InputHealthMonitor _health;
    private IntPtr _hook, _mouseHook;
    private UIntPtr _healthTimer;
    private uint _hookThreadId;
    private volatile bool _disposed;
    private volatile bool _doubleControlEnabled = true;
    private volatile bool _outsideClickDismissalEnabled;
    private int _pointerMonitorVersion;
    private int _gestureResetVersion, _lastGestureResetVersion;
    private int _recoveryRequested, _recoveryCount, _inputGeneration;
    private long _lastProbeAt;
    private IntPtr _targetWindow, _targetFocus;
    private uint _targetProcessId, _targetThreadId;
    private NativePoint _anchor;
    private bool _hasAnchor;
    private int _pasteInProgress;

    public PopupInteractionService(Dispatcher dispatcher)
        : this(dispatcher, key => (GetAsyncKeyState(key) & 0x8000) != 0, () => Environment.TickCount64)
    {
    }

    internal PopupInteractionService(Dispatcher dispatcher, Func<int, bool> keyIsDown, Func<long> clock)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _keyIsDown = keyIsDown ?? throw new ArgumentNullException(nameof(keyIsDown));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _health = new InputHealthMonitor(_clock());
        _hookCallback = KeyboardHook;
        _mouseHookCallback = MouseHook;
        _hookThread = new Thread(HookThreadMain) { IsBackground = true, Name = "ClipBridge Ctrl gesture" };
        _hookThread.Start();
        _hookReady.Wait(TimeSpan.FromSeconds(1));
    }

    public event Action? DoubleControlPressed;
    public event Action? OutsidePointerPressed;
    // Assign on every presentation, including a visible re-open, to invalidate older clicks.
    public bool OutsideClickDismissalEnabled
    {
        get => _outsideClickDismissalEnabled;
        set { _outsideClickDismissalEnabled = value; Interlocked.Increment(ref _pointerMonitorVersion); }
    }
    public bool DoubleControlEnabled
    {
        get => _doubleControlEnabled;
        set
        {
            if (_doubleControlEnabled == value) return;
            _doubleControlEnabled = value;
            Interlocked.Increment(ref _gestureResetVersion);
            if (value) RequestInputRecovery();
        }
    }
    public bool HookAvailable => !_disposed && _hook != IntPtr.Zero;
    public bool OutsideClickHookAvailable => !_disposed && _mouseHook != IntPtr.Zero;
    internal int RecoveryCount => Volatile.Read(ref _recoveryCount);

    /// <summary>Repairs native input monitoring on its worker without opening the picker.</summary>
    public void RequestInputRecovery()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _recoveryRequested, 1);
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WmRecoverInput, UIntPtr.Zero, IntPtr.Zero);
    }

    // A queued worker operation lets native tests advance the injected clock and key state.
    internal void CheckInputHealth()
    {
        if (!_disposed && _hookThreadId != 0)
            PostThreadMessage(_hookThreadId, WmCheckInputHealth, UIntPtr.Zero, IntPtr.Zero);
    }

    private void HookThreadMain()
    {
        try
        {
            _hookThreadId = GetCurrentThreadId();
            // Creating the queue first also makes Dispose/PostThreadMessage reliable.
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            RegisterInputHooks(recovery: false);
            _healthTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 100, IntPtr.Zero);
            _hookReady.Set();
            if (_disposed) return;
            if (Interlocked.Exchange(ref _recoveryRequested, 0) != 0) RegisterInputHooks(recovery: true);
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (_disposed) break;
                if (message.Message == WmRecoverInput)
                {
                    try
                    {
                        if (Interlocked.Exchange(ref _recoveryRequested, 0) != 0) RegisterInputHooks(recovery: true);
                    }
                    catch { _gesture.ResetSequence(); }
                    continue;
                }
                if (message.Message == WmCheckInputHealth ||
                    message.Message == WmTimer && message.WParam == _healthTimer)
                {
                    try { ProbeInputHealth(); }
                    catch { _gesture.ResetSequence(); }
                    continue;
                }
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch
        {
            // RegisterHotKey remains available if a hook is unavailable in this session.
        }
        finally
        {
            if (_healthTimer != UIntPtr.Zero) KillTimer(IntPtr.Zero, _healthTimer);
            _healthTimer = UIntPtr.Zero;
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            _hook = IntPtr.Zero;
            _mouseHook = IntPtr.Zero;
            _hookReady.Set();
        }
    }

    private void RegisterInputHooks(bool recovery)
    {
        if (_disposed) return;
        // Only the hook worker changes registrations. A timed-out low-level hook can leave
        // a nonzero stored handle, so a repair always replaces both registrations.
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _hook = _mouseHook = IntPtr.Zero;
        Interlocked.Increment(ref _inputGeneration);
        _gesture.ClearHeldKeys();
        for (int key = 8; key <= 0xE2; key++)
            if (IsPhysicalKeyboardKey(key) && _keyIsDown(key)) _gesture.SeedHeldKey(key);
        _hook = SetWindowsHookEx(WhKeyboardLl, _hookCallback, GetModuleHandle(null), 0);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseHookCallback, GetModuleHandle(null), 0);
        long now = _clock();
        _lastProbeAt = now;
        _health.Registered(now, _keyIsDown(VkLControl) || _keyIsDown(VkRControl), recovery);
        if (recovery) Interlocked.Increment(ref _recoveryCount);
    }

    private void ProbeInputHealth()
    {
        if (_disposed) return;
        long now = _clock();
        // A probe is useful only when monotonic time advances. In particular, repeated
        // timer messages cannot mutate a recognizer tied to an unchanged test clock.
        if (now == _lastProbeAt) return;
        _lastProbeAt = now;
        _gesture.ReconcileReleasedKeys(-1, _keyIsDown);
        bool controlDown = _keyIsDown(VkLControl) || _keyIsDown(VkRControl);
        if (_health.ShouldRecover(now, controlDown, _doubleControlEnabled, _disposed))
            RegisterInputHooks(recovery: true);
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            uint message = unchecked((uint)wParam.ToInt64());
            if (code >= 0 && !_disposed && message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
            {
                var data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
                int key = (int)data.VirtualKey;
                if (key == VkControl) key = (data.Flags & LlkhfExtended) != 0 ? VkRControl : VkLControl;
                bool injected = (data.Flags & (LlkhfInjected | LlkhfLowerInjected)) != 0;
                bool down = message is WmKeyDown or WmSysKeyDown;
                int resetVersion = Volatile.Read(ref _gestureResetVersion);
                if (resetVersion != _lastGestureResetVersion)
                {
                    _gesture.ResetSequence();
                    _lastGestureResetVersion = resetVersion;
                }
                if (!injected)
                {
                    // The current key's asynchronous state is still its pre-event state.
                    // Reconcile only other keys, including releases missed across desktops.
                    _gesture.ReconcileReleasedKeys(key, _keyIsDown);
                    if (down && key is VkLControl or VkRControl)
                    {
                        // On a down notification, an already-tracked Ctrl with an UP
                        // pre-event state proves a missed release, rather than auto-repeat.
                        _gesture.ReconcileCurrentPress(key, _keyIsDown);
                        _gesture.ObserveOtherHeldKeys(key, _keyIsDown);
                    }
                    if (IsPhysicalKeyboardKey(key)) _health.KeyboardObserved(_clock());
                }
                if (_gesture.Process(key, down, data.Time, injected, _doubleControlEnabled))
                {
                    // No UI, clipboard work, or waits are performed in the hook callback.
                    IntPtr foreground = GetForegroundWindow();
                    int generation = Volatile.Read(ref _inputGeneration);
                    // Keep earlier Normal-priority data updates ahead of this request,
                    // while allowing presentation to precede pending binding/render work.
                    _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        if (_disposed || !_doubleControlEnabled || GetForegroundWindow() != foreground ||
                            Volatile.Read(ref _inputGeneration) != generation ||
                            Volatile.Read(ref _gestureResetVersion) != resetVersion) return;
                        // The host guards modal editors before capturing or refreshing the target.
                        DoubleControlPressed?.Invoke();
                    }));
                }
            }
        }
        catch
        {
            // Exceptions must never cross the unmanaged callback boundary.
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            uint message = unchecked((uint)wParam.ToInt64());
            if (code >= 0 && !_disposed && _outsideClickDismissalEnabled &&
                message is 0x0201 or 0x0204 or 0x0207 or 0x020B)
            {
                int version = Volatile.Read(ref _pointerMonitorVersion);
                var data = Marshal.PtrToStructure<MouseHookData>(lParam);
                // Inspect before dispatch: a menu/dropdown may close before the UI gets this
                // notification. Its HWND still belongs to this app at the moment of the click.
                GetWindowThreadProcessId(WindowFromPoint(data.Point), out uint process);
                if (process != (uint)Environment.ProcessId)
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        if (_disposed || !_outsideClickDismissalEnabled ||
                            Volatile.Read(ref _pointerMonitorVersion) != version) return;
                        OutsidePointerPressed?.Invoke();
                    }));
                }
            }
        }
        catch
        {
            // Closing a picker must never interrupt normal mouse input.
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    /// <summary>Call immediately before showing the popup. Never records this app as a paste target.</summary>
    public void CaptureTarget(bool preserveCurrentTarget = false)
    {
        _dispatcher.VerifyAccess();
        IntPtr foreground = GetForegroundWindow();
        uint threadId = GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == (uint)Environment.ProcessId && preserveCurrentTarget)
        {
            // Reopening the active picker moves it to the current pointer, while retaining
            // the original external input control. A stale target is never resurrected.
            if (!IsCapturedTargetValid(_targetWindow, _targetProcessId, _targetThreadId)) ClearTarget();
            _hasAnchor = GetCursorPos(out _anchor);
            return;
        }
        if (foreground == IntPtr.Zero || processId == (uint)Environment.ProcessId || threadId == 0 || IsShellSurface(foreground))
        {
            ClearTarget();
            return;
        }
        _targetWindow = foreground;
        _targetProcessId = processId;
        _targetThreadId = threadId;
        _targetFocus = IntPtr.Zero;
        _hasAnchor = false;
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (GetGUIThreadInfo(threadId, ref info))
        {
            _targetFocus = info.Focus;
            if (info.Caret != IntPtr.Zero && IsWindow(info.Caret))
            {
                var caretPoint = new NativePoint { X = info.CaretRect.Left, Y = info.CaretRect.Bottom };
                if (ClientToScreen(info.Caret, ref caretPoint))
                {
                    _anchor = caretPoint;
                    _hasAnchor = true;
                }
            }
        }
        if (!_hasAnchor) _hasAnchor = GetCursorPos(out _anchor);
    }

    /// <summary>Places the popup near the captured caret or mouse, inside that monitor's work area.</summary>
    public void PlacePopup(Window window, Action<double>? fitAvailableHeight = null, bool preserveCurrentPosition = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        _dispatcher.VerifyAccess();
        if (preserveCurrentPosition && window.IsVisible)
        {
            IntPtr existingHandle = new WindowInteropHelper(window).EnsureHandle();
            if (!GetWindowRect(existingHandle, out var current)) return;
            var workArea = Forms.Screen.FromHandle(existingHandle).WorkingArea;
            double currentScale = Math.Max(96, GetDpiForWindow(existingHandle)) / 96d;
            var currentRectangle = new System.Drawing.Rectangle(current.Left, current.Top, current.Right - current.Left, current.Bottom - current.Top);
            var origin = ResizePopupBounds(currentRectangle, workArea, new System.Drawing.Size(1, 1));
            fitAvailableHeight?.Invoke(Math.Max(1, workArea.Bottom - 8 - origin.Top) / currentScale);
            window.UpdateLayout();
            int widthPixels = (int)Math.Ceiling((double.IsFinite(window.Width) ? window.Width : window.ActualWidth) * currentScale);
            int heightPixels = (int)Math.Ceiling((double.IsFinite(window.Height) ? window.Height : window.ActualHeight) * currentScale);
            var resized = ResizePopupBounds(currentRectangle, workArea, new System.Drawing.Size(widthPixels, heightPixels));
            SetWindowPos(existingHandle, IntPtr.Zero, resized.X, resized.Y, resized.Width, resized.Height, SwpNoActivate | SwpNoZorder);
            return;
        }
        if (!_hasAnchor && !GetCursorPos(out _anchor)) return;
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(_anchor.X, _anchor.Y));
        var area = screen.WorkingArea;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        // Moving the HWND first delivers WM_DPICHANGED before converting its WPF size to pixels.
        // Global desktop DIPs do not share one scale on monitors with different DPI settings.
        SetWindowPos(hwnd, IntPtr.Zero, _anchor.X, _anchor.Y, 0, 0, SwpNoActivate | SwpNoZorder | SwpNoSize);
        window.UpdateLayout();
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi > 0 ? dpi / 96d : 1d;
        fitAvailableHeight?.Invoke(Math.Max(1, area.Height - 16) / scale);
        window.UpdateLayout();
        double width = window.ActualWidth > 0 ? window.ActualWidth : double.IsFinite(window.Width) ? window.Width : 440;
        double height = double.IsFinite(window.Height) ? window.Height : window.ActualHeight > 0 ? window.ActualHeight : 520;
        int pixelWidth = Math.Min((int)Math.Ceiling(width * scale), Math.Max(1, area.Width - 16));
        int pixelHeight = Math.Min((int)Math.Ceiling(height * scale), Math.Max(1, area.Height - 16));
        int x = Math.Clamp(_anchor.X + 6, area.Left + 8, Math.Max(area.Left + 8, area.Right - pixelWidth - 8));
        int y = _anchor.Y + 8;
        if (y + pixelHeight > area.Bottom - 8) y = _anchor.Y - pixelHeight - 8;
        y = Math.Clamp(y, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - pixelHeight - 8));
        SetWindowPos(hwnd, IntPtr.Zero, x, y, pixelWidth, pixelHeight, SwpNoActivate | SwpNoZorder);
    }

    // Resizing keeps the current top edge and uses the remaining space below it.
    internal static System.Drawing.Rectangle ResizePopupBounds(System.Drawing.Rectangle current, System.Drawing.Rectangle area, System.Drawing.Size requested)
    {
        int left = Math.Clamp(current.Left, area.Left + 8, Math.Max(area.Left + 8, area.Right - 9));
        int top = Math.Clamp(current.Top, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - 9));
        int width = Math.Clamp(requested.Width, 1, Math.Max(1, area.Width - 16));
        left = Math.Min(left, Math.Max(area.Left + 8, area.Right - width - 8));
        int height = Math.Clamp(requested.Height, 1, Math.Max(1, area.Bottom - top - 8));
        return new System.Drawing.Rectangle(left, top, width, height);
    }

    /// <summary>Call only after the selected item was successfully copied to the clipboard.</summary>
    /// <returns>Null on success/copy-only; otherwise a message explaining manual paste fallback.</returns>
    public async Task<string?> PasteToTargetAsync(Action hidePopup, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hidePopup);
        _dispatcher.VerifyAccess();
        if (Interlocked.Exchange(ref _pasteInProgress, 1) != 0) return "コピー済みです。貼り付け処理が完了してから操作してください。";
        uint clipboardSequence = enabled ? GetClipboardSequenceNumber() : 0;
        try
        {
            IntPtr target = _targetWindow;
            uint process = _targetProcessId, thread = _targetThreadId;
            IntPtr focus = _targetFocus;
            // Keep focus on the popup until the selecting Enter/digit and user modifiers are up.
            // Hiding it first could deliver physical Enter auto-repeat to the destination.
            long deadline = Environment.TickCount64 + 1800;
            while (AnyKeyboardKeyDown())
            {
                if (cancellationToken.IsCancellationRequested || _disposed || Environment.TickCount64 > deadline)
                {
                    hidePopup();
                    return "コピーしました。キーを離してから、Ctrl+V で貼り付けてください。";
                }
                if (!ForegroundIsPopupOrTarget(target))
                {
                    hidePopup();
                    return "コピーしました。入力先が切り替わったため、Ctrl+V で貼り付けてください。";
                }
                await Task.Delay(15, cancellationToken);
            }
            if (!enabled) { hidePopup(); return null; }
            if (!IsCapturedTargetValid(target, process, thread))
            {
                hidePopup();
                return "コピーしました。元の入力先が見つからないため、Ctrl+V で貼り付けてください。";
            }
            if (cancellationToken.IsCancellationRequested || _disposed || !ForegroundIsPopupOrTarget(target))
            {
                hidePopup();
                return "コピーしました。入力先を確認し、Ctrl+V で貼り付けてください。";
            }
            hidePopup();
            if (!IsCapturedTargetValid(target, process, thread))
                return "コピーしました。元の入力先が閉じられたため、Ctrl+V で貼り付けてください。";
            SetForegroundWindow(target);
            await Task.Delay(40, cancellationToken);
            // Check the HWND, process/thread identity, focused control and keyboard immediately
            // before SendInput. Never redirect to whatever happens to be in the foreground.
            if (!IsCapturedTargetValid(target, process, thread) || GetForegroundWindow() != target || AnyKeyboardKeyDown())
                return "コピーしました。元の入力先を復元できなかったため、Ctrl+V で貼り付けてください。";
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            if (focus != IntPtr.Zero && (!GetGUIThreadInfo(thread, ref info) || info.Focus != focus))
                return "コピーしました。入力欄が切り替わったため、Ctrl+V で貼り付けてください。";
            if (GetForegroundWindow() != target || cancellationToken.IsCancellationRequested)
                return "コピーしました。入力先を確認し、Ctrl+V で貼り付けてください。";
            var inputs = new[] { KeyInput(VkLControl, false), KeyInput(0x56, false), KeyInput(0x56, true), KeyInput(VkLControl, true) };
            if (GetClipboardSequenceNumber() != clipboardSequence)
                return "クリップボードが更新されたため、自動貼り付けを中止しました。項目を選び直してください。";
            if (GetForegroundWindow() != target)
                return "コピーしました。入力先が切り替わったため、Ctrl+V で貼り付けてください。";
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
            if (sent == inputs.Length) return null;
            // If Windows accepted only a prefix, release every key that prefix pressed.
            // No Enter is synthesized, so choosing a row cannot submit a message/form.
            if (sent > 0)
            {
                var release = sent >= 2 ? new[] { KeyInput(0x56, true), KeyInput(VkLControl, true) } : new[] { KeyInput(VkLControl, true) };
                SendInput((uint)release.Length, release, Marshal.SizeOf<NativeInput>());
            }
            return "コピーしました。自動貼り付けが許可されなかったため、Ctrl+V で貼り付けてください。";
        }
        catch (OperationCanceledException)
        {
            hidePopup();
            return "コピーしました。Ctrl+V で貼り付けてください。";
        }
        finally { Volatile.Write(ref _pasteInProgress, 0); }
    }

    private static bool AnyKeyboardKeyDown()
    {
        return HasPhysicalKeysDown(key => (GetAsyncKeyState(key) & 0x8000) != 0);
    }

    // IME processing/status values (notably 0xF0..0xF6) can remain logically down
    // without a physically held key. Keep ordinary keys and modifiers guarded.
    internal static bool IsPhysicalKeyboardKey(int key) =>
        key is 0x08 or 0x09 or 0x0C or 0x0D or 0x15 or 0x19 or 0x1B or 0x1C or 0x1D or 0x5F or 0x90 or 0x91 or 0xE1 or 0xE2 ||
        key is >= 0x10 and <= 0x14 || key is >= 0x20 and <= 0x28 || key is >= 0x2C and <= 0x39 ||
        key is >= 0x41 and <= 0x5D || key is >= 0x60 and <= 0x87 || key is >= 0x92 and <= 0x96 ||
        key is >= 0xA0 and <= 0xB7 || key is >= 0xBA and <= 0xC2 || key is >= 0xDB and <= 0xDF;

    internal static bool HasPhysicalKeysDown(Func<int, bool> isKeyDown)
    {
        ArgumentNullException.ThrowIfNull(isKeyDown);
        for (int key = 8; key <= 0xE2; key++)
            if (IsPhysicalKeyboardKey(key) && isKeyDown(key)) return true;
        return false;
    }

    private static bool ForegroundIsPopupOrTarget(IntPtr target)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == target) return true;
        GetWindowThreadProcessId(foreground, out uint process);
        return process == (uint)Environment.ProcessId;
    }

    private static bool IsCapturedTargetValid(IntPtr hwnd, uint process, uint thread)
    {
        if (hwnd == IntPtr.Zero || process == 0 || process == (uint)Environment.ProcessId || !IsWindow(hwnd)) return false;
        return GetWindowThreadProcessId(hwnd, out uint currentProcess) == thread && currentProcess == process;
    }

    private static bool IsShellSurface(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW";
    }

    private void ClearTarget()
    {
        _targetWindow = _targetFocus = IntPtr.Zero;
        _targetProcessId = _targetThreadId = 0;
        _hasAnchor = false;
    }

    private static NativeInput KeyInput(int key, bool up) => new()
    {
        Type = 1,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)key, Flags = up ? KeyeventfKeyup : 0 } }
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, 0x0012, UIntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _hookThread) _hookThread.Join(1000);
        // Do not dispose the ready signal while a slow-starting hook thread may still set it.
        GC.KeepAlive(_hookCallback);
        GC.KeepAlive(_mouseHookCallback);
    }

    /// <summary>Deterministic checks with no hooks, clipboard access, activation or simulated input.</summary>
    public static IReadOnlyList<string> RunGestureChecks()
    {
        var failures = new List<string>();
        void Check(string name, bool condition) { if (!condition) failures.Add(name); }
        static bool Feed(ControlTapGesture gesture, params (int Key, bool Down, uint Time, bool Injected)[] events)
        {
            bool fired = false;
            foreach (var item in events) fired |= gesture.Process(item.Key, item.Down, item.Time, item.Injected, true);
            return fired;
        }
        static (int, bool, uint, bool) D(int key, uint time) => (key, true, time, false);
        static (int, bool, uint, bool) U(int key, uint time) => (key, false, time, false);
        Check("left Ctrl double tap", Feed(new(), D(VkLControl, 10), U(VkLControl, 40), D(VkLControl, 100), U(VkLControl, 130)));
        Check("right Ctrl double tap", Feed(new(), D(VkRControl, 10), U(VkRControl, 40), D(VkRControl, 100), U(VkRControl, 130)));
        Check("alternating Ctrl taps", Feed(new(), D(VkLControl, 10), U(VkLControl, 40), D(VkRControl, 100), U(VkRControl, 130)));
        Check("single Ctrl", !Feed(new(), D(VkLControl, 10), U(VkLControl, 40)));
        Check("Ctrl+C twice", !Feed(new(), D(VkLControl, 10), D(0x43, 20), U(0x43, 30), U(VkLControl, 40), D(VkLControl, 100), D(0x43, 110), U(0x43, 120), U(VkLControl, 130)));
        Check("chord breaks previous tap", !Feed(new(), D(VkLControl, 10), U(VkLControl, 40), D(VkLControl, 70), D(0x43, 80), U(0x43, 90), U(VkLControl, 100), D(VkLControl, 130), U(VkLControl, 150)));
        Check("typing between taps", !Feed(new(), D(VkLControl, 10), U(VkLControl, 40), D(0x41, 60), U(0x41, 70), D(VkLControl, 100), U(VkLControl, 130)));
        Check("Alt Ctrl chord", !Feed(new(), D(0xA4, 1), D(VkLControl, 10), U(VkLControl, 40), D(VkLControl, 100), U(VkLControl, 130), U(0xA4, 150)));
        Check("Shift Ctrl chord", !Feed(new(), D(0xA0, 1), D(VkLControl, 10), U(VkLControl, 40), D(VkLControl, 100), U(VkLControl, 130)));
        Check("two held Ctrl keys", !Feed(new(), D(VkLControl, 10), D(VkRControl, 20), U(VkLControl, 40), U(VkRControl, 60)));
        Check("Ctrl repeat is not a tap", !Feed(new(), D(VkLControl, 10), D(VkLControl, 40), D(VkLControl, 70), U(VkLControl, 100)));
        Check("long held Ctrl", !Feed(new(), D(VkLControl, 10), U(VkLControl, 500), D(VkLControl, 550), U(VkLControl, 580)));
        Check("slow second tap", !Feed(new(), D(VkLControl, 10), U(VkLControl, 40), D(VkLControl, 600), U(VkLControl, 630)));
        Check("injected taps ignored", !Feed(new(), (VkLControl, true, 10, true), (VkLControl, false, 40, true), (VkLControl, true, 100, true), (VkLControl, false, 130, true)));
        Check("injected copy does not trigger", !Feed(new(), (VkLControl, true, 10, true), (0x43, true, 20, true), (0x43, false, 30, true), (VkLControl, false, 40, true)));
        Check("timestamp wraps", Feed(new(), D(VkLControl, uint.MaxValue - 100), U(VkLControl, uint.MaxValue - 70), D(VkLControl, 20), U(VkLControl, 50)));
        var seeded = new ControlTapGesture(); seeded.SeedHeldKey(0xA4);
        Check("modifier held at startup", !Feed(seeded, D(VkLControl, 10), U(VkLControl, 40), D(VkLControl, 100), U(VkLControl, 130)));
        var imeStatus = new ControlTapGesture(); imeStatus.SeedHeldKey(0xF4);
        Check("Japanese IME status does not poison startup", Feed(imeStatus, D(VkLControl, 10), U(VkLControl, 40), D(VkLControl, 100), U(VkLControl, 130)));
        var aliases = new ControlTapGesture(); aliases.SeedHeldKey(VkControl); aliases.SeedHeldKey(0x10); aliases.SeedHeldKey(0x12);
        Check("generic modifier aliases do not poison startup", Feed(aliases, D(VkLControl, 10), U(VkLControl, 40), D(VkLControl, 100), U(VkLControl, 130)));
        var releasedModifier = new ControlTapGesture(); releasedModifier.SeedHeldKey(0xA4); releasedModifier.SeedHeldKey(0xF4);
        Check("real startup modifier release restores gesture", Feed(releasedModifier, U(0xA4, 1), D(VkLControl, 10), U(VkLControl, 40), D(VkLControl, 100), U(VkLControl, 130)));
        var disabled = new ControlTapGesture();
        disabled.Process(VkLControl, true, 10, false, false); disabled.Process(VkLControl, false, 40, false, false);
        Check("disabled gesture resets sequence", !Feed(disabled, D(VkLControl, 100), U(VkLControl, 130)));
        var triples = new ControlTapGesture();
        Feed(triples, D(VkLControl, 10), U(VkLControl, 40), D(VkLControl, 100), U(VkLControl, 130));
        Check("third tap does not repeat", !Feed(triples, D(VkLControl, 160), U(VkLControl, 190)));
        Check("INPUT native layout", Marshal.SizeOf<NativeInput>() == (IntPtr.Size == 8 ? 40 : 28));
        Check("hook native layout", Marshal.SizeOf<KeyboardHookData>() == (IntPtr.Size == 8 ? 24 : 20));
        Check("GUI native layout", Marshal.SizeOf<GuiThreadInfo>() == (IntPtr.Size == 8 ? 72 : 48));
        var pasteKeys = new[] { KeyInput(VkLControl, false), KeyInput(0x56, false), KeyInput(0x56, true), KeyInput(VkLControl, true) };
        Check("paste key order", pasteKeys[0].Data.Keyboard.VirtualKey == VkLControl && pasteKeys[0].Data.Keyboard.Flags == 0
            && pasteKeys[1].Data.Keyboard.VirtualKey == 0x56 && pasteKeys[1].Data.Keyboard.Flags == 0
            && pasteKeys[2].Data.Keyboard.VirtualKey == 0x56 && pasteKeys[2].Data.Keyboard.Flags == KeyeventfKeyup
            && pasteKeys[3].Data.Keyboard.VirtualKey == VkLControl && pasteKeys[3].Data.Keyboard.Flags == KeyeventfKeyup);
        Check("paste never sends Enter", Array.TrueForAll(pasteKeys, item => item.Type == 1 && item.Data.Keyboard.VirtualKey != 0x0D));
        return failures;
    }

    public static int GestureCheckCount => 27;

    /// <summary>Paste release-gate checks. No native key reads, clipboard or input injection.</summary>
    public static IReadOnlyList<string> RunPasteChecks()
    {
        var failures = new List<string>();
        void Check(string name, bool condition) { if (!condition) failures.Add(name); }
        static bool Held(params int[] keys) => HasPhysicalKeysDown(key => Array.IndexOf(keys, key) >= 0);
        static bool EveryHeld(int first, int last)
        {
            for (int key = first; key <= last; key++) if (!Held(key)) return false;
            return true;
        }
        Check("idle gate is released", !Held());
        Check("persistent IME 0xF4 does not delay paste", !Held(0xF4));
        Check("IME 0xF0 through 0xF6 do not delay paste", !Held(0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6));
        Check("IME processing and compatibility status keys ignored", !Held(0x16, 0x17, 0x18, 0x1E, 0x1F, 0xE5, 0xE7));
        Check("mouse buttons are not keyboard holds", !Held(0x01, 0x02, 0x04, 0x05, 0x06));
        Check("physical Enter keeps release guard", Held(0x0D));
        Check("physical number keys keep release guard", EveryHeld(0x30, 0x39));
        Check("physical letter keys keep release guard", EveryHeld(0x41, 0x5A));
        Check("numpad keys keep release guard", EveryHeld(0x60, 0x6F));
        Check("generic and sided modifiers keep release guard", Array.TrueForAll(new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }, key => Held(key)));
        Check("physical F4 is distinct from IME 0xF4", Held(0x73));
        Check("physical F1 through F24 keep release guard", EveryHeld(0x70, 0x87));
        Check("navigation and editing keys keep release guard", Array.TrueForAll(new[] { 0x08, 0x09, 0x1B, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E }, key => Held(key)));
        Check("JIS and OEM physical keys keep release guard", Array.TrueForAll(new[] { 0x15, 0x19, 0x1C, 0x1D, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF, 0xE2 }, key => Held(key)));
        Check("IME status cannot hide a held Enter", Held(0xF4, 0x0D));
        Check("IME status cannot hide held Ctrl", Held(0xF4, VkLControl));
        bool enterHeld = true;
        bool beforeRelease = HasPhysicalKeysDown(key => key == 0xF4 || key == 0x0D && enterHeld);
        enterHeld = false;
        Check("releasing physical key unblocks even with IME status held", beforeRelease && !HasPhysicalKeysDown(key => key == 0xF4 || key == 0x0D && enterHeld));
        bool queriedStatus = false;
        HasPhysicalKeysDown(key => { if (key is >= 0xF0 and <= 0xF6 or 0xE5 or 0xE7) queriedStatus = true; return false; });
        Check("IME status values are not queried by physical gate", !queriedStatus);
        return failures;
    }

    public static int PasteCheckCount => 18;

    /// <summary>Input recovery checks with synthetic key state and a monotonic test clock.</summary>
    public static IReadOnlyList<string> RunRecoveryChecks()
    {
        var failures = new List<string>();
        void Check(string name, bool passed) { if (!passed) failures.Add(name); }
        static bool DoubleTap(ControlTapGesture gesture, uint time = 10)
        {
            bool fired = false;
            fired |= gesture.Process(VkLControl, true, time, false, true);
            fired |= gesture.Process(VkLControl, false, time + 30, false, true);
            fired |= gesture.Process(VkLControl, true, time + 90, false, true);
            fired |= gesture.Process(VkLControl, false, time + 120, false, true);
            return fired;
        }
        foreach (int key in new[] { 0xF4, 0xE5, 0xE7, 0xFF })
        {
            var gesture = new ControlTapGesture();
            gesture.Process(key, true, 1, false, true);
            Check("runtime nonphysical key cannot poison Ctrl: " + key, DoubleTap(gesture));
        }
        var missedLetter = new ControlTapGesture();
        missedLetter.Process(0x41, true, 1, false, true);
        missedLetter.ReconcileReleasedKeys(VkLControl, _ => false);
        Check("missed ordinary key-up recovers before Ctrl", DoubleTap(missedLetter));
        var missedShift = new ControlTapGesture();
        missedShift.Process(0xA0, true, 1, false, true);
        missedShift.ReconcileReleasedKeys(VkLControl, _ => false);
        Check("missed Shift key-up recovers before Ctrl", DoubleTap(missedShift));
        var missedControl = new ControlTapGesture();
        missedControl.Process(VkLControl, true, 1, false, true);
        missedControl.ReconcileReleasedKeys(-1, _ => false);
        Check("idle reconciliation recovers missed Ctrl key-up", DoubleTap(missedControl));
        var heldEnter = new ControlTapGesture();
        heldEnter.SeedHeldKey(0x0D);
        heldEnter.ReconcileReleasedKeys(VkLControl, key => key == 0x0D);
        Check("actual held Enter still blocks clean Ctrl", !DoubleTap(heldEnter));
        var heldShift = new ControlTapGesture();
        heldShift.SeedHeldKey(0xA0);
        heldShift.ReconcileReleasedKeys(VkLControl, key => key == 0xA0);
        Check("actual held Shift still blocks clean Ctrl", !DoubleTap(heldShift));
        var currentKey = new ControlTapGesture();
        currentKey.Process(VkLControl, true, 1, false, true);
        currentKey.ReconcileReleasedKeys(VkLControl, _ => false);
        Check("current event is preserved until its key-up", !DoubleTap(currentKey, 300));
        var missingCurrentRelease = new ControlTapGesture();
        missingCurrentRelease.Process(VkLControl, true, 1, false, true);
        missingCurrentRelease.ReconcileCurrentPress(VkLControl, _ => false);
        Check("Ctrl down pre-state recovers its missed prior release", DoubleTap(missingCurrentRelease));
        var actualRepeat = new ControlTapGesture();
        actualRepeat.Process(VkLControl, true, 1, false, true);
        actualRepeat.ReconcileCurrentPress(VkLControl, _ => true);
        Check("Ctrl down pre-state keeps genuine repeat guarded", !DoubleTap(actualRepeat, 300));
        var unseenChord = new ControlTapGesture();
        unseenChord.ObserveOtherHeldKeys(VkLControl, key => key == 0x43);
        Check("a missed other-key down still guards the chord", !DoubleTap(unseenChord));
        var statusSnapshot = new ControlTapGesture();
        statusSnapshot.ObserveOtherHeldKeys(VkLControl, key => key == 0xF4 || key == VkControl);
        Check("held-state snapshot excludes IME and generic aliases", DoubleTap(statusSnapshot));
        var reset = new ControlTapGesture();
        reset.SeedHeldKey(0xA0);
        reset.ClearHeldKeys();
        Check("recovery clears stale held state", DoubleTap(reset));
        var partial = new ControlTapGesture();
        partial.Process(VkLControl, true, 10, false, true);
        partial.Process(VkLControl, false, 40, false, true);
        partial.ClearHeldKeys();
        partial.Process(VkLControl, true, 100, false, true);
        Check("recovery cannot complete an old first tap", !partial.Process(VkLControl, false, 130, false, true));
        var seededEnter = new ControlTapGesture(); seededEnter.SeedHeldKey(0x0D);
        Check("recovery seeding protects an already-held text key", !DoubleTap(seededEnter));

        var idle = new InputHealthMonitor(0);
        Check("zero clock is a valid initial health timestamp", !idle.ShouldRecover(0, false, true, false));
        Check("ordinary idle does not renew hooks", !idle.ShouldRecover(2000, false, true, false));
        Check("new Ctrl without callbacks requests repair", idle.ShouldRecover(2100, true, true, false));
        idle.Registered(2100, true, recovery: true);
        Check("a long-held Ctrl does not repeatedly repair", !idle.ShouldRecover(3500, true, true, false));
        idle.ShouldRecover(3600, false, true, false);
        Check("a later distinct Ctrl can repair again", idle.ShouldRecover(3700, true, true, false));
        var recent = new InputHealthMonitor(0);
        recent.KeyboardObserved(1900);
        Check("recent real callback prevents false repair", !recent.ShouldRecover(2000, true, true, false));
        var disabled = new InputHealthMonitor(0);
        Check("disabled Ctrl gesture does not request Ctrl repair", !disabled.ShouldRecover(2000, true, false, false));
        var stopped = new InputHealthMonitor(0);
        Check("disposed monitor cannot repair on Ctrl", !stopped.ShouldRecover(2000, true, true, true));
        Check("disposed monitor cannot repair on resume", !stopped.ShouldRecover(10000, false, true, true));
        var cooldown = new InputHealthMonitor(0);
        cooldown.Registered(1000, false, recovery: true);
        Check("repair cooldown suppresses a rapid repeat", !cooldown.ShouldRecover(1600, true, true, false));
        cooldown.ShouldRecover(1700, false, true, false);
        Check("repair cooldown expires for a new press", cooldown.ShouldRecover(2100, true, true, false));
        var resume = new InputHealthMonitor(0);
        Check("long timer gap requests resume repair", resume.ShouldRecover(6000, false, true, false));
        resume.Registered(6000, false, recovery: true);
        Check("resume repair is not repeated on normal ticks", !resume.ShouldRecover(6100, false, true, false));
        var mouseResume = new InputHealthMonitor(0);
        Check("resume also repairs mouse monitoring when Ctrl is disabled", mouseResume.ShouldRecover(6000, false, false, false));
        var backwards = new InputHealthMonitor(2000);
        Check("backward test clock resets baseline safely", !backwards.ShouldRecover(1000, true, true, false));
        Check("backward clock does not invent a second Ctrl transition", !backwards.ShouldRecover(1600, true, true, false));
        var startupHold = new InputHealthMonitor(0);
        startupHold.Registered(0, true, recovery: false);
        Check("Ctrl held while registering is not a new transition", !startupHold.ShouldRecover(2000, true, true, false));
        var boundary = new InputHealthMonitor(0);
        Check("subthreshold callback silence does not repair", !boundary.ShouldRecover(499, true, true, false));
        boundary.ShouldRecover(500, false, true, false);
        Check("callback silence boundary can repair on next press", boundary.ShouldRecover(501, true, true, false));
        var noSyntheticTap = new ControlTapGesture();
        noSyntheticTap.ClearHeldKeys(); noSyntheticTap.SeedHeldKey(VkLControl);
        Check("repair itself does not synthesize a double tap", !noSyntheticTap.Process(VkLControl, false, 100, false, true));
        return failures;
    }

    public static int RecoveryCheckCount => 37;

    private sealed class InputHealthMonitor
    {
        private long _lastTimer, _lastKeyboard, _lastRecovery;
        private bool _controlWasDown, _hasRecovery;

        public InputHealthMonitor(long now) { _lastTimer = _lastKeyboard = now; }
        public void KeyboardObserved(long now) { _lastKeyboard = now; }
        public void Registered(long now, bool controlDown, bool recovery)
        {
            _lastTimer = _lastKeyboard = now;
            _controlWasDown = controlDown;
            if (recovery) { _lastRecovery = now; _hasRecovery = true; }
        }
        public bool ShouldRecover(long now, bool controlDown, bool enabled, bool disposed)
        {
            if (disposed) return false;
            if (now < _lastTimer || now < _lastKeyboard || _hasRecovery && now < _lastRecovery)
            {
                _lastTimer = _lastKeyboard = now;
                if (_hasRecovery) _lastRecovery = now;
                _controlWasDown = controlDown;
                return false;
            }
            bool resumed = now - _lastTimer > 5000;
            bool newControlPress = controlDown && !_controlWasDown;
            _lastTimer = now;
            _controlWasDown = controlDown;
            bool cooldownComplete = !_hasRecovery || now - _lastRecovery >= 1000;
            return cooldownComplete && (resumed || enabled && newControlPress && now - _lastKeyboard >= 500);
        }
    }

    private sealed class ControlTapGesture
    {
        private readonly HashSet<int> _held = new();
        private bool _candidate, _hasFirstTap;
        private uint _pressTime, _firstRelease;
        public void SeedHeldKey(int key)
        {
            // Generic modifier values alias the sided keys and must never be seeded twice.
            if (IsPhysicalKeyboardKey(key) && key is not (0x10 or VkControl or 0x12)) _held.Add(key);
        }
        public void ResetSequence() { _candidate = false; _hasFirstTap = false; }
        public void ClearHeldKeys() { _held.Clear(); ResetSequence(); }
        public void ReconcileReleasedKeys(int currentKey, Func<int, bool> isKeyDown)
        {
            if (_held.RemoveWhere(key => key != currentKey && !isKeyDown(key)) != 0) ResetSequence();
        }
        public void ReconcileCurrentPress(int key, Func<int, bool> isKeyDown)
        {
            if (_held.Contains(key) && !isKeyDown(key)) { _held.Remove(key); ResetSequence(); }
        }
        public void ObserveOtherHeldKeys(int currentKey, Func<int, bool> isKeyDown)
        {
            for (int key = 8; key <= 0xE2; key++)
            {
                if (key == currentKey || key is 0x10 or VkControl or 0x12 || !IsPhysicalKeyboardKey(key)) continue;
                if (isKeyDown(key) && _held.Add(key)) ResetSequence();
            }
        }

        public bool Process(int key, bool down, uint time, bool injected, bool enabled)
        {
            if (injected) return false;
            // IME processing/status values can arrive without a corresponding release.
            // They may interrupt a tap sequence but can never become permanently held.
            if (!IsPhysicalKeyboardKey(key)) { ResetSequence(); return false; }
            bool control = key is VkLControl or VkRControl;
            if (down)
            {
                if (!_held.Add(key)) return false; // Ignore auto-repeat.
                if (control && _held.Count == 1 && enabled) { _candidate = true; _pressTime = time; }
                else { _candidate = false; _hasFirstTap = false; }
                return false;
            }
            bool wasHeld = _held.Remove(key);
            if (!enabled || !control || !wasHeld || !_candidate || _held.Count != 0 || unchecked(time - _pressTime) > 250)
            {
                _candidate = false; _hasFirstTap = false;
                return false;
            }
            _candidate = false;
            if (_hasFirstTap && unchecked(time - _firstRelease) <= 400)
            {
                _hasFirstTap = false;
                return true;
            }
            _hasFirstTap = true;
            _firstRelease = time;
            return false;
        }
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardHookData { public uint VirtualKey, ScanCode, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHookData { public NativePoint Point; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hookId, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr eventId, uint interval, IntPtr callback);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(IntPtr hwnd, UIntPtr eventId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int size);
}
