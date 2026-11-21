using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipBridge.App.Services;

// No SendInput, clipboard access, target capture, persisted settings or OneDrive is used.
// Native hook installation is real; keyboard and pointer packets are manually marshalled
// into the installed service's callbacks. This is not a physical-input end-to-end test.
internal static class Program
{
    private const int KeyDown = 0x0100, KeyUp = 0x0101;
    private const uint LeftControl = 0xA2, RightControl = 0xA3, GenericControl = 0x11;
    private static readonly List<object> Results = new();
    private static int failures;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var startupService = new PopupInteractionService(dispatcher);
            var startupThread = (Thread?)typeof(PopupInteractionService).GetField("_hookThread", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(startupService);
            var gesture = typeof(PopupInteractionService).GetField("_gesture", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(startupService);
            var held = (IEnumerable<int>?)gesture?.GetType().GetField("_held", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(gesture);
            // Never log text-key values; only total count plus safe modifier/IME/reserved categories.
            int[] startupKeys = held?.ToArray() ?? [];
            int[] safeStatusKeys = [0x10, 0x11, 0x12, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1C, 0x1D, 0x1E, 0x1F, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xE5, 0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5];
            Console.WriteLine(JsonSerializer.Serialize(new { StartupHeldCount = startupKeys.Length, SafeStatusVirtualKeys = startupKeys.Where(safeStatusKeys.Contains).Select(key => $"0x{key:X2}").ToArray() }));
            Check("real Windows low-level hook installed", startupService.HookAvailable, new { startupService.HookAvailable, WorkerAlive = startupThread?.IsAlive });
            if (!startupService.HookAvailable)
            {
                Console.Error.WriteLine("Native hook unavailable. Callback tests cannot establish native registration success.");
                return Finish(args);
            }
            Check("hook message-loop worker alive", startupThread?.IsAlive == true);
            var mouseHook = typeof(PopupInteractionService).GetField("_mouseHook", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("_mouseHook");
            Check("real Windows low-level mouse hook installed", mouseHook.GetValue(startupService) is IntPtr handle && handle != IntPtr.Zero);

            var resizeArea = new System.Drawing.Rectangle(0, 0, 1920, 1080);
            var currentBounds = new System.Drawing.Rectangle(100, 300, 380, 500);
            var resizedBounds = PopupInteractionService.ResizePopupBounds(currentBounds, resizeArea, new System.Drawing.Size(380, 700));
            Check("section resize preserves current top edge", resizedBounds.X == 100 && resizedBounds.Y == 300 && resizedBounds.Height == 700);
            resizedBounds = PopupInteractionService.ResizePopupBounds(new System.Drawing.Rectangle(100, 900, 380, 100), resizeArea, new System.Drawing.Size(380, 700));
            Check("section resize uses space below top instead of flipping above cursor", resizedBounds.Y == 900 && resizedBounds.Bottom == 1072);
            resizedBounds = PopupInteractionService.ResizePopupBounds(new System.Drawing.Rectangle(-1400, -400, 380, 100), new System.Drawing.Rectangle(-1920, -1080, 1920, 1080), new System.Drawing.Size(380, 700));
            Check("section resize supports negative monitor coordinates", resizedBounds.Y == -400 && resizedBounds.Bottom == -8 && resizedBounds.X == -1400);

            var pasteFailures = PopupInteractionService.RunPasteChecks();
            Check("paste physical-key release regressions", pasteFailures.Count == 0,
                new { Cases = PopupInteractionService.PasteCheckCount, Failures = pasteFailures });
            // Observe only boolean categories. Never log text keys or their virtual-key codes.
            static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
            bool imeStatusHeld = Enumerable.Range(0xF0, 7).Any(IsDown);
            bool physicalKeyHeld = PopupInteractionService.HasPhysicalKeysDown(IsDown);
            bool filteredImeHeld = PopupInteractionService.HasPhysicalKeysDown(key => key is >= 0xF0 and <= 0xF6 && IsDown(key));
            Check("native IME status cannot block filtered paste gate", !filteredImeHeld,
                new { ImeStatusReportedHeld = imeStatusHeld, PhysicalKeyReportedHeld = physicalKeyHeld,
                    LegacyGateBlockedByImeOnly = imeStatusHeld && !physicalKeyHeld, FilteredGateBlockedByImeOnly = filteredImeHeld });

            var recoveryFailures = PopupInteractionService.RunRecoveryChecks();
            Check("input hook recovery regressions", recoveryFailures.Count == 0,
                new { Cases = PopupInteractionService.RecoveryCheckCount, Failures = recoveryFailures });

            // Keep the real startup observation separate from callback regression cases.
            // Those cases require deterministic physical-key state, independent of the
            // user's keyboard and Japanese IME configuration while the test is running.
            startupService.Dispose();
            var input = new FakePhysicalInput();
            using var service = new PopupInteractionService(dispatcher, input.IsDown, input.ReadClock);
            var thread = (Thread?)typeof(PopupInteractionService).GetField("_hookThread", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(service);
            Check("deterministic callback service has real native hooks", service.HookAvailable && service.OutsideClickHookAvailable && thread?.IsAlive == true);
            var healthTimer = typeof(PopupInteractionService).GetField("_healthTimer", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("_healthTimer");
            Check("native health timer registered on hook worker", healthTimer.GetValue(service) is UIntPtr timer && timer != UIntPtr.Zero);

            using var callback = new HookCallback(service, input);
            using var popup = new OffscreenPopup(dispatcher);
            int eventCount = 0;
            List<string>? dispatchOrder = null;
            service.DoubleControlPressed += () => { dispatchOrder?.Add("popup"); eventCount++; popup.Show(); };

            // Run before any reset: the initial clean gesture must already work.
            callback.DoubleTap(LeftControl, 100);
            Pump(dispatcher);
            Check("freshly started service recognizes first Ctrl double tap", eventCount == 1,
                new { StartupHeldCount = startupKeys.Length, Events = eventCount });
            Check("freshly started service opens offscreen popup", popup.IsVisible);

            callback.DoubleTap(LeftControl, 1000);
            Check("visible popup second Ctrl double tap is queued", eventCount == 1 && popup.IsVisible);
            Pump(dispatcher);
            Check("visible popup receives second Ctrl double tap", eventCount == 2);
            Check("visible popup stays open after second Ctrl double tap", popup.IsVisible);

            void Reset()
            {
                int recoveryBefore = service.RecoveryCount;
                service.DoubleControlEnabled = false;
                input.Clear();
                // Release callback-tracked keys without dispatching keyboard events to
                // Windows. Re-enabling then reseeds on the hook worker before any feed.
                for (uint key = 8; key < 255; key++) callback.Feed(key, false, 1);
                service.DoubleControlEnabled = true;
                if (!WaitUntil(() => service.RecoveryCount > recoveryBefore))
                    throw new TimeoutException("Input-hook recovery did not finish after re-enabling the gesture.");
                popup.Hide();
            }
            void Sequence(string name, Action feed, int expected = 1)
            {
                Reset();
                int before = eventCount;
                feed();
                Check(name + ": dispatch is queued", eventCount == before);
                Pump(dispatcher);
                Check(name + ": callback reaches UI dispatcher", eventCount == before + expected, new { Before = before, After = eventCount, Expected = expected });
                Check(name + ": offscreen window visibility", popup.IsVisible == (expected > 0));
            }

            Reset();
            var priorityOrder = new List<string>();
            dispatchOrder = priorityOrder;
            int beforePriorityGesture = eventCount;
            dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => priorityOrder.Add("refresh")));
            callback.DoubleTap(LeftControl, 100);
            dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() => priorityOrder.Add("binding")));
            Check("popup priority request remains asynchronous", priorityOrder.Count == 0 && eventCount == beforePriorityGesture);
            Pump(dispatcher);
            Check("queued data refresh precedes popup, popup precedes pending binding", priorityOrder.SequenceEqual(new[] { "refresh", "popup", "binding" }), priorityOrder);
            dispatchOrder = null;

            Sequence("left Ctrl", () => callback.DoubleTap(LeftControl, 100));
            Sequence("right Ctrl", () => callback.DoubleTap(RightControl, 100));
            Sequence("generic Ctrl normalized left", () => callback.DoubleTap(GenericControl, 100));
            Sequence("generic Ctrl extended normalized right", () => callback.DoubleTap(GenericControl, 100, flags: 1));
            Sequence("injected Ctrl ignored", () => callback.DoubleTap(LeftControl, 100, flags: 0x10), 0);
            Sequence("lower-integrity injected Ctrl ignored", () => callback.DoubleTap(LeftControl, 100, flags: 0x02), 0);
            Sequence("negative hook code ignored", () => callback.DoubleTap(LeftControl, 100, code: -1), 0);
            Sequence("single tap ignored", () => { callback.Feed(LeftControl, true, 100); callback.Feed(LeftControl, false, 150); }, 0);
            Sequence("Ctrl+C twice ignored", () =>
            {
                for (uint time = 100; time <= 300; time += 200)
                {
                    callback.Feed(LeftControl, true, time);
                    callback.Feed(0x43, true, time + 20);
                    callback.Feed(0x43, false, time + 40);
                    callback.Feed(LeftControl, false, time + 60);
                }
            }, 0);
            Sequence("disabled gesture ignored", () => { service.DoubleControlEnabled = false; callback.DoubleTap(LeftControl, 100); }, 0);

            Sequence("runtime IME status without key-up cannot poison Ctrl gesture", () =>
            {
                callback.Feed(0xF4, true, 10);
                callback.DoubleTap(LeftControl, 100);
            });
            Sequence("missed ordinary key-up is reconciled before clean Ctrl gesture", () =>
            {
                callback.Feed(0x41, true, 10);
                input.SetDown(0x41, false); // Physical release occurred while callback was lost.
                callback.DoubleTap(LeftControl, 100);
            });
            Sequence("physically held Shift remains a chord", () =>
            {
                callback.Feed(0xA0, true, 10);
                callback.DoubleTap(LeftControl, 100);
            }, 0);
            Sequence("physically held Enter remains a chord", () =>
            {
                callback.Feed(0x0D, true, 10);
                callback.DoubleTap(LeftControl, 100);
            }, 0);
            Sequence("missed Shift release is reconciled", () =>
            {
                callback.Feed(0xA0, true, 10);
                input.SetDown(0xA0, false);
                callback.DoubleTap(LeftControl, 100);
            });
            Sequence("missed same-Ctrl release permits next clean double tap", () =>
            {
                callback.Feed(LeftControl, true, 10);
                input.SetDown((int)LeftControl, false);
                callback.DoubleTap(LeftControl, 100);
            });
            Sequence("physical Ctrl auto-repeat cannot convert long hold into a tap", () =>
            {
                callback.Feed(LeftControl, true, 10);
                callback.Feed(LeftControl, true, 400);
                callback.Feed(LeftControl, false, 450);
                callback.Feed(LeftControl, true, 550);
                callback.Feed(LeftControl, false, 600);
            }, 0);
            Sequence("physically held Ctrl chord cannot become a double tap", () =>
            {
                callback.Feed(RightControl, true, 10);
                callback.DoubleTap(LeftControl, 100);
            }, 0);
            Sequence("missed letter down still blocks held Ctrl chord", () =>
            {
                input.SetDown(0x43, true); // Physical down occurred without a hook packet.
                callback.DoubleTap(LeftControl, 100);
            }, 0);

            var keyboardHook = typeof(PopupInteractionService).GetField("_hook", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("_hook");
            Reset();
            int explicitRecoveryBefore = service.RecoveryCount;
            var lostHook = (IntPtr)keyboardHook.GetValue(service)!;
            Check("test removes real keyboard hook without clearing cached handle", lostHook != IntPtr.Zero && UnhookWindowsHookEx(lostHook) && service.HookAvailable);
            service.RequestInputRecovery();
            bool explicitRecovered = WaitUntil(() => service.RecoveryCount > explicitRecoveryBefore);
            Check("explicit input recovery repairs a silently removed native hook", explicitRecovered && service.HookAvailable && thread?.IsAlive == true);
            int beforeRecoveredGesture = eventCount;
            callback.DoubleTap(LeftControl, 100);
            Pump(dispatcher);
            Check("clean Ctrl gesture reaches UI after explicit recovery", eventCount == beforeRecoveredGesture + 1 && popup.IsVisible);
            // A new nonzero field alone would not prove Windows accepted registration.
            // Removing the repaired registration succeeds only for a real native hook.
            int verifiedRecoveryBefore = service.RecoveryCount;
            Check("recovered keyboard handle is registered with Windows", UnhookWindowsHookEx((IntPtr)keyboardHook.GetValue(service)!));
            service.RequestInputRecovery();
            Check("verified recovery registration is restored for remaining checks", WaitUntil(() => service.RecoveryCount > verifiedRecoveryBefore) && service.HookAvailable);

            Reset();
            int healthRecoveryBefore = service.RecoveryCount;
            Check("health recovery fixture removes native hook", UnhookWindowsHookEx((IntPtr)keyboardHook.GetValue(service)!) && service.HookAvailable);
            input.SetClock(2000);
            input.SetDown((int)LeftControl, true); // New physical Ctrl down missed by removed hook.
            // Do not call the test probe or recovery API here: the native SetTimer ->
            // GetMessage(WM_TIMER) -> health monitor -> native registration path must run.
            Check("native health timer repairs silent hook loss after missed physical Ctrl", WaitUntil(() => service.RecoveryCount > healthRecoveryBefore) && service.HookAvailable);
            callback.Feed(LeftControl, false, 2050);
            int beforeHealthGesture = eventCount;
            callback.DoubleTap(LeftControl, 2200);
            Pump(dispatcher);
            Check("clean Ctrl gesture reaches UI after health recovery", eventCount == beforeHealthGesture + 1 && popup.IsVisible);

            Reset();
            int resumeRecoveryBefore = service.RecoveryCount;
            Check("resume recovery fixture removes native hook", UnhookWindowsHookEx((IntPtr)keyboardHook.GetValue(service)!) && service.HookAvailable);
            input.SetClock(8000); // The health worker has not run for over five seconds.
            service.CheckInputHealth();
            Check("health worker restores native hook after resume-sized clock gap", WaitUntil(() => service.RecoveryCount > resumeRecoveryBefore) && service.HookAvailable);
            int beforeResumeGesture = eventCount;
            callback.DoubleTap(LeftControl, 8200);
            Pump(dispatcher);
            Check("clean Ctrl gesture reaches UI after resume recovery", eventCount == beforeResumeGesture + 1 && popup.IsVisible);

            using var mouse = new MouseHookCallback(service);
            int outsideCount = 0;
            service.OutsidePointerPressed += () => { outsideCount++; popup.Hide(); };
            void MouseSequence(string name, Action feed, int expected = 1)
            {
                service.OutsideClickDismissalEnabled = true;
                popup.Show();
                int before = outsideCount;
                feed();
                Check(name + ": dispatch is queued", outsideCount == before && popup.IsVisible);
                Pump(dispatcher);
                Check(name + ": callback reaches UI dispatcher", outsideCount == before + expected,
                    new { Before = before, After = outsideCount, Expected = expected });
                Check(name + ": offscreen window dismissal", popup.IsVisible == (expected == 0));
                service.OutsideClickDismissalEnabled = false;
                popup.Hide();
            }

            MouseSequence("outside left-button press", () => mouse.Feed(0x0201));
            MouseSequence("outside right-button press", () => mouse.Feed(0x0204));
            MouseSequence("outside middle-button press", () => mouse.Feed(0x0207));
            MouseSequence("outside X-button press", () => mouse.Feed(0x020B));
            MouseSequence("injected outside press still dismisses", () => mouse.Feed(0x0201, flags: 0x01));
            MouseSequence("lower-integrity injected outside press still dismisses", () => mouse.Feed(0x0201, flags: 0x02));
            MouseSequence("negative mouse hook code ignored", () => mouse.Feed(0x0201, code: -1), 0);
            MouseSequence("pointer motion does not dismiss", () => mouse.Feed(0x0200), 0);
            MouseSequence("pointer button releases do not dismiss", () =>
            {
                foreach (int message in new[] { 0x0202, 0x0205, 0x0208, 0x020C }) mouse.Feed(message);
            }, 0);
            MouseSequence("pointer wheel does not dismiss", () =>
            {
                mouse.Feed(0x020A);
                mouse.Feed(0x020E);
            }, 0);
            MouseSequence("disabled outside dismissal ignored", () =>
            {
                service.OutsideClickDismissalEnabled = false;
                mouse.Feed(0x0201);
            }, 0);
            MouseSequence("queued outside press invalidated by hiding", () =>
            {
                mouse.Feed(0x0201);
                service.OutsideClickDismissalEnabled = false;
            }, 0);
            MouseSequence("queued outside press invalidated by reopening", () =>
            {
                mouse.Feed(0x0201);
                service.OutsideClickDismissalEnabled = true;
            }, 0);
            MouseSequence("queued outside press cannot cross closed session", () =>
            {
                mouse.Feed(0x0201);
                service.OutsideClickDismissalEnabled = false;
                service.OutsideClickDismissalEnabled = true;
            }, 0);

            Reset();
            int beforeDispose = eventCount;
            int outsideBeforeDispose = outsideCount;
            callback.DoubleTap(LeftControl, 100);
            service.OutsideClickDismissalEnabled = true;
            mouse.Feed(0x0201);
            service.Dispose();
            Pump(dispatcher);
            Check("disposing drops queued popup request", eventCount == beforeDispose && !popup.IsVisible);
            Check("disposing drops queued outside press", outsideCount == outsideBeforeDispose);
            Check("native hook released after dispose", !service.HookAvailable);
            Check("native mouse hook released after dispose", mouseHook.GetValue(service) is IntPtr released && released == IntPtr.Zero);
            Check("native health timer cleared after dispose", healthTimer.GetValue(service) is UIntPtr releasedTimer && releasedTimer == UIntPtr.Zero);
            Check("native hook worker stopped after dispose", thread?.IsAlive == false);
        }
        catch (Exception error)
        {
            Check("harness completed without exception", false, error.ToString());
        }
        return Finish(args);
    }

    private static void Pump(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    private static void Check(string name, bool success, object? detail = null)
    {
        if (!success) failures++;
        Results.Add(new { Name = name, Success = success, Detail = detail });
        Console.WriteLine($"{(success ? "PASS" : "FAIL")} {name}" + (detail == null ? "" : " " + JsonSerializer.Serialize(detail)));
    }

    private static int Finish(string[] args)
    {
        Console.WriteLine($"Native integration checks: {Results.Count - failures}/{Results.Count} passed.");
        Console.WriteLine("Scope: real keyboard/mouse SetWindowsHookEx and hook-worker lifecycle; manual native payload -> callback -> Dispatcher -> offscreen WPF window. No physical input test.");
        if (args.Length > 0)
        {
            File.WriteAllText(Path.GetFullPath(args[0]), JsonSerializer.Serialize(new
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                Scope = "real keyboard/mouse native registration/lifecycle; manually marshalled callback packets; no physical-input end-to-end test",
                Passed = Results.Count - failures,
                Total = Results.Count,
                Results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failures == 0 ? 0 : 1;
    }

    private sealed class HookCallback : IDisposable
    {
        private readonly PopupInteractionService service;
        private readonly FakePhysicalInput input;
        private readonly MethodInfo callback = typeof(PopupInteractionService).GetMethod("KeyboardHook", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("KeyboardHook");
        private readonly IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf<Packet>());
        public HookCallback(PopupInteractionService instance, FakePhysicalInput physicalInput)
        {
            service = instance;
            input = physicalInput;
        }
        public void Feed(uint key, bool down, uint time, uint flags = 0, int code = 0)
        {
            Marshal.StructureToPtr(new Packet { VirtualKey = key, Flags = flags, Time = time }, data, false);
            callback.Invoke(service, new object[] { code, new IntPtr(down ? KeyDown : KeyUp), data });
            // Windows low-level callbacks run before GetAsyncKeyState reflects this
            // packet. Update afterward; injected/ignored packets are not physical input.
            if (code >= 0 && (flags & 0x12) == 0)
                input.SetDown(key == GenericControl ? ((flags & 1) != 0 ? (int)RightControl : (int)LeftControl) : (int)key, down);
        }
        public void DoubleTap(uint key, uint time, uint flags = 0, int code = 0)
        {
            Feed(key, true, time, flags, code);
            Feed(key, false, time + 50, flags, code);
            Feed(key, true, time + 150, flags, code);
            Feed(key, false, time + 200, flags, code);
        }
        public void Dispose() => Marshal.FreeHGlobal(data);
        [StructLayout(LayoutKind.Sequential)]
        private struct Packet
        {
            public uint VirtualKey, ScanCode, Flags, Time;
            public UIntPtr ExtraInfo;
        }
    }

    private sealed class FakePhysicalInput
    {
        private readonly object gate = new();
        private readonly HashSet<int> down = new();
        private long clock;
        public bool IsDown(int key) { lock (gate) return down.Contains(key); }
        public void SetDown(int key, bool isDown)
        {
            lock (gate)
            {
                if (isDown) down.Add(key); else down.Remove(key);
            }
        }
        public void Clear() { lock (gate) down.Clear(); }
        public long ReadClock() => Interlocked.Read(ref clock);
        public void SetClock(long value) => Interlocked.Exchange(ref clock, value);
    }

    private sealed class MouseHookCallback : IDisposable
    {
        private readonly PopupInteractionService service;
        private readonly MethodInfo callback = typeof(PopupInteractionService).GetMethod("MouseHook", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("MouseHook");
        private readonly IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf<Packet>());
        public MouseHookCallback(PopupInteractionService instance) => service = instance;
        public void Feed(int message, uint flags = 0, int code = 0)
        {
            // Outside all desktop windows, including this harness's (-30000, -30000)
            // popup. No pointer movement or button event is sent to Windows.
            Marshal.StructureToPtr(new Packet { Point = new Point { X = -31000, Y = -31000 }, Flags = flags }, data, false);
            callback.Invoke(service, new object[] { code, new IntPtr(message), data });
        }
        public void Dispose() => Marshal.FreeHGlobal(data);
        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X, Y;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Packet
        {
            public Point Point;
            public uint MouseData, Flags, Time;
            public UIntPtr ExtraInfo;
        }
    }

    private sealed class OffscreenPopup : IDisposable
    {
        private readonly Window window;
        private readonly IntPtr originalForeground;
        public OffscreenPopup(Dispatcher dispatcher)
        {
            dispatcher.VerifyAccess();
            originalForeground = GetForegroundWindow();
            window = new Window
            {
                Width = 10, Height = 10, Left = -30000, Top = -30000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Opacity = 0
            };
            var handle = new WindowInteropHelper(window).EnsureHandle();
            const int ExStyle = -20;
            long style = GetWindowLongPtr(handle, ExStyle).ToInt64();
            SetWindowLongPtr(handle, ExStyle, new IntPtr(style | 0x08000000 | 0x80)); // NOACTIVATE | TOOLWINDOW
        }
        public bool IsVisible => window.IsVisible;
        public void Show()
        {
            window.Show();
            if (GetForegroundWindow() != originalForeground) throw new InvalidOperationException("Offscreen test window changed foreground.");
        }
        public void Hide() => window.Hide();
        public void Dispose() => window.Close();
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
