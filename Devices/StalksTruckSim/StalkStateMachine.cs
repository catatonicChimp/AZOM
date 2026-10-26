using System;
using System.Collections.Generic;
using MozaPlugin.Settings;

namespace MozaPlugin.Devices.StalksTruckSim
{
    /// <summary>A single keyboard operation produced by <see cref="StalkStateMachine"/>.</summary>
    internal enum StalkKeyOp { Tap, Down, Up, ReleaseAll }

    internal readonly struct StalkKeyCommand
    {
        public StalkKeyCommand(StalkKeyOp op, string key)
        {
            Op = op;
            Key = key ?? "";
        }

        public StalkKeyOp Op { get; }
        public string Key { get; }

        public override string ToString() => Op == StalkKeyOp.ReleaseAll ? "ReleaseAll" : Op + ":" + Key;
    }

    public enum IndicatorSide { None = 0, Left = 1, Right = 2 }

    public enum HighBeamLever { Unknown = 0, Neutral = 1, On = 2, Flash = 3 }

    /// <summary>
    /// Immutable view of the stalks for the settings-tab visual and the
    /// <c>AZOM.Stalks.*</c> properties. "Lever" fields are where the physical stalk
    /// sits; the others are what the plugin believes the game is showing.
    /// </summary>
    public sealed class StalkStateSnapshot
    {
        public static readonly StalkStateSnapshot Empty = new StalkStateSnapshot();

        /// <summary>Keyboard sync is on, so the game fields are being driven. In
        /// button-box mode only the lever fields mean anything.</summary>
        public bool KeyboardSync { get; internal set; }

        public IndicatorSide IndicatorLever { get; internal set; }
        public IndicatorSide IndicatorLit { get; internal set; }
        /// <summary>Lever is back at neutral but the blinker is held for the minimum blink time.</summary>
        public bool IndicatorCancelPending { get; internal set; }

        /// <summary>Wiper lever stage, -1 until a wiper position has been reported.</summary>
        public int WiperLever { get; internal set; } = -1;
        public bool WiperSwipe { get; internal set; }
        public int WiperStage { get; internal set; }
        public int WiperStageCount { get; internal set; } = 1;

        /// <summary>Intermittent-wiper frequency lever stage, -1 until reported.</summary>
        public int WiperIntervalLever { get; internal set; } = -1;
        public int WiperIntervalStage { get; internal set; }
        public int WiperIntervalStageCount { get; internal set; } = 1;

        /// <summary>Light knob stage, -1 until a light position has been reported.</summary>
        public int LightLever { get; internal set; } = -1;
        public int LightStage { get; internal set; }
        public int LightStageCount { get; internal set; } = 1;

        public HighBeamLever HighBeamLever { get; internal set; }
        public bool HighBeamOn { get; internal set; }
        public bool FlashHeld { get; internal set; }

        /// <summary>Cruise control tracked as engaged in the game.</summary>
        public bool CruiseActive { get; internal set; }
        /// <summary>Cruise main switch tracked as on (armed or engaged).</summary>
        public bool CruiseMain { get; internal set; }
        /// <summary>Cruise lever: +1 held up, -1 held down, 0 at rest.</summary>
        public int CruiseLever { get; internal set; }

        /// <summary>Generic toggle keys the plugin tracks as switched on in the game.</summary>
        public IReadOnlyList<string> TogglesOn { get; internal set; } = Array.Empty<string>();
    }

    /// <summary>
    /// The stalk logic, free of threads, timers and key injection so it can be
    /// reasoned about (and exercised) in isolation.
    ///
    /// MOZA stalks report absolute positions — a button per lever position — while
    /// games mostly expose relative controls: a toggle key (indicators, high beam,
    /// beacons…) or a cycle key (wipers, light modes). The machine keeps two models:
    ///
    ///   • the <b>lever</b> state — where each stalk physically sits, updated on every
    ///     press whether or not keys can be delivered;
    ///   • the <b>game</b> state — what the plugin believes the game shows, advanced
    ///     only by keys it actually sends.
    ///
    /// Each press converges the game state for that control to the lever; anything
    /// left mismatched (the lever moved while the game was alt-tabbed) is caught up by
    /// <see cref="Reconcile"/> once delivery is possible again. Indicators are never
    /// reconciled — a blinker switching on by itself on refocus would surprise.
    ///
    /// Not thread-safe: the owner serialises every call.
    /// </summary>
    internal sealed class StalkStateMachine
    {
        private StalkTruckSimSettings _cfg = new StalkTruckSimSettings();

