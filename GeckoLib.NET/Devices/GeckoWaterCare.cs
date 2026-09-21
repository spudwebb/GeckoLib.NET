using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Protocol.Messages;

namespace GeckoLib.NET.Devices
{
    /// <summary>
    /// The spa's water care programme.
    ///
    /// Unlike most devices this is not in the pack structure - it has its own protocol
    /// exchange - so the value here is whatever the last read returned, and stays null
    /// until one has happened.
    /// </summary>
    public sealed class GeckoWaterCare : GeckoDevice
    {
        public GeckoWaterCare(IGeckoSpa spa)
            : base(spa, "WaterCare", "WATERCARE")
        {
            // MrSteam and BainUltra are different products and have no water care.
            IsAvailable = IsSpaPack(spa);
        }

        /// <summary>The mode last read from the spa, or null if none has been.</summary>
        public EWatercareMode? Mode { get; private set; }

        /// <summary>The available modes, in wire order.</summary>
        public IList<string> Modes
        {
            get { return WatercareMessage.MODE_NAMES; }
        }

        /// <summary>The current mode's display name.</summary>
        public string State
        {
            get { return Mode.HasValue ? WatercareMessage.ToDisplayName(Mode.Value) : "Unknown"; }
        }

        /// <summary>Read the current mode from the spa.</summary>
        public async Task<EWatercareMode?> RefreshAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            EWatercareMode? mode = await Spa.GetWatercareModeAsync(cancellationToken).ConfigureAwait(false);
            if (mode.HasValue) SetMode(mode.Value);
            return mode;
        }

        /// <summary>Change the mode.</summary>
        public async Task<bool> SetStateAsync(
            EWatercareMode mode, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!await Spa.SetWatercareModeAsync(mode, cancellationToken).ConfigureAwait(false)) return false;

            SetMode(mode);
            return true;
        }

        /// <summary>Change the mode by display name, e.g. "Energy Saving".</summary>
        public async Task<bool> SetStateAsync(
            string modeName, CancellationToken cancellationToken = default(CancellationToken))
        {
            int index = Array.FindIndex(
                WatercareMessage.MODE_NAMES,
                n => string.Equals(n, modeName, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                throw new ArgumentException(
                    "'" + modeName + "' is not a water care mode", nameof(modeName));
            }

            return await SetStateAsync((EWatercareMode)index, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Record a mode read elsewhere, e.g. by the facade's update loop.</summary>
        internal void SetMode(EWatercareMode mode)
        {
            if (Mode.HasValue && Mode.Value == mode) return;

            Mode = mode;
            RaiseChanged();
        }

        /// <summary>
        /// Is this a spa pack, as opposed to a MrSteam or BainUltra unit? Those share the
        /// protocol but have no water care or reminders.
        /// </summary>
        internal static bool IsSpaPack(IGeckoSpa spa)
        {
            string platform = spa.PlatformKey;
            if (platform == null) return true;

            platform = platform.ToLowerInvariant();
            return platform != "mrsteam" && platform != "mas-ibc-32k";
        }

        public override string Monitor
        {
            get { return "WC: " + (Mode.HasValue ? ((int)Mode.Value).ToString() : "?"); }
        }

        public override string ToString()
        {
            if (!IsAvailable) return Name + ": Unavailable";
            return Mode.HasValue ? Name + ": " + State : Name + ": Waiting...";
        }
    }
}
