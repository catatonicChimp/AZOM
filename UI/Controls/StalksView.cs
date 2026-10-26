using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MozaPlugin.Devices.StalksTruckSim;

namespace MozaControls
{
    /// <summary>
    /// Live picture of the MOZA stalks: indicators, light knob + high beam,
    /// wipers and cruise control. Filled marks are what the plugin believes the game shows; outlined
    /// marks are where the physical lever sits, so a mismatch (the game still to be
    /// caught up) is visible at a glance. Poll() supplies the latest snapshot.
    /// </summary>
    public class StalksView : FrameworkElement
    {
        public Func<StalkStateSnapshot?>? Poll { get; set; }

        // Section captions, set from code so they follow the plugin language.
        public string IndicatorsLabel { get; set; } = "Indicators";
        public string LightsLabel { get; set; } = "Lights";
        public string WipersLabel { get; set; } = "Wipers";
        public string HighBeamLabel { get; set; } = "High beam";
        public string SwipeLabel { get; set; } = "Swipe";
        public string TogglesLabel { get; set; } = "On: {0}";
        public string CruiseLabel { get; set; } = "Cruise";
        public string OnLabel { get; set; } = "On";
        public string OffLabel { get; set; } = "Off";

        // Typical car blinker rate, ~1.5 Hz.
        private const int BlinkHalfPeriodMs = 330;

        private static readonly Color Track = Color.FromRgb(0x2E, 0x37, 0x3D);
        private static readonly Color Text = Color.FromRgb(0xBB, 0xC4, 0xCC);
        private static readonly Color Muted = Color.FromRgb(0x6A, 0x73, 0x7C);
        private static readonly Color Cyan = Color.FromRgb(0x00, 0xE5, 0xFF);
        private static readonly Color Green = Color.FromRgb(0x39, 0xFF, 0x88);
        private static readonly Color Amber = Color.FromRgb(0xFF, 0xB8, 0x4D);
        private static readonly Color Blue = Color.FromRgb(0x4D, 0x8C, 0xFF);

        private readonly Brush _track, _text, _muted, _cyan, _cyanGlow, _green, _greenGlow, _amber, _blue, _blueGlow;
        private readonly Pen _trackPen, _cyanPen, _greenPen;
        private readonly Typeface _face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private bool _hooked;
        private StalkStateSnapshot? _state;
        private bool _blinkOn;