        // Lever (physical) state.
        private IndicatorSide _indLever;
        private int _wiperLever = -1;
        private bool _wiperSwipe;
        private int _swipeReturn;
        private int _lightLever = -1;
        private HighBeamLever _hbLever;
        // Wanted high-beam state: set by a latched lever position or a sprung toggle
        // push; null until either has been used (then high beam is never reconciled).
        private bool? _hbWant;
        private int _intervalLever = -1;
        private readonly Dictionary<string, bool> _toggleLever = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Game (tracked) state.
        private IndicatorSide _indLit;
        private long _indOnMs;
        private IndicatorSide _pendingCancel;
        private long _cancelDueMs;
        private int _wiperStage;
        private int _lightStage;
        private bool _highBeam;
        private int _intervalStage;
        private readonly Dictionary<string, bool> _toggleGame = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private bool _cruiseActive;
        private bool _cruiseMain;

        // Cruise lever hold tracking: which way it is held (+1/-1, 0 = rest), when the
        // next hold step is due, and whether the hold already fired (so the release
        // doesn't add a short-press step on top).
        private int _cruiseHeld;
        private long _cruiseNextMs;
        private bool _cruiseLong;

        // Keys this machine asked to be held, so a key-up is never sent for a key
        // that was not pressed (a stray key-up still reaches the focused window).
        private readonly HashSet<string> _down = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public StalkTruckSimSettings Config => _cfg;

        public void Configure(StalkTruckSimSettings? cfg)
        {
            _cfg = cfg ?? new StalkTruckSimSettings();
            _wiperStage = WrapStage(_wiperStage, _cfg.WiperStageCount);
            _lightStage = WrapStage(_lightStage, _cfg.LightStageCount);
            if (_wiperLever >= 0) _wiperLever = WrapStage(_wiperLever, _cfg.WiperStageCount);
            if (_lightLever >= 0) _lightLever = WrapStage(_lightLever, _cfg.LightStageCount);
            _intervalStage = WrapStage(_intervalStage, _cfg.WiperIntervalStageCount);
            if (_intervalLever >= 0) _intervalLever = WrapStage(_intervalLever, _cfg.WiperIntervalStageCount);
            // A blink scheduled under the old style/keys must not fire under the new ones.
            _pendingCancel = IndicatorSide.None;
        }

        /// <summary>Earliest time (ms) <see cref="Tick"/> has work to do; 0 = nothing pending.</summary>
        public long PendingDeadlineMs
        {
            get
            {
                long a = _pendingCancel != IndicatorSide.None ? _cancelDueMs : 0;
                long b = _cruiseHeld != 0 ? _cruiseNextMs : 0;
                return a == 0 ? b : b == 0 ? a : Math.Min(a, b);
            }
        }

        /// <summary>True when a lever sits somewhere the tracked game state does not
        /// match — <see cref="Reconcile"/> would send keys.</summary>
        public bool NeedsReconcile
        {
            get
            {
                if (_wiperLever >= 0 && !_wiperSwipe && _wiperLever != _wiperStage) return true;
                if (_lightLever >= 0 && _lightLever != _lightStage) return true;
                if (_intervalLever >= 0 && _intervalLever != _intervalStage) return true;
                if (_hbWant.HasValue && _hbWant.Value != _highBeam) return true;
                foreach (var kv in _toggleLever)
                    if (GameToggle(kv.Key) != kv.Value) return true;
                return false;
            }
        }

