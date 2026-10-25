namespace MozaPlugin.Devices
{
    /// <summary>
    /// Decodes the AB9 / AB6 HID gear buttons (docs/protocol/devices/ab9-shifter.md
    /// § HID input report). The buttons name the logical gear, independent of layout.
    /// </summary>
    internal static class Ab9Gear
    {
        public const int Reverse = -1;
        public const int Neutral = 0;

        // 0-based button indices (SimHub's _Bnn numbering).
        private const int ReverseButton = 0;
        private const int FirstGearButton = 1;
        private const int MaxGear = 8;
        private const int SeqDownButton = 12; // push
        private const int SeqUpButton = 13;   // pull

        /// <summary>Engaged gear: <see cref="Reverse"/>, <see cref="Neutral"/> or 1..8.</summary>
        public static int FromButtons(bool[] buttons)
        {
            if (buttons[ReverseButton]) return Reverse;
            for (int g = 1; g <= MaxGear; g++)
                if (buttons[FirstGearButton + g - 1]) return g;
            return Neutral;
        }

        public static bool SeqUp(bool[] buttons) => buttons[SeqUpButton];
        public static bool SeqDown(bool[] buttons) => buttons[SeqDownButton];
    }
}
