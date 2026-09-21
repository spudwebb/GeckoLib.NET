using System;
using System.Collections.Generic;
using System.Globalization;
using GeckoLib.NET.Struct;

namespace GeckoLib.NET.Devices
{
    /// <summary>Broad category of what a device is, for consumers that care.</summary>
    public static class GeckoDeviceClass
    {
        public const string PUMP = "PUMP";
        public const string BLOWER = "BLOWER";
        public const string LIGHT = "LIGHT";
        public const string SWITCH = "SWITCH";
        public const string OTHER = "OTHER";
    }

    /// <summary>A read-only value from the pack structure.</summary>
    public class GeckoSensor : GeckoDevice
    {
        private readonly GeckoStructAccessor _accessor;
        private readonly GeckoStructAccessor _unitAccessor;

        /// <param name="spa">The spa this belongs to.</param>
        /// <param name="name">Display name.</param>
        /// <param name="accessor">The field to read.</param>
        /// <param name="unitAccessor">A field holding the unit, e.g. TempUnits.</param>
        /// <param name="deviceClass">Optional category.</param>
        public GeckoSensor(
            IGeckoSpa spa,
            string name,
            GeckoStructAccessor accessor,
            GeckoStructAccessor unitAccessor = null,
            string deviceClass = null)
            : base(spa, name, accessor != null ? accessor.Key : name.ToUpperInvariant())
        {
            _accessor = accessor;
            _unitAccessor = unitAccessor;
            DeviceClass = deviceClass;
            IsAvailable = accessor != null;

            Watch(accessor);
            Watch(unitAccessor);
        }

        /// <summary>The field this reads, or null.</summary>
        public GeckoStructAccessor Accessor
        {
            get { return _accessor; }
        }

        /// <summary>The current value.</summary>
        public virtual object State
        {
            get { return _accessor == null ? null : _accessor.Value; }
        }

        /// <summary>The unit, if this sensor has one.</summary>
        public string UnitOfMeasurement
        {
            get
            {
                return _unitAccessor == null
                    ? null
                    : Convert.ToString(_unitAccessor.Value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Optional category.</summary>
        public string DeviceClass { get; }

        public override string Monitor
        {
            get { return (_accessor != null ? _accessor.Tag : Key) + ": " + State; }
        }

        public override string ToString()
        {
            return Name + ": " + State;
        }
    }

    /// <summary>A sensor whose value is on or off.</summary>
    public sealed class GeckoBinarySensor : GeckoSensor
    {
        public GeckoBinarySensor(
            IGeckoSpa spa, string name, GeckoStructAccessor accessor, string deviceClass = null)
            : base(spa, name, accessor, null, deviceClass)
        {
        }

        /// <summary>Is it on?</summary>
        public bool IsOn
        {
            get
            {
                object state = State;
                if (state is bool) return (bool)state;

                var text = state as string;
                if (string.IsNullOrEmpty(text)) return false;
                return text != "OFF";
            }
        }

        public override string ToString()
        {
            return Name + ": " + IsOn;
        }
    }

    /// <summary>
    /// Every fault the pack can report, rolled into one value.
    ///
    /// The pack declares a list of error keys; this watches all of them and reports the
    /// ones that are set, so a consumer needs one entity rather than twenty.
    /// </summary>
    public sealed class GeckoErrorSensor : GeckoDevice
    {
        private readonly List<GeckoStructAccessor> _errors = new List<GeckoStructAccessor>();

        public GeckoErrorSensor(IGeckoSpa spa)
            : base(spa, "Error Sensor", "ERROR SENSOR")
        {
            IsAvailable = true;

            if (spa.LogStruct == null) return;

            foreach (string key in spa.LogStruct.ErrorKeys)
            {
                GeckoStructAccessor accessor = Struct[key];
                if (accessor == null) continue;

                _errors.Add(accessor);
                Watch(accessor);
            }
        }

        /// <summary>The faults currently set, comma separated, or "None".</summary>
        public string State
        {
            get
            {
                var active = new List<string>();
                foreach (GeckoStructAccessor accessor in _errors)
                {
                    // Only the boolean error flags count; the pack also lists some
                    // non-boolean diagnostics among its error keys.
                    object value = accessor.Value;
                    if (value is bool && (bool)value) active.Add(accessor.Tag);
                }

                return active.Count == 0 ? "None" : string.Join(", ", active.ToArray());
            }
        }

        /// <summary>Is anything wrong?</summary>
        public bool HasErrors
        {
            get { return State != "None"; }
        }

        public override string ToString()
        {
            return Name + ": " + State;
        }
    }
}