        public StalksView()
        {
            _track = Frozen(Track);
            _text = Frozen(Text);
            _muted = Frozen(Muted);
            _cyan = Frozen(Cyan);
            _cyanGlow = Frozen(Color.FromArgb(0x55, Cyan.R, Cyan.G, Cyan.B));
            _green = Frozen(Green);
            _greenGlow = Frozen(Color.FromArgb(0x55, Green.R, Green.G, Green.B));
            _amber = Frozen(Amber);
            _blue = Frozen(Blue);
            _blueGlow = Frozen(Color.FromArgb(0x55, Blue.R, Blue.G, Blue.B));
            _trackPen = FrozenPen(_track, 1.5);
            _cyanPen = FrozenPen(_cyan, 2);
            _greenPen = FrozenPen(_green, 2);
            Loaded += (_, __) => Hook();
            Unloaded += (_, __) => Unhook();
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Pen FrozenPen(Brush b, double width)
        {
            var p = new Pen(b, width);
            p.Freeze();
            return p;
        }

        private void Hook() { if (!_hooked) { CompositionTarget.Rendering += OnFrame; _hooked = true; } }
        private void Unhook() { if (_hooked) { CompositionTarget.Rendering -= OnFrame; _hooked = false; } }

        private void OnFrame(object? sender, EventArgs e)
        {
            if (!IsVisible || Poll == null) return;
            var s = Poll() ?? StalkStateSnapshot.Empty;
            bool blink = s.IndicatorLit != IndicatorSide.None
                && (Environment.TickCount / BlinkHalfPeriodMs) % 2 == 0;
            if (ReferenceEquals(_state, s) && _blinkOn == blink) return;
            _state = s;
            _blinkOn = blink;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            var s = _state ?? StalkStateSnapshot.Empty;

            // Four panels: indicators · lights · wipers · cruise (narrower).
            double col = w / 3.6;
            double unit = Math.Max(10, Math.Min(col / 7, h / 6));
            DrawIndicators(dc, s, new Rect(0, 0, col, h), unit);
            DrawLights(dc, s, new Rect(col, 0, col, h), unit);
            DrawWipers(dc, s, new Rect(2 * col, 0, col, h), unit);
            DrawCruise(dc, s, new Rect(3 * col, 0, w - 3 * col, h), unit);
        }

        private void DrawIndicators(DrawingContext dc, StalkStateSnapshot s, Rect r, double u)
        {
            Caption(dc, IndicatorsLabel, r, u);
            double cy = r.Top + r.Height * 0.48;
            double cx = r.Left + r.Width / 2;
            double gap = u * 1.6;
            DrawArrow(dc, new Point(cx - gap, cy), u * 1.3, left: true,
                lit: s.IndicatorLit == IndicatorSide.Left && _blinkOn,
                lever: s.IndicatorLever == IndicatorSide.Left);
            DrawArrow(dc, new Point(cx + gap, cy), u * 1.3, left: false,
                lit: s.IndicatorLit == IndicatorSide.Right && _blinkOn,
                lever: s.IndicatorLever == IndicatorSide.Right);

            // Lever detents underneath: L · N · R.
            double dy = r.Top + r.Height * 0.80;
            var labels = new[] { "L", "N", "R" };
            var sides = new[] { IndicatorSide.Left, IndicatorSide.None, IndicatorSide.Right };
            for (int i = 0; i < 3; i++)
            {
                var c = new Point(cx + (i - 1) * gap, dy);
                bool at = s.IndicatorLever == sides[i];
                Pill(dc, c, u * 0.62, labels[i], filled: false, ring: at, glowAmber: at && i == 1 && s.IndicatorCancelPending);
            }
        }

        private void DrawLights(DrawingContext dc, StalkStateSnapshot s, Rect r, double u)
        {
            Caption(dc, LightsLabel, r, u);
            int n = Math.Max(1, s.LightStageCount);
            double cy = r.Top + r.Height * 0.48;
            double pitch = Math.Min(u * 1.7, (r.Width - u) / n);
            double x0 = r.Left + r.Width / 2 - (n - 1) * pitch / 2;
            for (int i = 0; i < n; i++)
                Pill(dc, new Point(x0 + i * pitch, cy), u * 0.7, i.ToString(CultureInfo.InvariantCulture),
                    filled: s.LightStage == i, ring: s.LightLever == i, glowAmber: false);

            // High beam: blue headlight glyph, lit while on or flashing.
            double hy = r.Top + r.Height * 0.80;
            bool on = s.HighBeamOn || s.FlashHeld;
            var hc = new Point(r.Left + r.Width / 2 - u * 2.2, hy);
            DrawHighBeam(dc, hc, u * 0.6, on, s.HighBeamLever == HighBeamLever.On || s.HighBeamLever == HighBeamLever.Flash);
            var ft = Label(HighBeamLabel + (s.FlashHeld ? " ⚡" : ""), u * 0.85, on ? _blue : _muted);
            dc.DrawText(ft, new Point(hc.X + u * 1.0, hy - ft.Height / 2));

            if (s.TogglesOn.Count > 0)
            {
                var names = new List<string>();
                foreach (var k in s.TogglesOn) names.Add(KeyCodes.DisplayName(KeyCodes.Parse(k)));
                var tt = Label(string.Format(TogglesLabel, string.Join(", ", names)), u * 0.75, _green);
                dc.DrawText(tt, new Point(r.Left + (r.Width - tt.Width) / 2, r.Bottom - tt.Height - 2));
            }
        }

        private void DrawWipers(DrawingContext dc, StalkStateSnapshot s, Rect r, double u)
        {
            Caption(dc, WipersLabel, r, u);
            int n = Math.Max(1, s.WiperStageCount);
            double cy = r.Top + r.Height * 0.48;
            double pitch = Math.Min(u * 1.7, (r.Width - u) / n);
            double x0 = r.Left + r.Width / 2 - (n - 1) * pitch / 2;
            for (int i = 0; i < n; i++)
                Pill(dc, new Point(x0 + i * pitch, cy), u * 0.7, i.ToString(CultureInfo.InvariantCulture),
                    filled: s.WiperStage == i, ring: s.WiperLever == i && !s.WiperSwipe, glowAmber: false);

            double sy = r.Top + r.Height * 0.80;
            var ft = Label(SwipeLabel, u * 0.85, s.WiperSwipe ? _cyan : _muted);
            dc.DrawText(ft, new Point(r.Left + (r.Width - ft.Width) / 2, sy - ft.Height / 2));
        }

        private void DrawCruise(DrawingContext dc, StalkStateSnapshot s, Rect r, double u)
        {
            Caption(dc, CruiseLabel, r, u);
            double cx = r.Left + r.Width / 2;
            double cy = r.Top + r.Height * 0.48;
            // Lever up / down arrows, lit while held.
            DrawChevron(dc, new Point(cx, cy - u * 0.9), u * 0.7, up: true, lit: s.CruiseLever > 0);
            DrawChevron(dc, new Point(cx, cy + u * 0.9), u * 0.7, up: false, lit: s.CruiseLever < 0);
            var ft = Label((s.CruiseActive ? OnLabel : OffLabel).ToUpperInvariant(), u * 0.85, s.CruiseActive ? _green : _muted);
            dc.DrawText(ft, new Point(cx - ft.Width / 2, r.Top + r.Height * 0.80 - ft.Height / 2));
        }

        // ---- primitives ------------------------------------------------------------

        private void DrawChevron(DrawingContext dc, Point c, double size, bool up, bool lit)
        {
            double dir = up ? -1 : 1;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X - size, c.Y - dir * size * 0.4), true, true);
                ctx.LineTo(new Point(c.X, c.Y + dir * size * 0.5), true, false);
                ctx.LineTo(new Point(c.X + size, c.Y - dir * size * 0.4), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(lit ? _cyan : _track, null, g);
        }

