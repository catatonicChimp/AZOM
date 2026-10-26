using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using MozaPlugin.Settings;

namespace MozaPlugin.Devices.StalksTruckSim
{
    /// <summary>
    /// Translates MOZA Stalks button presses into keyboard output for a game
    /// ("keyboard sync" mode; ETS2/ATS by default). The decisions live in
    /// <see cref="StalkStateMachine"/>, which tracks where each lever sits and what
    /// the game is showing and converges the latter to the former; this class owns
    /// the threads around it — debounce, the deferred-cancel timer, foreground gating
    /// and the <see cref="KeyboardSender"/> (its own worker thread), so the HID and
    /// SimHub threads never block.
    ///
    /// Wiring: the plugin subscribes <see cref="OnStalkButton"/> to
    /// <c>MozaHidReader.StalksButtonChanged</c>, calls <see cref="ApplySettings"/>
    /// whenever settings change, and <see cref="SetGameContext"/> each DataUpdate.
    /// </summary>
    internal sealed class StalksTruckSimController : IDisposable
    {
        private readonly KeyboardSender _kb = new KeyboardSender();
        // Bindings starting "role:" go to SimHub Control Mapper instead of the keyboard.
        private readonly RoleSender _roles;
        private readonly object _lock = new object();

        // Config snapshot (guarded by _lock).
        private volatile StalkMode _mode = StalkMode.ButtonBox;
        private StalkTruckSimSettings _cfg = new StalkTruckSimSettings();

        // Fast gates (read on the HID thread).
        private volatile bool _keyboardEnabled;   // _mode == TruckSim with a game process configured
        private volatile bool _gameRunning;

        // Lever + game model. Only touched under _edgeLock.
        private readonly StalkStateMachine _machine = new StalkStateMachine();
        private readonly Timer _cancelTimer;
        private volatile StalkStateSnapshot _snapshot = StalkStateSnapshot.Empty;

        // Catch-up polling: while a lever and the game disagree, look for the game
        // window regaining focus at most this often (a process lookup per check).
        private const long ReconcilePollMs = 500;
        private long _lastReconcilePollMs;

        // Per-button press lockout (guarded by _lock). An accepted press freezes that
        // button for DebounceMs so contact chatter can't re-fire the action; a release
        // that lands inside the window is delivered when it closes.
        private readonly bool[] _raw = new bool[MozaData.MaxStalksButtons];
        private readonly long[] _lockoutEnd = new long[MozaData.MaxStalksButtons];   // Stopwatch ticks, 0 = none
        private volatile int _debounceMs;
        private readonly Timer _lockoutTimer;
        // Serialises edge delivery and every state-machine call between the HID
        // thread, the timers and DataUpdate. Taken before _lock, never inside it.
        private readonly object _edgeLock = new object();

