using System;
using System.Collections.Generic;
using System.Linq;
using MozaPlugin.Devices.StalksTruckSim;
using MozaPlugin.Settings;

static class Program
{
    static int fails, passes;
    static StalkStateMachine M(Action<StalkTruckSimSettings>? tweak = null)
    {
        var cfg = new StalkTruckSimSettings();
        cfg.ApplyEts2Defaults();
        cfg.ButtonActions[10] = new StalkAction { Kind = StalkActionKind.ToggleOn, Key = "B" };
        cfg.ButtonActions[11] = new StalkAction { Kind = StalkActionKind.ToggleOff, Key = "B" };
        cfg.ButtonActions[12] = new StalkAction { Kind = StalkActionKind.HeldKey, Key = "H" };
        tweak?.Invoke(cfg);
        var m = new StalkStateMachine();
        m.Configure(cfg);
        return m;
    }
    static string P(StalkStateMachine m, int idx, long t = 0, bool deliver = true)
    { var o = new List<StalkKeyCommand>(); m.OnPress(idx, t, deliver, o); return string.Join(" ", o); }
    static string R(StalkStateMachine m, int idx, long t = 0, bool deliver = true)
    { var o = new List<StalkKeyCommand>(); m.OnRelease(idx, t, deliver, o); return string.Join(" ", o); }
    static string T(StalkStateMachine m, long t, bool deliver = true, Func<int,bool>? raw = null)
    { var o = new List<StalkKeyCommand>(); m.Tick(t, deliver, raw ?? (_ => false), o); return string.Join(" ", o); }
    static string Rec(StalkStateMachine m)
    { var o = new List<StalkKeyCommand>(); m.Reconcile(o); return string.Join(" ", o); }
    static void Eq(string name, object? got, object? want)
    {
        if (Equals(got?.ToString(), want?.ToString())) passes++;
        else { fails++; Console.WriteLine($"FAIL {name}: got '{got}' want '{want}'"); }
    }