        /// <summary>Handle an accepted press of a stalk button.</summary>
        /// <param name="deliver">Keys sent now will reach the game. When false only the
        /// lever model moves (and keys already held may still be released).</param>
        public void OnPress(int index, long nowMs, bool deliver, List<StalkKeyCommand> output)
        {
            var action = ActionFor(index);
            if (action == null) return;
            var cfg = _cfg;

            switch (action.Kind)
            {
                case StalkActionKind.HeldKey:
                case StalkActionKind.LatchKey:
                    // Latch: released by a ReleaseHeld button, not this button's release.
                    if (deliver) Down(action.Key, output);
                    break;
                case StalkActionKind.ReleaseHeld:
                    ReleaseAll(output);
                    break;
                case StalkActionKind.Momentary:
                    if (deliver) Tap(action.Key, output);
                    break;

                case StalkActionKind.WiperStage:
                    _wiperLever = WrapStage(action.Stage, cfg.WiperStageCount);
                    _wiperSwipe = false;
                    if (deliver) ConvergeWiper(_wiperLever, output);
                    break;
                case StalkActionKind.WiperSingleSwipe:
                    // Spring-loaded: one forward step (wipers on) while held; the release
                    // (or the off position firing as the stalk returns) steps back.
                    if (_wiperSwipe) break;
                    _wiperSwipe = true;
                    _swipeReturn = _wiperLever >= 0 ? _wiperLever : _wiperStage;
                    if (deliver)
                    {
                        Tap(cfg.WiperForwardKey, output);
                        _wiperStage = WrapStage(_wiperStage + 1, cfg.WiperStageCount);
                    }
                    break;
                case StalkActionKind.WiperIntervalStage:
                    _intervalLever = WrapStage(action.Stage, cfg.WiperIntervalStageCount);
                    if (deliver) ConvergeInterval(_intervalLever, output);
                    break;
                case StalkActionKind.LightStage:
                    _lightLever = WrapStage(action.Stage, cfg.LightStageCount);
                    if (deliver) ConvergeLight(_lightLever, output);
                    break;

                case StalkActionKind.IndicatorLeft:
                    _indLever = IndicatorSide.Left;
                    if (deliver) Indicate(IndicatorSide.Left, nowMs, output);
                    break;
                case StalkActionKind.IndicatorRight:
                    _indLever = IndicatorSide.Right;
                    if (deliver) Indicate(IndicatorSide.Right, nowMs, output);
                    break;
                case StalkActionKind.IndicatorCancel:
                    _indLever = IndicatorSide.None;
                    CancelIndicator(nowMs, deliver, output);
                    break;

                case StalkActionKind.HighBeamOn:
                    _hbLever = HighBeamLever.On;
                    _hbWant = true;
                    Up(cfg.HighBeamFlashKey, output);
                    if (deliver) ConvergeHighBeam(output);
                    break;
                case StalkActionKind.HighBeamToggle:
                    // Sprung push: the lever comes straight back to neutral, so the
                    // press itself is the request — flip what we want.
                    _hbWant = !(_hbWant ?? _highBeam);
                    if (deliver) ConvergeHighBeam(output);
                    break;
                case StalkActionKind.HighBeamNeutral:
                    if (_hbLever == HighBeamLever.On) _hbWant = false;
                    _hbLever = HighBeamLever.Neutral;
                    Up(cfg.HighBeamFlashKey, output);
                    if (deliver) ConvergeHighBeam(output);
                    break;
                case StalkActionKind.HighBeamFlash:
                    if (_hbLever == HighBeamLever.On) _hbWant = false;
                    _hbLever = HighBeamLever.Flash;
                    if (deliver)
                    {
                        ConvergeHighBeam(output);
                        Down(cfg.HighBeamFlashKey, output);
                    }
                    break;

                case StalkActionKind.ToggleFlip:
                    if (string.IsNullOrEmpty(action.Key)) break;
                    _toggleLever[action.Key] = !(_toggleLever.TryGetValue(action.Key, out bool cur) ? cur : GameToggle(action.Key));
                    if (deliver) ConvergeToggle(action.Key, output);
                    break;
                case StalkActionKind.ToggleOn:
                case StalkActionKind.ToggleOff:
                case StalkActionKind.ToggleHold:
                    if (string.IsNullOrEmpty(action.Key)) break;
                    _toggleLever[action.Key] = action.Kind != StalkActionKind.ToggleOff;
                    if (deliver) ConvergeToggle(action.Key, output);
                    break;

                case StalkActionKind.CruiseUp:
                case StalkActionKind.CruiseDown:
                    // Short or long is only known on release / at the hold time, so
                    // nothing is sent yet.
                    _cruiseHeld = action.Kind == StalkActionKind.CruiseUp ? 1 : -1;
                    _cruiseLong = false;
                    _cruiseNextMs = nowMs + Math.Max(1, cfg.CruiseHoldMs);
                    break;
                case StalkActionKind.CruiseCancel:
                    _cruiseHeld = 0;
                    if (!deliver) break;
                    if (!string.IsNullOrEmpty(cfg.CruiseCancelKey))
                    {
                        Tap(cfg.CruiseCancelKey, output);
                        _cruiseActive = false;
                    }
                    else if (cfg.CruiseMainEngages && _cruiseActive && !string.IsNullOrEmpty(cfg.CruiseMainKey))
                    {
                        // ETS2-style single key: the main key toggles cruise off.
                        Tap(cfg.CruiseMainKey, output);
                        _cruiseActive = false;
                        _cruiseMain = false;
                    }
                    break;
                case StalkActionKind.CruiseMainToggle:
                    _cruiseHeld = 0;
                    if (!deliver || string.IsNullOrEmpty(cfg.CruiseMainKey)) break;
                    Tap(cfg.CruiseMainKey, output);
                    // A main key that engages also disengages: follow the cruise state.
                    _cruiseMain = cfg.CruiseMainEngages ? !_cruiseActive : !_cruiseMain;
                    _cruiseActive = cfg.CruiseMainEngages && _cruiseMain;
                    break;
            }
        }

