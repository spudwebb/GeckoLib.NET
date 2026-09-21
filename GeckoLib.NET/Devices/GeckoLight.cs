using System;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Struct;

namespace GeckoLib.NET.Devices
{
    /// <summary>
    /// A spa light.
    ///
    /// The two lights a pack can have differ in how they are turned on: the main light
    /// takes "HI", the 120V one takes "ON".
    /// </summary>
    public abstract class GeckoLight : GeckoPowerDevice
    {
        private readonly GeckoStructAccessor _state;
        private readonly string _onValue;

        /// <param name="spa">The spa this belongs to.</param>
        /// <param name="name">Display name.</param>
        /// <param name="key">Connection key, e.g. "LI".</param>
        /// <param name="demandKey">The field carrying what the user asked for, e.g. "UdLi".</param>
        /// <param name="onValue">The value that means on for this light.</param>
        protected GeckoLight(IGeckoSpa spa, string name, string key, string demandKey, string onValue)
            : base(spa, name, key)
        {
            DeviceClass = GeckoDeviceClass.LIGHT;
            _onValue = onValue;

            foreach (string connection in spa.Struct.Connections)
            {
                if (!string.Equals(connection, key, StringComparison.Ordinal)) continue;

                _state = spa.Struct[demandKey];
                IsAvailable = _state != null;
                Watch(_state);
                break;
            }
        }

        /// <summary>Optional category.</summary>
        public string DeviceClass { get; }

        /// <inheritdoc/>
        public override bool IsOn
        {
            get { return State != "OFF"; }
        }

        /// <summary>The current setting, e.g. OFF, LO, HI.</summary>
        public string State
        {
            get { return _state == null ? "OFF" : (string)_state.Value; }
        }

        /// <summary>Turn the light on. Does nothing if it is already on.</summary>
        public async Task<bool> TurnOnAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_state == null || IsOn) return _state != null;
            return await _state.SetValueAsync(_onValue, false, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Turn the light off. Does nothing if it is already off.</summary>
        public async Task<bool> TurnOffAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_state == null || !IsOn) return _state != null;
            return await _state.SetValueAsync("OFF", false, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The main spa light.</summary>
    public sealed class GeckoLightLi : GeckoLight
    {
        public GeckoLightLi(IGeckoSpa spa)
            : base(spa, "Light", "LI", "UdLi", "HI")
        {
        }
    }

    /// <summary>The second, 120V light.</summary>
    public sealed class GeckoLightL120 : GeckoLight
    {
        public GeckoLightL120(IGeckoSpa spa)
            : base(spa, "Light 2", "L120", "UdL120", "ON")
        {
        }
    }
}
