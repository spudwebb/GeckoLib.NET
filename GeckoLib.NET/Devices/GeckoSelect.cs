using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Struct;

namespace GeckoLib.NET.Devices
{
    /// <summary>
    /// A setting with a fixed set of values, backed by an enum field.
    ///
    /// Subclasses add a display mapping where the wire values are not presentable.
    /// </summary>
    public class GeckoSelect : GeckoDevice
    {
        private readonly GeckoStructAccessor _accessor;

        /// <param name="spa">The spa this belongs to.</param>
        /// <param name="name">Display name.</param>
        /// <param name="key">The enum field behind it.</param>
        public GeckoSelect(IGeckoSpa spa, string name, string key)
            : base(spa, name, key)
        {
            _accessor = spa.Struct[key];
            IsAvailable = _accessor != null;
            Watch(_accessor);
        }

        /// <summary>The field behind this setting, or null.</summary>
        protected GeckoStructAccessor Accessor
        {
            get { return _accessor; }
        }

        /// <summary>The current value, mapped for display.</summary>
        public virtual string State
        {
            get
            {
                if (_accessor == null) return "Unknown";
                return ToDisplay(Convert.ToString(_accessor.Value, CultureInfo.InvariantCulture));
            }
        }

        /// <summary>The values this setting accepts, mapped for display.</summary>
        public virtual IList<string> States
        {
            get
            {
                IList<string> mapped = MappedValues;
                if (mapped.Count > 0) return mapped;

                if (_accessor == null || _accessor.Definition.Items == null) return new string[0];
                return _accessor.Definition.Items;
            }
        }

        /// <summary>Change the setting. Accepts either a display or a wire value.</summary>
        public virtual async Task<bool> SetStateAsync(
            string newState, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_accessor == null) return false;
            return await _accessor.SetValueAsync(ToWire(newState), false, cancellationToken).ConfigureAwait(false);
        }

        public override string Monitor
        {
            get { return Key + ": " + State; }
        }

        public override string ToString()
        {
            return Name + ": " + State;
        }
    }

    /// <summary>How much of the spa's control panel is locked out.</summary>
    public sealed class GeckoLockMode : GeckoSelect
    {
        public GeckoLockMode(IGeckoSpa spa)
            : base(spa, "Lock Mode", "LockMode")
        {
            SetMapping(new Dictionary<string, string>
            {
                { "UNLOCK", "Unlocked" },
                { "PARTIAL", "Partial Lock" },
                { "FULL", "Full Lock" }
            });
        }
    }

    /// <summary>
    /// The heat pump mode, if a Modbus heat pump is fitted.
    ///
    /// Shares its field with <see cref="GeckoInGrid"/>; which one is real depends on
    /// what the pack reports as detected.
    /// </summary>
    public sealed class GeckoHeatPump : GeckoSelect
    {
        public GeckoHeatPump(IGeckoSpa spa)
            : base(spa, "Heat Pump", "CoolZoneMode")
        {
            GeckoStructAccessor detected = spa.Struct["ModbusHeatPumpDetected"];
            IsAvailable = detected != null && detected.Value is bool && (bool)detected.Value;

            SetMapping(new Dictionary<string, string>
            {
                { "CHILL", "Cool" },
                { "INTERNAL_HEAT", "Electric" },
                { "HEAT_SAVER", "Eco Heat" },
                { "HEAT_W_BOOST", "Smart Heat" },
                { "AUTO_SAVER", "Eco Auto" },
                { "AUTO_W_BOOST", "Smart Auto" }
            });
        }
    }

    /// <summary>The inGrid heating mode, if an inGrid is fitted.</summary>
    public sealed class GeckoInGrid : GeckoSelect
    {
        public GeckoInGrid(IGeckoSpa spa)
            : base(spa, "In Grid", "CoolZoneMode")
        {
            GeckoStructAccessor detected = spa.Struct["InGridDetected"];
            IsAvailable = detected != null && detected.Value is bool && (bool)detected.Value;

            SetMapping(new Dictionary<string, string>
            {
                { "INTERNAL_HEAT", "Electric" },
                { "HEAT_SAVER", "Eco Heat" },
                { "BOTH_HEAT", "Both" },
                { "HEAT_W_BOOST", "Smart Heat" }
            });
        }
    }

    /// <summary>The colour of the keypad backlight.</summary>
    public sealed class GeckoKeypadBacklight : GeckoSelect
    {
        public GeckoKeypadBacklight(IGeckoSpa spa)
            : base(spa, "Keypad Backlight", "KeypadBacklightColor")
        {
        }
    }
}
