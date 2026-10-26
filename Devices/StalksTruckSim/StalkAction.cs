namespace MozaPlugin.Devices.StalksTruckSim
{
    /// <summary>
    /// Operating mode for the MOZA Multi-Function Stalks.
    /// </summary>
    public enum StalkMode
    {
        /// <summary>Default — the plugin does nothing; the stalks act as a raw
        /// 28-button HID joystick (bind them yourself in the game / SimHub).</summary>
        ButtonBox = 0,

        /// <summary>ETS2/ATS truck-sim keyboard emulation — the plugin translates
        /// stalk button presses into keyboard keys for the truck game.</summary>
        TruckSim = 1,
    }

    /// <summary>
    /// What a single stalk button does while in <see cref="StalkMode.TruckSim"/>.
    /// </summary>
    public enum StalkActionKind
    {
        /// <summary>Unassigned — the button does nothing.</summary>
        None = 0,

        /// <summary>Tap a keyboard key once on press (horn, hazard, indicators, …).
        /// The game handles any stateful behavior (e.g. indicator auto-cancel).</summary>
        Momentary = 1,

        /// <summary>Select a wiper stage: the controller steps the game's wiper to
        /// <see cref="StalkAction.Stage"/> using the forward/back cycle keys.</summary>
        WiperStage = 2,

        /// <summary>Select a light-knob stage: the controller steps the game's light
        /// mode to <see cref="StalkAction.Stage"/> using the light cycle key.</summary>
        LightStage = 3,

        /// <summary>Turn-signal stalk moved LEFT: taps the left-indicator key and
        /// remembers it as the active blinker (for <see cref="IndicatorCancel"/>).</summary>
        IndicatorLeft = 4,

        /// <summary>Turn-signal stalk moved RIGHT: taps the right-indicator key and
        /// remembers it as the active blinker.</summary>
        IndicatorRight = 5,

        /// <summary>Turn-signal stalk returned to neutral: re-taps whichever indicator
        /// key is active to cancel it (games where the indicator key toggles).</summary>
        IndicatorCancel = 6,

        /// <summary>Wiper "single swipe" position: pulses the wipers on then off
        /// (forward + back cycle tap) without changing the tracked stage.</summary>
        WiperSingleSwipe = 7,

        /// <summary>Hold a key down while the button is pressed and release it when the
        /// button is released (for a stalk position that reports a sustained hold).</summary>
        HeldKey = 8,

        /// <summary>Latch a key down on press and keep it held (auto-repeated) — NOT
        /// released on this button's own release. Released by a <see cref="ReleaseHeld"/>
        /// button. Use for a flash lever that pulses instead of reporting a hold: the
        /// key stays down until the stalk returns to neutral.</summary>
        LatchKey = 9,

        /// <summary>Release every currently-held/latched key. Map to the neutral stalk
        /// position so returning there drops the flash.</summary>
        ReleaseHeld = 10,

        /// <summary>High-beam lever pushed to a latched ON position (for a lever
        /// that stays there; see <see cref="HighBeamToggle"/> for a sprung one). The game's
        /// high-beam key is a toggle, so it is tapped only when the tracked game state
        /// is off — the lever position, not the press count, decides the outcome.</summary>
        HighBeamOn = 11,

        /// <summary>High-beam lever at neutral: releases a held flash, and switches the
        /// high beam off when the lever is leaving a latched ON position.</summary>
        HighBeamNeutral = 12,

        /// <summary>High-beam lever pulled to flash: holds the flash key down until the
        /// lever returns to <see cref="HighBeamNeutral"/>.</summary>
        HighBeamFlash = 13,

        /// <summary>Generic "switch on" position for a game control bound to a single
        /// toggle key (<see cref="StalkAction.Key"/>): taps the key only if the tracked
        /// state for that key is off. Pair with <see cref="ToggleOff"/> on the same key
        /// to drive a press-on/press-again-off game control from two stalk positions.</summary>
        ToggleOn = 14,

        /// <summary>Generic "switch off" position: taps <see cref="StalkAction.Key"/>
        /// only if the tracked state for that key is on.</summary>
        ToggleOff = 15,

        /// <summary>Cruise-control lever up. A short press resumes when cruise is
        /// tracked as off, otherwise steps the set speed up; holding it past the
        /// hold time sends the big-step key (or repeats the small step) until let go.</summary>
        CruiseUp = 16,

        /// <summary>Cruise-control lever down: like <see cref="CruiseUp"/>, with "set"
        /// in place of "resume" and steps going down.</summary>
        CruiseDown = 17,

        /// <summary>Cruise-control cancel: taps the cancel key and tracks cruise as off.</summary>
        CruiseCancel = 18,

        /// <summary>A latched switch reported as one button held while on (e.g. fog
        /// lights): pressing switches the game's toggle key <see cref="StalkAction.Key"/>
        /// on, releasing switches it off — both only when the tracked state differs.</summary>
        ToggleHold = 19,

        /// <summary>Spring-loaded high-beam push: each press flips the high beam
        /// (one tap of the high-beam key). Use instead of <see cref="HighBeamOn"/>
        /// when the forward position springs back, as on the MOZA stalks.</summary>
        HighBeamToggle = 20,

        /// <summary>Select an intermittent-wiper frequency stage: the controller steps
        /// the game's interval to <see cref="StalkAction.Stage"/> with the interval
        /// faster/slower keys.</summary>
        WiperIntervalStage = 21,

        /// <summary>Cruise-control main switch (on/off button): taps the main key and
        /// flips the tracked main state. Switching main off disengages cruise; switching
        /// it on engages it when the game's main key does that (ETS2/ATS).</summary>
        CruiseMainToggle = 22,
    }

    /// <summary>
    /// How the game's turn-signal keys behave, which decides what the indicator
    /// lever positions send.
    /// </summary>
    public enum IndicatorStyle
    {
        /// <summary>Tap to switch on, tap again to switch off (ETS2/ATS and most
        /// games). Neutral re-taps the lit side once the minimum blink time is up.</summary>
        Toggle = 0,

        /// <summary>The blinker is on only while its key is held: the side key is
        /// held down for as long as the lever sits in that position.</summary>
        Hold = 1,
    }

    /// <summary>
    /// Per-button configuration in truck-sim mode. Plain serializable POCO — stored
    /// in <c>MozaPluginSettings</c> as a <c>Dictionary&lt;int, StalkAction&gt;</c>
    /// keyed by 0-based stalk button index.
    /// </summary>
    public sealed class StalkAction
    {
        public StalkActionKind Kind { get; set; } = StalkActionKind.None;

        /// <summary>Key name for <see cref="StalkActionKind.Momentary"/> — resolved to a
        /// scan code by <c>KeyboardSender</c> (e.g. "Comma", "Period", "H", "F", "K").</summary>
        public string Key { get; set; } = "";

        /// <summary>Target stage index (0-based) for
        /// <see cref="StalkActionKind.WiperStage"/> / <see cref="StalkActionKind.LightStage"/>.</summary>
        public int Stage { get; set; }

        public StalkAction Clone() => new StalkAction { Kind = Kind, Key = Key, Stage = Stage };
    }
}