        /// <summary>Handle a release. Held keys are let go, a held switch goes off, and
        /// a cruise lever released before its hold time fires its short-press action.</summary>
        public void OnRelease(int index, long nowMs, bool deliver, List<StalkKeyCommand> output)
        {
            var action = ActionFor(index);
            if (action == null) return;
            if (action.Kind == StalkActionKind.HeldKey)
            {
                Up(action.Key, output);
                return;
            }
            if (action.Kind == StalkActionKind.HighBeamFlash)
            {
                // Sprung flash lever let go: drop the flash now rather than waiting
                // for the neutral position to report.
                Up(_cfg.HighBeamFlashKey, output);
                return;
            }
            if (action.Kind == StalkActionKind.WiperSingleSwipe)
            {
                if (!_wiperSwipe) return;
                _wiperSwipe = false;
                if (_wiperLever < 0) _wiperLever = _swipeReturn;
                if (deliver) ConvergeWiper(_wiperLever, output);
                return;
            }
            if (action.Kind == StalkActionKind.ToggleHold)
            {
                if (string.IsNullOrEmpty(action.Key)) return;
                _toggleLever[action.Key] = false;
                if (deliver) ConvergeToggle(action.Key, output);
                return;
            }
            if (action.Kind != StalkActionKind.CruiseUp && action.Kind != StalkActionKind.CruiseDown) return;

            int dir = action.Kind == StalkActionKind.CruiseUp ? 1 : -1;
            if (_cruiseHeld != dir) return;
            _cruiseHeld = 0;
            if (_cruiseLong || !deliver) return;
            CruiseStep(dir, big: false, output);
        }

        /// <summary>Serve a deferred indicator cancel once its time has come.</summary>
        /// <param name="isPressed">Raw (un-debounced) button state, so a lever already
        /// back at the lit side keeps signalling even if its press is still locked out.</param>
        public void Tick(long nowMs, bool deliver, Func<int, bool> isPressed, List<StalkKeyCommand> output)
        {
            TickCruise(nowMs, deliver, output);
            TickIndicator(nowMs, deliver, isPressed, output);
        }

        // Cruise lever still held at its hold time (and every repeat after): send the
        // hold action. Undeliverable steps are skipped, not queued.
        private void TickCruise(long nowMs, bool deliver, List<StalkKeyCommand> output)
        {
            if (_cruiseHeld == 0 || nowMs < _cruiseNextMs) return;
            bool first = !_cruiseLong;
            _cruiseLong = true;
            _cruiseNextMs = nowMs + Math.Max(50, _cfg.CruiseRepeatMs);
            if (!deliver) return;
            // Holding with cruise off engages it first; the repeats then step.
            if (first && !_cruiseActive) CruiseStep(_cruiseHeld, big: false, output);
            else CruiseStep(_cruiseHeld, big: true, output);
        }

