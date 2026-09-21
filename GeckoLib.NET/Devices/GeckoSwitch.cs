using System;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Packs;
using GeckoLib.NET.Struct;

namespace GeckoLib.NET.Devices
{
    /// <summary>
    /// Something that is either on or off.
    ///
    /// The underlying field may be a boolean or an enum with "ON"/"OFF" members, so both
    /// are handled.
    /// </summary>
    public class GeckoSwitch : GeckoPowerDevice
    {
        private readonly GeckoStructAccessor _accessor;

        /// <param name="spa">The spa this belongs to.</param>
        /// <param name="name">Display name.</param>
        /// <param name="key">The field to read and write.</param>
        /// <param name="deviceClass">Optional category.</param>
        public GeckoSwitch(IGeckoSpa spa, string name, string key, string deviceClass = GeckoDeviceClass.SWITCH)
            : base(spa, name, key)
        {
            _accessor = spa.Struct[key];
            DeviceClass = deviceClass;
            IsAvailable = _accessor != null;
            Watch(_accessor);
        }

        /// <summary>Optional category.</summary>
        public string DeviceClass { get; }

        /// <summary>The field behind this switch, or null if the spa has not got it.</summary>
        protected GeckoStructAccessor Accessor
        {
            get { return _accessor; }
        }

        private bool IsBoolean
        {
            get { return _accessor != null && _accessor.Kind == EAccessorKind.Bool; }
        }

        /// <inheritdoc/>
        public override bool IsOn
        {
            get
            {
                if (_accessor == null) return false;
                if (IsBoolean) return (bool)_accessor.Value;
                return (string)_accessor.Value != "OFF";
            }
        }

        /// <summary>"ON" or "OFF".</summary>
        public virtual string State
        {
            get
            {
                if (_accessor == null) return "OFF";
                if (IsBoolean) return IsOn ? "ON" : "OFF";
                return (string)_accessor.Value;
            }
        }

        /// <summary>Turn it on. Does nothing if it is already on.</summary>
        public virtual async Task<bool> TurnOnAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_accessor == null || IsOn) return true;

            return IsBoolean
                ? await _accessor.SetValueAsync(true, false, cancellationToken).ConfigureAwait(false)
                : await _accessor.SetValueAsync("ON", false, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Turn it off. Does nothing if it is already off.</summary>
        public virtual async Task<bool> TurnOffAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_accessor == null || !IsOn) return true;

            return IsBoolean
                ? await _accessor.SetValueAsync(false, false, cancellationToken).ConfigureAwait(false)
                : await _accessor.SetValueAsync("OFF", false, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Standby, which stops the pumps for maintenance.
    ///
    /// Inverted compared to everything else: the field reads "OFF" when standby is ON,
    /// and turning standby off writes "NOT_SET" rather than "ON".
    /// </summary>
    public sealed class GeckoStandby : GeckoDevice
    {
        /// <summary>The field that carries standby, despite the name.</summary>
        public const string QUIET_STATE_KEY = "QuietState";

        private readonly GeckoStructAccessor _accessor;

        public GeckoStandby(IGeckoSpa spa)
            : base(spa, "Standby", QUIET_STATE_KEY)
        {
            _accessor = spa.Struct[QUIET_STATE_KEY];
            IsAvailable = _accessor != null;
            Watch(_accessor);
        }

        /// <summary>Is standby active?</summary>
        public bool IsOn
        {
            get { return _accessor != null && (string)_accessor.Value == "OFF"; }
        }

        /// <summary>Put the spa into standby.</summary>
        public async Task<bool> TurnOnAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_accessor == null) return false;
            return await _accessor.SetValueAsync("OFF", false, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Take the spa out of standby.</summary>
        public async Task<bool> TurnOffAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_accessor == null) return false;
            return await _accessor.SetValueAsync("NOT_SET", false, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The raw field value, which is more informative than on/off.</summary>
        public string RawState
        {
            get { return _accessor == null ? "Unknown" : (string)_accessor.Value; }
        }

        public override string Monitor
        {
            get { return Key + ": " + IsOn; }
        }

        public override string ToString()
        {
            return Name + ": " + IsOn;
        }
    }
}
