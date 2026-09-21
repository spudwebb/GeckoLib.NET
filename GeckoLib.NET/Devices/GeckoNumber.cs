using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Struct;

namespace GeckoLib.NET.Devices
{
    /// <summary>
    /// A numeric setting backed by a byte or word field.
    ///
    /// The pack definitions do not carry a range, so the bounds here are defaults a
    /// consumer can override.
    /// </summary>
    public sealed class GeckoNumber : GeckoDevice
    {
        private readonly GeckoStructAccessor _accessor;
        private readonly GeckoStructAccessor _unitAccessor;
        private readonly string _fixedUnit;

        /// <param name="spa">The spa this belongs to.</param>
        /// <param name="name">Display name.</param>
        /// <param name="key">The field behind it.</param>
        /// <param name="unitAccessor">A field holding the unit, e.g. TempUnits.</param>
        /// <param name="unit">A fixed unit string, if there is no unit field.</param>
        public GeckoNumber(
            IGeckoSpa spa,
            string name,
            string key,
            GeckoStructAccessor unitAccessor = null,
            string unit = null)
            : base(spa, name, key)
        {
            _accessor = spa.Struct[key];
            _unitAccessor = unitAccessor;
            _fixedUnit = unit;

            IsAvailable = _accessor != null;
            Watch(_accessor);
            Watch(unitAccessor);
        }

        /// <summary>Lowest value a consumer should offer.</summary>
        public double MinValue { get; set; } = 0.0;

        /// <summary>Highest value a consumer should offer.</summary>
        public double MaxValue { get; set; } = 100.0;

        /// <summary>Step between values.</summary>
        public double Step { get; set; } = 1.0;

        /// <summary>The current value.</summary>
        public double Value
        {
            get
            {
                if (_accessor == null) return 0.0;
                return Convert.ToDouble(_accessor.Value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>The unit, if this setting has one.</summary>
        public string UnitOfMeasurement
        {
            get
            {
                if (_unitAccessor != null)
                {
                    return Convert.ToString(_unitAccessor.Value, CultureInfo.InvariantCulture);
                }

                return _fixedUnit;
            }
        }

        /// <summary>Change the value. It is rounded to a whole number on the wire.</summary>
        public async Task<bool> SetValueAsync(
            double newValue, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_accessor == null) return false;
            return await _accessor.SetValueAsync((int)newValue, false, cancellationToken).ConfigureAwait(false);
        }

        public override string Monitor
        {
            get { return Key + ": " + Value.ToString(CultureInfo.InvariantCulture); }
        }

        public override string ToString()
        {
            return Name + ": " + Value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