        /// <summary>One cruise action. Off: resume (up) or set (down) engages it.
        /// Engaged: a speed step, the big-step key when <paramref name="big"/> and bound.</summary>
        private void CruiseStep(int dir, bool big, List<StalkKeyCommand> output)
        {
            var cfg = _cfg;
            if (!_cruiseActive)
            {
                string engage = dir > 0 ? cfg.CruiseResumeKey : cfg.CruiseSetKey;
                // Unbound resume/set: the step key itself is how the game engages.
                if (string.IsNullOrEmpty(engage)) engage = dir > 0 ? cfg.CruiseUpKey : cfg.CruiseDownKey;
                if (string.IsNullOrEmpty(engage)) return;
                Tap(engage, output);
                _cruiseActive = true;
                _cruiseMain = true;
                return;
            }
            string bigKey = dir > 0 ? cfg.CruiseUpBigKey : cfg.CruiseDownBigKey;
            string key = big && !string.IsNullOrEmpty(bigKey) ? bigKey : (dir > 0 ? cfg.CruiseUpKey : cfg.CruiseDownKey);
            Tap(key, output);
        }

        private void TickIndicator(long nowMs, bool deliver, Func<int, bool> isPressed, List<StalkKeyCommand> output)
        {
            var side = _pendingCancel;
            if (side == IndicatorSide.None) return;
            if (_indLit != side) { _pendingCancel = IndicatorSide.None; return; }
            if (nowMs < _cancelDueMs) return;

            _pendingCancel = IndicatorSide.None;
            if (SideHeld(side, isPressed)) return;
            // Not deliverable: the key would be dropped, so leave the blinker tracked
            // as lit — the next lever flick then still resolves to the right tap.
            if (!deliver) return;
            _indLit = IndicatorSide.None;
            Tap(SideKey(side), output);
        }

        /// <summary>Send whatever brings wipers, lights, high beam and toggles in line
        /// with the levers. Call only when keys can be delivered.</summary>
        public void Reconcile(List<StalkKeyCommand> output)
        {
            if (_wiperLever >= 0 && !_wiperSwipe) ConvergeWiper(_wiperLever, output);
            if (_lightLever >= 0) ConvergeLight(_lightLever, output);
            if (_intervalLever >= 0) ConvergeInterval(_intervalLever, output);
            if (_hbWant.HasValue) ConvergeHighBeam(output);
            foreach (var key in new List<string>(_toggleLever.Keys)) ConvergeToggle(key, output);
        }

        /// <summary>The game re-homed (Re-sync button): drive the wipers to off when
        /// deliverable and assume every tracked control is back at its rest state.</summary>
        public void ResyncToRest(bool deliver, List<StalkKeyCommand> output)
        {
            if (deliver && !string.IsNullOrEmpty(_cfg.WiperBackKey))
            {
                int backs = Math.Max(0, _cfg.WiperStageCount - 1);
                for (int i = 0; i < backs; i++) Tap(_cfg.WiperBackKey, output);
            }
            // The light knob's cycle key is forward-only, so the lights can't be
            // forced — the tracker assumes the user has set them off.
            _wiperStage = 0;
            _lightStage = 0;
            _highBeam = false;
            // A latched lever still wants high beam; a sprung toggle starts over off.
            if (_hbWant.HasValue) _hbWant = _hbLever == HighBeamLever.On;
            _cruiseActive = false;
            _cruiseMain = false;
            _toggleGame.Clear();
        }

        /// <summary>Forget the tracked blinker, cruise state and anything deferred
        /// (the game, and with it that state, went away).</summary>
        public void ResetIndicator()
        {
            _pendingCancel = IndicatorSide.None;
            _indLit = IndicatorSide.None;
            _cruiseActive = false;
            _cruiseMain = false;
            _cruiseHeld = 0;
        }

        /// <summary>The owner released every key itself (mode change, game exit).</summary>
        public void ForgetHeld()
        {
            _down.Clear();
            if (_cfg.IndicatorStyle == IndicatorStyle.Hold) _indLit = IndicatorSide.None;
        }