        private void Caption(DrawingContext dc, string text, Rect r, double u)
        {
            var ft = Label(text.ToUpperInvariant(), u * 0.8, _muted);
            dc.DrawText(ft, new Point(r.Left + (r.Width - ft.Width) / 2, r.Top + 2));
        }

        /// <summary>A stage marker: filled = game state, ring = lever position.</summary>
        private void Pill(DrawingContext dc, Point c, double radius, string text, bool filled, bool ring, bool glowAmber)
        {
            if (glowAmber)
                dc.DrawEllipse(null, FrozenPen(_amber, 2), c, radius + 3, radius + 3);
            if (filled)
            {
                dc.DrawEllipse(_cyanGlow, null, c, radius + 3, radius + 3);
                dc.DrawEllipse(_cyan, null, c, radius, radius);
            }
            else
            {
                dc.DrawEllipse(null, _trackPen, c, radius, radius);
            }
            if (ring)
                dc.DrawEllipse(null, filled ? FrozenPen(_text, 2) : _cyanPen, c, radius + (filled ? 0 : 1), radius + (filled ? 0 : 1));

            var ft = Label(text, radius * 1.05, filled ? Frozen(Color.FromRgb(0x12, 0x16, 0x1A)) : _text);
            dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
        }

        private void DrawArrow(DrawingContext dc, Point c, double size, bool left, bool lit, bool lever)
        {
            double dir = left ? -1 : 1;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X + dir * size, c.Y), true, true);
                ctx.LineTo(new Point(c.X, c.Y - size * 0.75), true, false);
                ctx.LineTo(new Point(c.X, c.Y - size * 0.32), true, false);
                ctx.LineTo(new Point(c.X - dir * size * 0.8, c.Y - size * 0.32), true, false);
                ctx.LineTo(new Point(c.X - dir * size * 0.8, c.Y + size * 0.32), true, false);
                ctx.LineTo(new Point(c.X, c.Y + size * 0.32), true, false);
                ctx.LineTo(new Point(c.X, c.Y + size * 0.75), true, false);
            }
            g.Freeze();
            if (lit)
            {
                dc.DrawGeometry(_greenGlow, FrozenPen(_greenGlow, 6), g);
                dc.DrawGeometry(_green, null, g);
            }
            else
            {
                dc.DrawGeometry(_track, lever ? _greenPen : null, g);
            }
        }

        private void DrawHighBeam(DrawingContext dc, Point c, double r, bool on, bool lever)
        {
            // Headlight lens (a "D") with three beam lines to the left.
            var lens = new StreamGeometry();
            using (var ctx = lens.Open())
            {
                ctx.BeginFigure(new Point(c.X, c.Y - r), true, true);
                ctx.ArcTo(new Point(c.X, c.Y + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            }
            lens.Freeze();
            var brush = on ? _blue : _track;
            if (on) dc.DrawEllipse(_blueGlow, null, new Point(c.X + r * 0.3, c.Y), r * 1.4, r * 1.4);
            dc.DrawGeometry(brush, lever && !on ? _cyanPen : null, lens);
            var pen = FrozenPen(brush, Math.Max(1.5, r * 0.22));
            for (int i = -1; i <= 1; i++)
            {
                double y = c.Y + i * r * 0.6;
                dc.DrawLine(pen, new Point(c.X - r * 0.35, y), new Point(c.X - r * 1.25, y));
            }
        }

        private FormattedText Label(string text, double size, Brush brush)
            => new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                _face, Math.Max(8, size), brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