        /// <param name="getControlMapper">Returns SimHub's ControlMapperInterface, or
        /// null while unavailable; used for "role:" bindings.</param>
        public StalksTruckSimController(Func<object?> getControlMapper)
        {
            _roles = new RoleSender(getControlMapper);
            _kb.SetForegroundProcesses(ParseProcesses(StalkTruckSimSettings.DefaultGameProcesses));
            _cancelTimer = new Timer(OnCancelTimer, null, Timeout.Infinite, Timeout.Infinite);
            _lockoutTimer = new Timer(OnLockoutTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>The latest lever + game model, for the UI and SimHub properties.</summary>
        public StalkStateSnapshot Snapshot => _snapshot;

        /// <summary>Apply the current mode + config (call on load and on every UI edit).</summary>
        public void ApplySettings(StalkMode mode, StalkTruckSimSettings cfg)
        {
            cfg = cfg ?? new StalkTruckSimSettings();
            var procs = ParseProcesses(cfg.GameProcesses);
            lock (_lock)
            {
                _mode = mode;
                _cfg = cfg;
                _kb.HoldMs = Math.Max(1, _cfg.KeyHoldMs);
                _kb.GapMs = Math.Max(0, _cfg.KeyGapMs);
                _roles.HoldMs = Math.Max(60, _cfg.KeyHoldMs);
                _roles.GapMs = Math.Max(60, _cfg.KeyGapMs);
                _debounceMs = Math.Max(0, _cfg.DebounceMs);
            }
            _kb.SetForegroundProcesses(procs);
            // No process configured means no window may receive keys — an empty list
            // would otherwise let the sender type into whatever has focus.
            _keyboardEnabled = mode == StalkMode.TruckSim && procs.Length > 0;
            lock (_edgeLock)
            {
                _machine.Configure(cfg);
                if (!_keyboardEnabled)
                {
                    _machine.ResetIndicator();
                    _machine.ForgetHeld();
                    // Flush first: ReleaseAll enqueues the key-ups, and a Flush after it
                    // would drain them before the worker wakes.
                    _kb.Flush();
                    _kb.ReleaseAll();
                    _roles.ReleaseAll();
                }
                PublishLocked();
            }
        }

        /// <summary>Push the current game context each DataUpdate tick.</summary>
        public void SetGameContext(string gameName, bool gameRunning)
        {
            bool wasRunning = _gameRunning;
            _gameRunning = gameRunning;
            if (wasRunning && !gameRunning)
            {
                // The blinker tracker models the game's own state, which is gone with
                // the game — unlike the wiper/light stages, it is reset here. Tracked
                // stages persist: alt-tabbing or a game switch leaves the truck's
                // lights/wipers as they were, and Re-sync realigns if anything drifts.
                lock (_edgeLock)
                {
                    _machine.ResetIndicator();
                    _machine.ForgetHeld();
                    _kb.Flush();
                    _kb.ReleaseAll();
                    _roles.ReleaseAll();
                    PublishLocked();
                }
                return;
            }

            // Catch up levers moved while keys couldn't be delivered (alt-tabbed, game
            // not yet started). Throttled, and only while something is out of line.
            if (!_keyboardEnabled) return;
            long now = NowMs();
            if (now - Interlocked.Read(ref _lastReconcilePollMs) < ReconcilePollMs) return;
            Interlocked.Exchange(ref _lastReconcilePollMs, now);
            bool needed;
            lock (_edgeLock) needed = _machine.NeedsReconcile;
            if (!needed || !_kb.IsGameForeground()) return;
            var output = new List<StalkKeyCommand>();
            lock (_edgeLock)
            {
                _machine.Reconcile(output);
                Send(output);
                PublishLocked();
            }
        }

        /// <summary>Handle a stalk button edge (from the HID read thread).</summary>
        public void OnStalkButton(int index, bool pressed)
        {
            if (index < 0 || index >= MozaData.MaxStalksButtons) return;
            lock (_edgeLock)
            {
                bool owedRelease = false;
                lock (_lock)
                {
                    _raw[index] = pressed;
                    long now = Stopwatch.GetTimestamp();
                    long end = _lockoutEnd[index];
                    if (end != 0 && now < end) return;   // frozen
                    // Window over but not swept by the timer yet: a release that landed
                    // inside it is still owed ahead of this press.
                    if (end != 0) { _lockoutEnd[index] = 0; owedRelease = pressed; }
                    int debounce = _debounceMs;
                    if (pressed && debounce > 0)
                    {
                        _lockoutEnd[index] = now + debounce * Stopwatch.Frequency / 1000;
                        ArmLockoutTimer(now);
                    }
                }
                if (owedRelease) ProcessEdge(index, false);
                ProcessEdge(index, pressed);
            }
        }

        // A lockout window closed: deliver the release for any button let go while frozen.
        private void OnLockoutTimer(object? _)
        {
            lock (_edgeLock)
            {
                var released = new List<int>();
                lock (_lock)
                {
                    long now = Stopwatch.GetTimestamp();
                    for (int i = 0; i < _lockoutEnd.Length; i++)
                    {
                        if (_lockoutEnd[i] == 0 || _lockoutEnd[i] > now) continue;
                        _lockoutEnd[i] = 0;
                        if (!_raw[i]) released.Add(i);
                    }
                    ArmLockoutTimer(now);
                }
                foreach (int i in released) ProcessEdge(i, false);
            }
        }

        /// <summary>Re-arm the lockout timer for the earliest open window, or park it.
        /// Caller holds <see cref="_lock"/>.</summary>
        private void ArmLockoutTimer(long now)
        {
            long earliest = 0;
            for (int i = 0; i < _lockoutEnd.Length; i++)
                if (_lockoutEnd[i] != 0 && (earliest == 0 || _lockoutEnd[i] < earliest)) earliest = _lockoutEnd[i];
            long dueMs = earliest == 0 ? Timeout.Infinite
                : Math.Max(1, (earliest - now) * 1000 / Stopwatch.Frequency);
            try { _lockoutTimer.Change(dueMs, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>Act on an accepted edge. Runs under <see cref="_edgeLock"/>.</summary>
        private void ProcessEdge(int index, bool pressed)
        {
            var output = new List<StalkKeyCommand>();
            if (!pressed)
            {
                // Release edge: held keys are let go (a key-up is always sent, so a
                // key can never stick down); a short cruise press acts now.
                _machine.OnRelease(index, NowMs(), _keyboardEnabled && _kb.IsGameForeground(), output);
            }
            else
            {
                // Press edge: keys reach the game only while it owns the foreground.
                // The lever model moves regardless, so the visual tracks the stalk and
                // the game is caught up once it regains focus.
                bool deliver = _keyboardEnabled && _kb.IsGameForeground();
                _machine.OnPress(index, NowMs(), deliver, output);
            }
            Send(output);
            PublishLocked();
        }

        // A deferred indicator cancel or a cruise hold step is due.
        private void OnCancelTimer(object? _)
        {
            // The foreground query is a process lookup; it is fine under _edgeLock
            // (which only serialises edges) but must stay outside _lock.
            bool deliver = _keyboardEnabled && _kb.IsGameForeground();
            lock (_edgeLock)
            {
                var output = new List<StalkKeyCommand>();
                _machine.Tick(NowMs(), deliver, IsRawPressed, output);
                Send(output);
                PublishLocked();
            }
        }

        private bool IsRawPressed(int index)
        {
            lock (_lock) return index >= 0 && index < _raw.Length && _raw[index];
        }

        /// <summary>Execute machine output, re-arm the cancel timer.
        /// Caller holds <see cref="_edgeLock"/>.</summary>
        private void Send(List<StalkKeyCommand> output)
        {
            foreach (var c in output)
            {
                switch (c.Op)
                {
                    case StalkKeyOp.ReleaseAll: _kb.ReleaseAll(); _roles.ReleaseAll(); break;
                    case StalkKeyOp.Tap when StalkOutput.IsRole(c.Key): _roles.Tap(StalkOutput.RoleName(c.Key)); break;
                    case StalkKeyOp.Down when StalkOutput.IsRole(c.Key): _roles.Down(StalkOutput.RoleName(c.Key)); break;
                    case StalkKeyOp.Up when StalkOutput.IsRole(c.Key): _roles.Up(StalkOutput.RoleName(c.Key)); break;
                    case StalkKeyOp.Tap: _kb.Tap(c.Key); break;
                    case StalkKeyOp.Down: _kb.KeyDown(c.Key); break;
                    case StalkKeyOp.Up: _kb.KeyUp(c.Key); break;
                }
            }
        }

        /// <summary>Re-arm the cancel timer and publish a fresh snapshot.
        /// Caller holds <see cref="_edgeLock"/>.</summary>
        private void PublishLocked()
        {
            long due = _machine.PendingDeadlineMs;
            long dueIn = due == 0 ? Timeout.Infinite : Math.Max(1, due - NowMs());
            try { _cancelTimer.Change(dueIn, Timeout.Infinite); }
            catch (ObjectDisposedException) { }

            var snap = _machine.Snapshot();
            snap.KeyboardSync = _mode == StalkMode.TruckSim;
            _snapshot = snap;
        }

        /// <summary>Force the game wipers to stage 0 and re-sync the tracker
        /// (the UI "Re-sync" button). Levers that sit elsewhere are then caught up
        /// on the next DataUpdate, so the game ends up matching the stalks.</summary>
        public void ResyncWipers()
        {
            bool deliver = _keyboardEnabled && _kb.IsGameForeground();
            lock (_edgeLock)
            {
                var output = new List<StalkKeyCommand>();
                _machine.ResyncToRest(deliver, output);
                Send(output);
                PublishLocked();
            }
        }

        private static long NowMs() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

        /// <summary>Split a comma/semicolon/space separated process list, dropping any
        /// ".exe" suffix.</summary>
        internal static string[] ParseProcesses(string? list)
        {
            if (string.IsNullOrWhiteSpace(list)) return Array.Empty<string>();
            var result = new List<string>();
            foreach (var part in list!.Split(new[] { ',', ';', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = part.Trim();
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - 4);
                if (name.Length > 0) result.Add(name);
            }
            return result.ToArray();
        }

        public void Dispose()
        {
            try { _lockoutTimer.Dispose(); } catch { }
            try { _cancelTimer.Dispose(); } catch { }
            try { _kb.Dispose(); } catch { }
            try { _roles.Dispose(); } catch { }
        }
    }
}