        public StalkStateSnapshot Snapshot()
        {
            var on = new List<string>();
            foreach (var kv in _toggleGame) if (kv.Value) on.Add(kv.Key);
            return new StalkStateSnapshot
            {
                IndicatorLever = _indLever,
                IndicatorLit = _indLit,
                IndicatorCancelPending = _pendingCancel != IndicatorSide.None,
                WiperLever = _wiperLever,
                WiperSwipe = _wiperSwipe,
                WiperStage = _wiperStage,
                WiperStageCount = Math.Max(1, _cfg.WiperStageCount),
                WiperIntervalLever = _intervalLever,
                WiperIntervalStage = _intervalStage,
                WiperIntervalStageCount = Math.Max(1, _cfg.WiperIntervalStageCount),
                LightLever = _lightLever,
                LightStage = _lightStage,
                LightStageCount = Math.Max(1, _cfg.LightStageCount),
                HighBeamLever = _hbLever,
                HighBeamOn = _highBeam,
                FlashHeld = !string.IsNullOrEmpty(_cfg.HighBeamFlashKey) && _down.Contains(_cfg.HighBeamFlashKey),
                CruiseActive = _cruiseActive,
                CruiseMain = _cruiseMain,
                CruiseLever = _cruiseHeld,
                TogglesOn = on,
            };
        }

        // ---- indicators ---------------------------------------------------------

        // Toggle style: tap the side's key and remember it so a later neutral press can
        // re-tap it to switch the blinker off. A side that is already lit is switched
        // off instead, and swapping sides switches the old one off before the new one on.
        // Hold style: the side key is simply held while the lever sits there.
        private void Indicate(IndicatorSide side, long nowMs, List<StalkKeyCommand> output)
        {
            if (_cfg.IndicatorStyle == IndicatorStyle.Hold)
            {
                Up(SideKey(Other(side)), output);
                Down(SideKey(side), output);
                _indLit = side;
                return;
            }

            // Same side again inside the minimum blink time: the lever came back from
            // neutral (or the contact chattered) — keep it lit, don't toggle.
            if (_indLit == side && (_pendingCancel == side || RemainingBlinkMs(nowMs) > 0))
            {
                _pendingCancel = IndicatorSide.None;
                return;
            }
            _pendingCancel = IndicatorSide.None;
            bool turnOn = _indLit != side;
            if (_indLit != IndicatorSide.None && _indLit != side)
                Tap(SideKey(_indLit), output);
            _indLit = turnOn ? side : IndicatorSide.None;
            if (turnOn) _indOnMs = nowMs;
            Tap(SideKey(side), output);
        }

        // Lever back at neutral. Toggle style cancels now only once the blinker has been
        // lit for the configured minimum; otherwise the cancel is deferred to Tick so a
        // quick flick still signals for a few seconds (matching Pit House).
        private void CancelIndicator(long nowMs, bool deliver, List<StalkKeyCommand> output)
        {
            if (_cfg.IndicatorStyle == IndicatorStyle.Hold)
            {
                // Releasing is always safe, deliverable or not.
                Up(_cfg.IndicatorLeftKey, output);
                Up(_cfg.IndicatorRightKey, output);
                _indLit = IndicatorSide.None;
                return;
            }

            // Not deliverable: leave any armed cancel alone, as if the press never came.
            if (!deliver) return;
            _pendingCancel = IndicatorSide.None;
            if (_indLit == IndicatorSide.None) return;

            long remainMs = RemainingBlinkMs(nowMs);
            if (remainMs > 0)
            {
                _pendingCancel = _indLit;
                _cancelDueMs = nowMs + remainMs;
                return;
            }
            Tap(SideKey(_indLit), output);
            _indLit = IndicatorSide.None;
        }

        private long RemainingBlinkMs(long nowMs)
        {
            long minMs = Math.Max(0, _cfg.IndicatorMinBlinkSeconds) * 1000L;
            return Math.Max(0, minMs - (nowMs - _indOnMs));
        }

        private bool SideHeld(IndicatorSide side, Func<int, bool> isPressed)
        {
            if (_cfg.ButtonActions == null || isPressed == null) return false;
            var kind = side == IndicatorSide.Left ? StalkActionKind.IndicatorLeft : StalkActionKind.IndicatorRight;
            foreach (var kv in _cfg.ButtonActions)
                if (kv.Value?.Kind == kind && isPressed(kv.Key)) return true;
            return false;
        }

