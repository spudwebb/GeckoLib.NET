using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GeckoLib.NET.Devices
{
    /// <summary>
    /// One button on the spa's keypad.
    ///
    /// Pressing a button is a different mechanism from writing a field: it tells the spa
    /// somebody pressed it, and the spa decides what that means. For a pump that is
    /// usually "advance to the next speed".
    /// </summary>
    public sealed class GeckoButton : GeckoDevice
    {
        private readonly int _keyCode;

        /// <param name="spa">The spa this belongs to.</param>
        /// <param name="name">Display name, e.g. "Key Pump 1".</param>
        /// <param name="keyCode">The keypad code, from <see cref="GeckoKeys.Keypad"/>.</param>
        public GeckoButton(IGeckoSpa spa, string name, int keyCode)
            : base(spa, name, "KEY" + keyCode)
        {
            _keyCode = keyCode;
            IsAvailable = true;
        }

        /// <summary>The keypad code this button sends.</summary>
        public int KeyCode
        {
            get { return _keyCode; }
        }

        /// <summary>Press it.</summary>
        public Task<bool> PressAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return Spa.PressKeyAsync(_keyCode, cancellationToken);
        }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// The spa's keypad: the buttons this spa actually has, plus the backlight.
    ///
    /// Which buttons exist follows from which devices are fitted, so this is built after
    /// the rest of the facade.
    /// </summary>
    public sealed class GeckoKeypad : GeckoDevice
    {
        private readonly List<GeckoButton> _buttons = new List<GeckoButton>();

        public GeckoKeypad(IGeckoSpa spa, GeckoSpaFacade facade)
            : base(spa, "Keypad", "KEYPAD")
        {
            IsAvailable = true;
            Backlight = new GeckoKeypadBacklight(spa);

            AddIf(facade.Pump1, "Key Pump 1", GeckoKeys.Keypad.PUMP_1);
            AddIf(facade.Pump2, "Key Pump 2", GeckoKeys.Keypad.PUMP_2);
            AddIf(facade.Pump3, "Key Pump 3", GeckoKeys.Keypad.PUMP_3);
            AddIf(facade.Pump4, "Key Pump 4", GeckoKeys.Keypad.PUMP_4);
            AddIf(facade.Pump5, "Key Pump 5", GeckoKeys.Keypad.PUMP_5);
            AddIf(facade.Blower, "Key Blower", GeckoKeys.Keypad.BLOWER);
            AddIf(facade.Waterfall, "Key Waterfall", GeckoKeys.Keypad.WATERFALL);
            AddIf(facade.BubbleGenerator, "Key Bubble Generator", GeckoKeys.Keypad.AUX);
            AddIf(facade.Light, "Key Light", GeckoKeys.Keypad.LIGHT);
            AddIf(facade.Light2, "Key Light 2", GeckoKeys.Keypad.LIGHT_120);
            AddIf(facade.WaterHeater, "Key Up", GeckoKeys.Keypad.UP);
            AddIf(facade.WaterHeater, "Key Down", GeckoKeys.Keypad.DOWN);
        }

        /// <summary>The backlight colour setting.</summary>
        public GeckoKeypadBacklight Backlight { get; }

        /// <summary>The buttons this spa has.</summary>
        public IReadOnlyList<GeckoButton> Buttons
        {
            get { return _buttons; }
        }

        private void AddIf(GeckoDevice device, string name, int keyCode)
        {
            if (device == null || !device.IsAvailable) return;
            _buttons.Add(new GeckoButton(Spa, name, keyCode));
        }

        public override string ToString()
        {
            return Name + ": " + _buttons.Count + " buttons";
        }
    }
}