    static void Main()
    {
        // High beam: latched lever drives a toggle key by position, not by press count.
        var m = M(c => c.ButtonActions[3] = new StalkAction { Kind = StalkActionKind.HighBeamOn });
        Eq("hb on", P(m, 3), "Tap:K");
        Eq("hb on again", P(m, 3), "");
        Eq("hb neutral", P(m, 4), "Tap:K");
        Eq("hb neutral again", P(m, 4), "");
        Eq("flash", P(m, 5), "Down:J");
        Eq("flash snapshot", m.Snapshot().FlashHeld, true);
        Eq("flash -> neutral", P(m, 4), "Up:J");
        Eq("flash released", m.Snapshot().FlashHeld, false);

        // Indicators, toggle style, 3 s minimum blink.
        m = M();
        Eq("left on", P(m, 9, 0), "Tap:[");
        Eq("quick neutral defers", P(m, 8, 500), "");
        Eq("deadline", m.PendingDeadlineMs, 3000);
        Eq("tick early", T(m, 2999), "");
        Eq("tick due", T(m, 3000), "Tap:[");
        Eq("off", m.Snapshot().IndicatorLit, IndicatorSide.None);

        m = M();
        P(m, 9, 0);
        Eq("late neutral cancels now", P(m, 8, 5000), "Tap:[");

        m = M();
        P(m, 9, 0);
        Eq("swap sides", P(m, 7, 4000), "Tap:[ Tap:]");
        Eq("right lit", m.Snapshot().IndicatorLit, IndicatorSide.Right);

        m = M();
        P(m, 9, 0); P(m, 8, 500);
        Eq("reflick keeps lit", P(m, 9, 800), "");
        Eq("pending cleared", m.PendingDeadlineMs, 0);
        Eq("neutral re-defers", P(m, 8, 1000), "");
        Eq("deadline from first on", m.PendingDeadlineMs, 3000);
        Eq("tick", T(m, 3000), "Tap:[");

        m = M();
        P(m, 9, 0); P(m, 8, 500);
        Eq("lever back at side keeps lit", T(m, 3000, true, i => i == 9), "");
        Eq("still lit", m.Snapshot().IndicatorLit, IndicatorSide.Left);

        m = M();
        P(m, 9, 0); P(m, 8, 500);
        Eq("undeliverable tick", T(m, 3000, deliver: false), "");
        Eq("left tracked lit", m.Snapshot().IndicatorLit, IndicatorSide.Left);
        Eq("next neutral cancels", P(m, 8, 6000), "Tap:[");

        m = M();
        Eq("left undelivered", P(m, 9, 0, deliver: false), "");
        Eq("lever moved", m.Snapshot().IndicatorLever, IndicatorSide.Left);
        Eq("game untouched", m.Snapshot().IndicatorLit, IndicatorSide.None);
        Eq("indicators not reconciled", m.NeedsReconcile, false);

        // Indicators, hold style.
        m = M(c => c.IndicatorStyle = IndicatorStyle.Hold);
        Eq("hold left", P(m, 9), "Down:[");
        Eq("hold swap", P(m, 7), "Up:[ Down:]");
        Eq("hold neutral", P(m, 8), "Up:]");
        Eq("hold neutral again", P(m, 8), "");

        // Wipers (4 stages, back key, no wrap) and single swipe.
        m = M();
        Eq("wiper low", P(m, 22), "Tap:P Tap:P");
        Eq("wiper off", P(m, 20), "Tap:Minus Tap:Minus");
        Eq("swipe", P(m, 19), "Tap:P");
        Eq("swipe no reconcile", m.NeedsReconcile, false);
        Eq("swipe return", P(m, 20), "Tap:Minus");

        // Lights (3 stages, forward-only wrap).
        m = M();
        Eq("lights head", P(m, 2), "Tap:L Tap:L");
        Eq("lights off wraps", P(m, 0), "Tap:L");

        // Catch-up after moving levers while alt-tabbed.
        m = M(c => c.ButtonActions[3] = new StalkAction { Kind = StalkActionKind.HighBeamOn });
        Eq("undelivered light", P(m, 2, deliver: false), "");
        Eq("undelivered wiper", P(m, 22, deliver: false), "");
        Eq("undelivered hb", P(m, 3, deliver: false), "");
        Eq("undelivered toggle", P(m, 10, deliver: false), "");
        Eq("needs reconcile", m.NeedsReconcile, true);
        Eq("reconcile", Rec(m), "Tap:P Tap:P Tap:L Tap:L Tap:K Tap:B");
        Eq("in line", m.NeedsReconcile, false);
        Eq("reconcile idempotent", Rec(m), "");

        // Generic toggle pair.
        m = M();
        Eq("toggle on", P(m, 10), "Tap:B");
        Eq("toggle on again", P(m, 10), "");
        Eq("toggle off", P(m, 11), "Tap:B");
        Eq("toggle off again", P(m, 11), "");

        // Held key: no stray key-up when nothing was pressed.
        m = M();
        Eq("held undelivered", P(m, 12, deliver: false), "");
        Eq("no stray up", R(m, 12), "");
        Eq("held", P(m, 12), "Down:H");
        Eq("held up", R(m, 12), "Up:H");

        // Re-sync: wipers driven off, then levers caught up.
        m = M();
        P(m, 21); P(m, 1);
        var o = new List<StalkKeyCommand>(); m.ResyncToRest(true, o);
        Eq("resync", string.Join(" ", o), "Tap:Minus Tap:Minus Tap:Minus");
        Eq("resync then reconcile", Rec(m), "Tap:P Tap:L");

        // Stage-count change keeps trackers in range.
        m = M();
        P(m, 23);
        var cfg2 = m.Config.Clone(); cfg2.WiperStageCount = 3; m.Configure(cfg2);
        Eq("wrapped", m.Snapshot().WiperStage < 3, true);

        // Cruise control: short press resumes/sets when off, steps when on; hold = big steps.
        Action<StalkTruckSimSettings> cruise = c =>
        {
            c.ButtonActions[13] = new StalkAction { Kind = StalkActionKind.CruiseUp };
            c.ButtonActions[14] = new StalkAction { Kind = StalkActionKind.CruiseDown };
            c.ButtonActions[15] = new StalkAction { Kind = StalkActionKind.CruiseCancel };
            c.CruiseResumeKey = "R"; c.CruiseSetKey = "S"; c.CruiseUpKey = "U"; c.CruiseDownKey = "D";
            c.CruiseUpBigKey = "PageUp"; c.CruiseCancelKey = "C";
        };
        m = M(cruise);
        Eq("cruise up press silent", P(m, 13, 0), "");
        Eq("deadline hold", m.PendingDeadlineMs, 500);
        Eq("short up resumes", R(m, 13, 200), "Tap:R");
        Eq("active", m.Snapshot().CruiseActive, true);
        P(m, 13, 1000);
        Eq("short up steps", R(m, 13, 1100), "Tap:U");
        P(m, 13, 2000);
        Eq("hold early", T(m, 2400), "");
        Eq("hold fires big", T(m, 2500), "Tap:PageUp");
        Eq("repeat due", m.PendingDeadlineMs, 2900);
        Eq("hold repeats", T(m, 2900), "Tap:PageUp");
        Eq("release after hold silent", R(m, 13, 3000), "");
        Eq("no deadline", m.PendingDeadlineMs, 0);
        P(m, 14, 4000);
        Eq("hold down no big key repeats small", T(m, 4500), "Tap:D");
        R(m, 14, 4600);
        Eq("cancel", P(m, 15, 5000), "Tap:C");
        Eq("inactive", m.Snapshot().CruiseActive, false);
        P(m, 14, 6000);
        Eq("short down sets", R(m, 14, 6100), "Tap:S");
        m.ResetIndicator();
        P(m, 13, 7000);
        Eq("hold while off engages first", T(m, 7500), "Tap:R");
        Eq("then big steps", T(m, 7900), "Tap:PageUp");
        R(m, 13, 8000);
        P(m, 13, 9000, deliver: false);
        Eq("undeliverable short", R(m, 13, 9100, deliver: false), "");

        // Forward-only wiper cycle (no back key) wraps forward.
        m = M(c => c.WiperBackKey = "");
        Eq("fwd only up", P(m, 22), "Tap:P Tap:P");
        Eq("fwd only to off wraps", P(m, 20), "Tap:P Tap:P");

        // Held switch (fog lights on button 7): on while held, off on release.
        m = M(c => c.ButtonActions[6] = new StalkAction { Kind = StalkActionKind.ToggleHold, Key = "F" });
        Eq("fog on", P(m, 6), "Tap:F");
        Eq("fog on snapshot", string.Join(",", m.Snapshot().TogglesOn), "F");
        Eq("fog off", R(m, 6), "Tap:F");
        P(m, 6, deliver: false);
        Eq("fog catch-up", Rec(m), "Tap:F");
        R(m, 6, deliver: false);
        Eq("fog catch-up off", Rec(m), "Tap:F");

        // Sprung high-beam push (MOZA, ETS2 defaults): each push toggles; neutral leaves it.
        m = M();
        Eq("push on", P(m, 3), "Tap:K");
        Eq("spring back keeps on", P(m, 4), "");
        Eq("still on", m.Snapshot().HighBeamOn, true);
        Eq("push off", P(m, 3), "Tap:K");
        Eq("flash while off", P(m, 5), "Down:J");
        Eq("flash release", P(m, 4), "Up:J");
        Eq("toggle needs no reconcile", m.NeedsReconcile, false);
        P(m, 3, deliver: false);
        Eq("undelivered push caught up", Rec(m), "Tap:K");
        P(m, 3, deliver: false); P(m, 3, deliver: false);
        Eq("double undelivered push cancels", Rec(m), "");

        // Intermittent frequency stages (buttons 14-18).
        Action<StalkTruckSimSettings> iv = c =>
        {
            for (int i = 0; i < 5; i++) c.ButtonActions[13 + i] = new StalkAction { Kind = StalkActionKind.WiperIntervalStage, Stage = i };
            c.WiperIntervalFasterKey = "Up"; c.WiperIntervalSlowerKey = "Down";
        };
        m = M(iv);
        Eq("interval 3", P(m, 16), "Tap:Up Tap:Up Tap:Up");
        Eq("interval 1", P(m, 14), "Tap:Down Tap:Down");
        P(m, 17, deliver: false);
        Eq("interval catch-up", Rec(m), "Tap:Up Tap:Up Tap:Up");
        m = M(c => { iv(c); c.WiperIntervalSlowerKey = ""; });
        P(m, 15);
        Eq("interval fwd-only wraps", P(m, 13), "Tap:Up Tap:Up Tap:Up");

        // Cruise main switch, ETS2 defaults (btn25 main C, 26 set/-, 27 res/+, 28 cancel).
        m = M(c => { c.CruiseUpKey = "U"; c.CruiseDownKey = "D"; });
        Eq("main engages", P(m, 24), "Tap:C");
        Eq("engaged", m.Snapshot().CruiseActive, true);
        P(m, 26, 0);
        Eq("res+ steps when engaged", R(m, 26, 100), "Tap:U");
        Eq("cancel via main key", P(m, 27), "Tap:C");
        Eq("cancelled", m.Snapshot().CruiseActive, false);
        Eq("cancel when off sends nothing", P(m, 27), "");
        P(m, 24);
        Eq("main again toggles off", P(m, 24), "Tap:C");
        Eq("main off", m.Snapshot().CruiseMain, false);

        // Car-style main (arms only) with separate keys.
        m = M(c => { c.CruiseMainEngages = false; c.CruiseMainKey = "M"; c.CruiseSetKey = "S"; c.CruiseCancelKey = "X"; });
        Eq("arm", P(m, 24), "Tap:M");
        Eq("armed not active", m.Snapshot().CruiseActive, false);
        Eq("armed", m.Snapshot().CruiseMain, true);
        P(m, 25, 0);
        Eq("set engages", R(m, 25, 100), "Tap:S");
        Eq("cancel key", P(m, 27), "Tap:X");
        Eq("main stays armed", m.Snapshot().CruiseMain, true);
        Eq("disarm", P(m, 24), "Tap:M");
        Eq("off", m.Snapshot().CruiseMain, false);

        // Sprung controls as reported by the hardware: held while pressed, then release.
        m = M();
        Eq("swipe press", P(m, 19), "Tap:P");
        Eq("swipe held, no reconcile", m.NeedsReconcile, false);
        Eq("swipe release steps back", R(m, 19), "Tap:Minus");
        Eq("off pulse after release no-op", P(m, 20), "");
        m = M();
        P(m, 21);
        Eq("swipe from intermittent", P(m, 19), "Tap:P");
        Eq("swipe release back to intermittent", R(m, 19), "Tap:Minus");
        m = M();
        Eq("flash press", P(m, 5), "Down:J");
        Eq("flash release", R(m, 5), "Up:J");
        Eq("neutral after release no-op", P(m, 4), "");

        // Fog push toggle (button 7 springs back).
        m = M(c => c.ButtonActions[6] = new StalkAction { Kind = StalkActionKind.ToggleFlip, Key = "F" });
        Eq("fog push on", P(m, 6), "Tap:F");
        Eq("fog release nothing", R(m, 6), "");
        Eq("fog tracked", string.Join(",", m.Snapshot().TogglesOn), "F");
        Eq("fog push off", P(m, 6), "Tap:F");
        P(m, 6, deliver: false);
        Eq("fog push caught up", Rec(m), "Tap:F");

        // Process list parsing.
        Console.WriteLine($"{passes} passed, {fails} failed");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
