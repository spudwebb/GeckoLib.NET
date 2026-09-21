using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Packs;
using GeckoLib.NET.Struct;

namespace GeckoLib.NET.Devices
{
    /// <summary>What a pump can do, worked out from how it is wired.</summary>
    public enum EPumpType
    {
        /// <summary>Not fitted.</summary>
        None = 0,

        /// <summary>On or off.</summary>
        SingleSpeed = 1,

        /// <summary>Off, low or high.</summary>
        TwoSpeed = 2,

        /// <summary>Any speed from 0 to 100 percent.</summary>
        VariableSpeed = 3
    }

    /// <summary>
    /// A pump, and the base for blowers, waterfalls and bubble generators.
    ///
    /// Which pumps a spa has is not declared anywhere directly: it is inferred from what
    /// the pack reports is wired to its outputs. "P1H" means pump 1 has a high speed,
    /// "P1L" a low one, so both means two-speed. A pump flagged as VSP is driven by a
    /// percentage instead, through a different field.
    /// </summary>
    public class GeckoPump : GeckoPowerDevice
    {
        private readonly GeckoStructAccessor _state;

        /// <param name="spa">The spa this belongs to.</param>
        /// <param name="name">Display name, e.g. "Pump 1".</param>
        /// <param name="key">Device key, e.g. "P1".</param>
        public GeckoPump(IGeckoSpa spa, string name, string key)
            : base(spa, name, key)
        {
            DeviceClass = GeckoDeviceClass.PUMP;
            PumpType = EPumpType.None;

            IReadOnlyList<string> connections = spa.Struct.Connections;
            string demandKey = "Ud" + key;

            if (Contains(connections, key) || Contains(connections, key.ToUpperInvariant()) ||
                Contains(connections, key + "H") || Contains(connections, key + "O"))
            {
                PumpType = EPumpType.SingleSpeed;
            }

            if (Contains(connections, key + "L"))
            {
                PumpType = PumpType == EPumpType.None ? EPumpType.SingleSpeed : EPumpType.TwoSpeed;
            }

            // A pump can be reconfigured as variable speed, which moves it to its own
            // demand field.
            if (key == "P1" && IsTrue(spa.Struct["Pump1AsVSP"]))
            {
                PumpType = EPumpType.VariableSpeed;
                demandKey = "UdVSP1";
            }

            if (key == "P3" && IsTrue(spa.Struct["Pump3AsVSP"]))
            {
                PumpType = EPumpType.VariableSpeed;
                demandKey = "UdVSP3";
            }

            if (PumpType != EPumpType.None)
            {
                _state = spa.Struct[demandKey];
                if (_state != null)
                {
                    IsAvailable = true;
                    Watch(_state);
                }
            }
        }

        /// <summary>How this pump is wired.</summary>
        public EPumpType PumpType { get; protected set; }

        /// <summary>Optional category.</summary>
        public string DeviceClass { get; protected set; }

        /// <summary>Is this a variable speed pump?</summary>
        public bool IsVariableSpeed
        {
            get { return PumpType == EPumpType.VariableSpeed; }
        }

        /// <summary>The field carrying what the user has asked this pump to do.</summary>
        protected GeckoStructAccessor StateAccessor
        {
            get { return _state; }
        }

        /// <inheritdoc/>
        public override bool IsOn
        {
            get
            {
                if (_state == null) return false;

                switch (_state.Kind)
                {
                    case EAccessorKind.Bool: return (bool)_state.Value;
                    case EAccessorKind.Byte: return Convert.ToInt32(_state.Value, CultureInfo.InvariantCulture) > 0;
                    default: return (string)_state.Value != "OFF";
                }
            }
        }

        /// <summary>The modes this pump accepts, e.g. OFF, LO, HI. Empty for variable speed.</summary>
        public IList<string> Modes
        {
            get
            {
                if (_state == null || _state.Definition.Items == null) return new string[0];
                return _state.Definition.Items;
            }
        }

        /// <summary>The current mode, or "NA" if the pump is not fitted.</summary>
        public string Mode
        {
            get
            {
                if (_state == null) return "NA";
                return Convert.ToString(_state.Value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Speed as a percentage. Only meaningful for a variable speed pump.</summary>
        public int Percentage
        {
            get
            {
                if (_state == null) return 0;
                return Convert.ToInt32(_state.Value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Current state, with a variable speed pump at zero reported as "OFF".</summary>
        public string State
        {
            get
            {
                if (IsVariableSpeed && Percentage == 0) return "OFF";
                return Mode;
            }
        }

        /// <summary>
        /// Start the pump. A variable speed pump takes a percentage, everything else a
        /// mode; both fall back to a sensible default.
        /// </summary>
        /// <param name="percentage">Speed for a variable speed pump. Defaults to 100.</param>
        /// <param name="presetMode">Mode for any other pump. Defaults to "HI", or "ON" if HI is not offered.</param>
        /// <param name="cancellationToken">Abandons the write.</param>
        public async Task<bool> TurnOnAsync(
            int? percentage = null,
            string presetMode = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_state == null) return false;

            if (IsVariableSpeed)
            {
                return await _state.SetValueAsync(percentage ?? 100, false, cancellationToken).ConfigureAwait(false);
            }

            string mode = presetMode ?? "HI";
            if (_state.Definition.Items != null && Array.IndexOf(_state.Definition.Items, mode) < 0)
            {
                mode = "ON";
            }

            return await _state.SetValueAsync(mode, false, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Stop the pump.</summary>
        public async Task<bool> TurnOffAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_state == null) return false;

            return IsVariableSpeed
                ? await _state.SetValueAsync(0, false, cancellationToken).ConfigureAwait(false)
                : await _state.SetValueAsync("OFF", false, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Set the mode directly, e.g. "LO".</summary>
        public async Task<bool> SetModeAsync(
            string mode, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_state == null) return false;
            return await _state.SetValueAsync(mode, false, cancellationToken).ConfigureAwait(false);
        }

        private static bool Contains(IReadOnlyList<string> connections, string value)
        {
            foreach (string connection in connections)
            {
                if (string.Equals(connection, value, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private static bool IsTrue(GeckoStructAccessor accessor)
        {
            return accessor != null && accessor.Value is bool && (bool)accessor.Value;
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

    /// <summary>The air blower. Wired and driven like a pump.</summary>
    public sealed class GeckoBlower : GeckoPump
    {
        public GeckoBlower(IGeckoSpa spa)
            : base(spa, "Blower", "BL")
        {
            DeviceClass = GeckoDeviceClass.BLOWER;
        }
    }

    /// <summary>The waterfall. Wired and driven like a pump.</summary>
    public sealed class GeckoWaterfall : GeckoPump
    {
        public GeckoWaterfall(IGeckoSpa spa)
            : base(spa, "Waterfall", "Waterfall")
        {
        }
    }

    /// <summary>
    /// A bubble generator on the auxiliary output. Only fitted if the pack says the aux
    /// output is configured as one.
    /// </summary>
    public sealed class GeckoBubbleGenerator : GeckoPump
    {
        public GeckoBubbleGenerator(IGeckoSpa spa)
            : base(spa, "Bubble Generator", "Aux")
        {
            if (!IsAvailable) return;

            GeckoStructAccessor asBubbleGen = spa.Struct["AuxAsBubbleGen"];
            if (asBubbleGen != null)
            {
                IsAvailable = asBubbleGen.Value is bool && (bool)asBubbleGen.Value;
            }
        }
    }
}
