# Native popup callback integration checks

Run on Windows with .NET 10 Desktop SDK:

```powershell
dotnet run --project tests/ClipBridge.Native.Integration/ClipBridge.Native.Integration.csproj -c Release -- artifacts/native-integration.json
```

The harness links the production `PopupInteractionService.cs`. It installs and
disposes a real low-level keyboard hook with its real worker/message loop, then
manually marshals native keyboard packets into its registered callback. Its first
Ctrl-double-tap sequence runs against the freshly started service without resetting
the startup state, covering the IME-status-key regression. Later checks reset only
the isolated recognizer between cases. It verifies normalization, injected-event
rejection, asynchronous dispatcher delivery, an offscreen nonactivating WPF window,
and cleanup/queued-event cancellation.

It sends no Windows keyboard input, does not activate a visible popup, does not
access the clipboard, and does not load settings or sync data. The callback packets
are test data; the test therefore does **not** establish physical keyboard capture
or a complete real-user Ctrl-double-tap interaction. The native registration check
does establish whether hook installation itself succeeds in the tested session.
