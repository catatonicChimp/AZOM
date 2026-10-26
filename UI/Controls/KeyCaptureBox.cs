using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MozaPlugin.Devices.StalksTruckSim;
using MozaPlugin.Resources;

namespace MozaControls
{
    /// <summary>
    /// Click-to-arm key binder. Shows the layout-aware name of the bound key
    /// (<see cref="KeyCode"/>, 0 = not set); while armed, the next physical key
    /// press is captured. Escape or focus loss cancels. Right-click opens a menu
    /// to clear the binding or bind a SimHub Control Mapper button role instead of
    /// a key (<see cref="RoleSource"/>). The binding is <see cref="StoredKey"/> —
    /// a key string, or <see cref="StalkOutput.RolePrefix"/> + role name.
    /// <see cref="KeyChanged"/> fires only on a user capture, pick or clear,
    /// never on programmatic changes.
    /// </summary>
    public class KeyCaptureBox : Control
    {
        static KeyCaptureBox()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(KeyCaptureBox),
                new FrameworkPropertyMetadata(typeof(KeyCaptureBox)));
        }

        public KeyCaptureBox()
        {
            Focusable = true;
        }

        /// <summary>Lists the Control Mapper button roles offered in the context menu.</summary>
        public static Func<IReadOnlyList<string>>? RoleSource { get; set; }

        public static readonly DependencyProperty StoredKeyProperty =
            DependencyProperty.Register(nameof(StoredKey), typeof(string), typeof(KeyCaptureBox),
                new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    (d, _) => ((KeyCaptureBox)d).OnStoredKeyChanged()));

        /// <summary>The stored binding: a key string ("sc:1E", legacy names), a
        /// "role:" Control Mapper role, or "" when unset.</summary>
        public string StoredKey
        {
            get => (string)GetValue(StoredKeyProperty) ?? "";
            set => SetValue(StoredKeyProperty, value ?? "");
        }

        private bool _syncing;

        private void OnStoredKeyChanged()
        {
            if (_syncing) return;
            _syncing = true;
            try { SetCurrentValue(KeyCodeProperty, (int)KeyCodes.Parse(StoredKey)); }
            finally { _syncing = false; }
            RefreshDisplayText();
        }

        private void Commit(string stored)
        {
            if (stored == StoredKey) return;
            _syncing = true;
            try
            {
                SetCurrentValue(StoredKeyProperty, stored);
                SetCurrentValue(KeyCodeProperty, (int)KeyCodes.Parse(stored));
            }
            finally { _syncing = false; }
            RefreshDisplayText();
            RaiseEvent(new RoutedEventArgs(KeyChangedEvent));
        }

        public static readonly DependencyProperty KeyCodeProperty =
            DependencyProperty.Register(nameof(KeyCode), typeof(int), typeof(KeyCaptureBox),
                new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    (d, _) => ((KeyCaptureBox)d).OnKeyCodeChanged()));

        // A KeyCode set from code (not via StoredKey) rebinds to that key.
        private void OnKeyCodeChanged()
        {
            if (!_syncing)
            {
                _syncing = true;
                try { SetCurrentValue(StoredKeyProperty, KeyCodes.Encode((ushort)KeyCode)); }
                finally { _syncing = false; }
            }
            RefreshDisplayText();
        }

        /// <summary>Bound scan code (Set-1; extended keys carry 0xE0 in the high byte). 0 = not set.</summary>
        public int KeyCode
        {
            get => (int)GetValue(KeyCodeProperty);
            set => SetValue(KeyCodeProperty, value);
        }

        private static readonly DependencyPropertyKey IsCapturingKey =
            DependencyProperty.RegisterReadOnly(nameof(IsCapturing), typeof(bool), typeof(KeyCaptureBox),
                new PropertyMetadata(false, (d, _) => ((KeyCaptureBox)d).RefreshDisplayText()));
        public static readonly DependencyProperty IsCapturingProperty = IsCapturingKey.DependencyProperty;
        public bool IsCapturing => (bool)GetValue(IsCapturingProperty);

        private static readonly DependencyPropertyKey DisplayTextKey =
            DependencyProperty.RegisterReadOnly(nameof(DisplayText), typeof(string), typeof(KeyCaptureBox),
                new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty DisplayTextProperty = DisplayTextKey.DependencyProperty;
        public string DisplayText => (string)GetValue(DisplayTextProperty);

        public static readonly RoutedEvent KeyChangedEvent =
            EventManager.RegisterRoutedEvent(nameof(KeyChanged), RoutingStrategy.Bubble,
                typeof(RoutedEventHandler), typeof(KeyCaptureBox));

        /// <summary>Raised when the user captures a different key or clears it.</summary>
        public event RoutedEventHandler KeyChanged
        {
            add => AddHandler(KeyChangedEvent, value);
            remove => RemoveHandler(KeyChangedEvent, value);
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            RefreshDisplayText();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            SetValue(IsCapturingKey, true);
            e.Handled = true;
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            SetValue(IsCapturingKey, false);
            e.Handled = true;

            var menu = new ContextMenu { PlacementTarget = this };
            var clear = new MenuItem { Header = Strings.KeyCapture_Clear, IsEnabled = StoredKey.Length > 0 };
            clear.Click += (_, __) => Commit("");
            menu.Items.Add(clear);
            menu.Items.Add(new Separator());
            IReadOnlyList<string>? roles = null;
            try { roles = RoleSource?.Invoke(); } catch { }
            if (roles == null || roles.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = Strings.KeyCapture_NoRoles, IsEnabled = false });
            }
            else
            {
                menu.Items.Add(new MenuItem { Header = Strings.KeyCapture_RolesHeader, IsEnabled = false });
                foreach (var role in roles)
                {
                    string stored = StalkOutput.RolePrefix + role;
                    var item = new MenuItem { Header = role, IsCheckable = true, IsChecked = stored == StoredKey };
                    item.Click += (_, __) => Commit(stored);
                    menu.Items.Add(item);
                }
            }
            menu.IsOpen = true;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (!IsCapturing)
            {
                if (e.Key == Key.Enter || e.Key == Key.Space)
                {
                    SetValue(IsCapturingKey, true);
                    e.Handled = true;
                }
                else base.OnPreviewKeyDown(e);
                return;
            }

            e.Handled = true;
            if (e.IsRepeat) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.None || key == Key.ImeProcessed || key == Key.DeadCharProcessed) return;
            if (key == Key.Escape) { SetValue(IsCapturingKey, false); return; }
            if (key == Key.LWin || key == Key.RWin) return;

            ushort code = KeyCodes.FromVirtualKey(KeyInterop.VirtualKeyFromKey(key));
            if (code == 0) return;
            SetValue(IsCapturingKey, false);
            if (code != KeyCode || StalkOutput.IsRole(StoredKey))
                Commit(KeyCodes.Encode(code));
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            if (IsCapturing) e.Handled = true;
            else base.OnPreviewKeyUp(e);
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            SetValue(IsCapturingKey, false);
            base.OnLostKeyboardFocus(e);
        }

        private void RefreshDisplayText()
        {
            string text = IsCapturing ? Strings.KeyCapture_Prompt
                : StalkOutput.DisplayName(StoredKey) is string name && name.Length > 0 ? name
                : KeyCode == 0 ? Strings.KeyCapture_None
                : KeyCodes.DisplayName((ushort)KeyCode);
            SetValue(DisplayTextKey, text);
        }
    }
}