        private string SideKey(IndicatorSide side)
            => side == IndicatorSide.Left ? _cfg.IndicatorLeftKey
             : side == IndicatorSide.Right ? _cfg.IndicatorRightKey
             : "";

        private static IndicatorSide Other(IndicatorSide side)
            => side == IndicatorSide.Left ? IndicatorSide.Right : IndicatorSide.Left;

        // ---- stage / toggle convergence ------------------------------------------

        private void ConvergeWiper(int target, List<StalkKeyCommand> output)
        {
            // No back key (ACC's single wiper cycle): step forward around the ring.
            bool hasBack = !string.IsNullOrEmpty(_cfg.WiperBackKey);
            var steps = StageCycle.PlanSteps(_wiperStage, target, _cfg.WiperStageCount, _cfg.WiperForwardWraps || !hasBack, hasBack);
            foreach (var s in steps)
                Tap(s > 0 ? _cfg.WiperForwardKey : _cfg.WiperBackKey, output);
            _wiperStage = WrapStage(target, _cfg.WiperStageCount);
        }

        private void ConvergeInterval(int target, List<StalkKeyCommand> output)
        {
            // Faster key steps up; no slower key → step forward around the ring.
            bool hasBack = !string.IsNullOrEmpty(_cfg.WiperIntervalSlowerKey);
            var steps = StageCycle.PlanSteps(_intervalStage, target, _cfg.WiperIntervalStageCount, !hasBack, hasBack);
            foreach (var s in steps)
                Tap(s > 0 ? _cfg.WiperIntervalFasterKey : _cfg.WiperIntervalSlowerKey, output);
            _intervalStage = WrapStage(target, _cfg.WiperIntervalStageCount);
        }

        private void ConvergeLight(int target, List<StalkKeyCommand> output)
        {
            // Light knob uses a single forward-only cycle key (wraps).
            var steps = StageCycle.PlanSteps(_lightStage, target, _cfg.LightStageCount, wrap: true, hasBackKey: false);
            foreach (var _ in steps)
                Tap(_cfg.LightCycleKey, output);
            _lightStage = WrapStage(target, _cfg.LightStageCount);
        }

        private void ConvergeHighBeam(List<StalkKeyCommand> output)
        {
            bool want = _hbWant ?? _highBeam;
            if (want == _highBeam) return;
            Tap(_cfg.HighBeamKey, output);
            _highBeam = want;
        }

        private void ConvergeToggle(string key, List<StalkKeyCommand> output)
        {
            if (!_toggleLever.TryGetValue(key, out bool want) || want == GameToggle(key)) return;
            Tap(key, output);
            _toggleGame[key] = want;
        }

        private bool GameToggle(string key) => _toggleGame.TryGetValue(key, out bool on) && on;

        // ---- key output ------------------------------------------------------------

        private StalkAction? ActionFor(int index)
        {
            var map = _cfg.ButtonActions;
            return map != null && map.TryGetValue(index, out var a) ? a : null;
        }

        private static void Tap(string key, List<StalkKeyCommand> output)
        {
            if (!string.IsNullOrEmpty(key)) output.Add(new StalkKeyCommand(StalkKeyOp.Tap, key));
        }

        private void Down(string key, List<StalkKeyCommand> output)
        {
            if (string.IsNullOrEmpty(key)) return;
            _down.Add(key);
            output.Add(new StalkKeyCommand(StalkKeyOp.Down, key));
        }

        private void Up(string key, List<StalkKeyCommand> output)
        {
            if (string.IsNullOrEmpty(key) || !_down.Remove(key)) return;
            output.Add(new StalkKeyCommand(StalkKeyOp.Up, key));
        }

        private void ReleaseAll(List<StalkKeyCommand> output)
        {
            if (_down.Count == 0) return;
            _down.Clear();
            output.Add(new StalkKeyCommand(StalkKeyOp.ReleaseAll, ""));
        }

        internal static int WrapStage(int stage, int count)
        {
            if (count <= 0) return 0;
            int r = stage % count;
            return r < 0 ? r + count : r;
        }
    }
}
