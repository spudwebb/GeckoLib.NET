using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Struct;

namespace GeckoLib.NET.Devices
{
    /// <summary>What the heater is doing right now.</summary>
    public static class GeckoHeaterOperation
    {
        public const string HEATING = "Heating";
        public const string COOLING = "Cooling";
        public const string IDLE = "Idle";
    }

    /// <summary>
    /// The spa's water heater: current and target temperature, and what it is doing.
    ///
    /// Ported from geckolib automation/heater.py.
    /// </summary>
    public sealed class GeckoWaterHeater : GeckoPowerDevice
    {
        /// <summary>Fallback limits, used when the pack does not declare its own.</summary>
        public const double MIN_TEMP_C = 15;
        public const double MAX_TEMP_C = 40;
        public const double MIN_TEMP_F = 59;
        public const double MAX_TEMP_F = 104;

        public const string TEMP_CELSIUS = "°C";
        public const string TEMP_FAHRENHEIT = "°F";

        private readonly GeckoStructAccessor _units;
        private readonly GeckoStructAccessor _target;
        private readonly GeckoStructAccessor _current;
        private readonly GeckoStructAccessor _realTarget;
        private readonly GeckoBinarySensor _heating;
        private readonly GeckoBinarySensor _cooling;
        private readonly GeckoStructAccessor _minTemp;
        private readonly GeckoStructAccessor _maxTemp;
        private readonly GeckoStructAccessor _tempNotValid;

        public GeckoWaterHeater(IGeckoSpa spa)
            : base(spa, "Heater", "HEAT")
        {
            _units = spa.Struct[GeckoKeys.TEMP_UNITS];
            _target = spa.Struct["SetpointG"];
            _current = spa.Struct["DisplayedTempG"];
            _realTarget = spa.Struct["RealSetPointG"];
            // These are not always booleans: on some packs "Heating" is an enum whose
            // value is the word "Heating". A binary sensor reads both shapes the way
            // geckolib does - anything that is not "OFF" counts as on.
            _heating = MakeSensor(spa, "Heating", "Heating");
            _cooling = MakeSensor(spa, "Cooling", "CoolingDown");
            _minTemp = spa.Struct["MinSetpointG"];
            _maxTemp = spa.Struct["MaxSetpointG"];
            _tempNotValid = spa.Struct["TempNotValid"];

            IsAvailable = _target != null || _current != null || _realTarget != null;

            foreach (GeckoStructAccessor accessor in new[]
            {
                _units, _target, _current, _realTarget, _minTemp, _maxTemp, _tempNotValid
            })
            {
                Watch(accessor);
            }

            foreach (GeckoBinarySensor sensor in new[] { _heating, _cooling })
            {
                if (sensor != null) sensor.Changed += (sender, e) => RaiseChanged();
            }

            if (_tempNotValid != null)
            {
                // A pack that reports its temperature is not valid has nothing useful to
                // say, so the heater goes unavailable rather than reporting nonsense.
                _tempNotValid.Changed += (sender, e) => UpdateAvailability();
                UpdateAvailability();
            }
        }

        private void UpdateAvailability()
        {
            if (_tempNotValid == null) return;
            IsAvailable = !(_tempNotValid.Value is bool && (bool)_tempNotValid.Value);
        }

        /// <summary>Is the spa reporting temperatures in Celsius?</summary>
        public bool IsCelsius
        {
            get { return _units != null && (string)_units.Value == "C"; }
        }

        /// <summary>"°C" or "°F".</summary>
        public string TemperatureUnit
        {
            get { return IsCelsius ? TEMP_CELSIUS : TEMP_FAHRENHEIT; }
        }

        /// <summary>The water temperature as the spa displays it.</summary>
        public double CurrentTemperature
        {
            get { return ToDouble(_current); }
        }

        /// <summary>The temperature the user asked for.</summary>
        public double TargetTemperature
        {
            get { return ToDouble(_target); }
        }

        /// <summary>
        /// The temperature the spa is actually working towards, which differs from the
        /// target while economy mode is in force.
        /// </summary>
        public double RealTargetTemperature
        {
            get { return _realTarget != null ? ToDouble(_realTarget) : TargetTemperature; }
        }

        /// <summary>Lowest temperature the spa accepts.</summary>
        public double MinTemp
        {
            get
            {
                if (_minTemp != null) return ToDouble(_minTemp);
                return IsCelsius ? MIN_TEMP_C : MIN_TEMP_F;
            }
        }

        /// <summary>Highest temperature the spa accepts.</summary>
        public double MaxTemp
        {
            get
            {
                if (_maxTemp != null) return ToDouble(_maxTemp);
                return IsCelsius ? MAX_TEMP_C : MAX_TEMP_F;
            }
        }

        /// <inheritdoc/>
        public override bool IsOn
        {
            get { return CurrentOperation != GeckoHeaterOperation.IDLE; }
        }

        /// <summary>
        /// Heating, cooling or idle.
        ///
        /// The pack's own flags are believed when present. Failing that, the temperatures
        /// are compared - which is what geckolib falls back to.
        /// </summary>
        public string CurrentOperation
        {
            get
            {
                // With both flags present they are the whole story.
                if (_heating != null && _cooling != null)
                {
                    if (_heating.IsOn) return GeckoHeaterOperation.HEATING;
                    if (_cooling.IsOn) return GeckoHeaterOperation.COOLING;
                    return GeckoHeaterOperation.IDLE;
                }

                if (_heating != null && _heating.IsOn) return GeckoHeaterOperation.HEATING;
                if (_cooling != null && _cooling.IsOn) return GeckoHeaterOperation.COOLING;

                if (CurrentTemperature < RealTargetTemperature) return GeckoHeaterOperation.HEATING;
                if (CurrentTemperature > RealTargetTemperature) return GeckoHeaterOperation.COOLING;
                return GeckoHeaterOperation.IDLE;
            }
        }

        /// <summary>Set the target temperature, in whatever units the spa is using.</summary>
        public async Task<bool> SetTargetTemperatureAsync(
            double temperature, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_target == null) return false;
            return await _target.SetValueAsync(temperature, false, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Switch the spa between Celsius and Fahrenheit.</summary>
        public async Task<bool> SetTemperatureUnitAsync(
            string unit, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_units == null) return false;

            bool fahrenheit = unit == TEMP_FAHRENHEIT ||
                              string.Equals(unit, "F", StringComparison.OrdinalIgnoreCase);

            return await _units.SetValueAsync(fahrenheit ? "F" : "C", false, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Format a temperature the way the spa would display it.</summary>
        public string FormatTemperature(double temperature)
        {
            return temperature.ToString("0.0", CultureInfo.InvariantCulture) + TemperatureUnit;
        }

        private static double ToDouble(GeckoStructAccessor accessor)
        {
            if (accessor == null) return 0.0;
            return Convert.ToDouble(accessor.Value, CultureInfo.InvariantCulture);
        }

        private static GeckoBinarySensor MakeSensor(IGeckoSpa spa, string name, string key)
        {
            GeckoStructAccessor accessor = spa.Struct[key];
            return accessor == null ? null : new GeckoBinarySensor(spa, name, accessor);
        }

        /// <summary>Is the spa actively heating? Null if the pack does not report it.</summary>
        public bool? IsHeating
        {
            get { return _heating == null ? (bool?)null : _heating.IsOn; }
        }

        /// <summary>Is the spa cooling down? Null if the pack does not report it.</summary>
        public bool? IsCoolingDown
        {
            get { return _cooling == null ? (bool?)null : _cooling.IsOn; }
        }

        public override string Monitor
        {
            get
            {
                return "HTR: " + FormatTemperature(CurrentTemperature) +
                       " SET: " + FormatTemperature(RealTargetTemperature);
            }
        }

        public override string ToString()
        {
            if (!IsAvailable) return Name + ": Not present";

            return string.Format(
                "{0}: Temperature {1}, SetPoint {2}, Real SetPoint {3}, Operation {4}",
                Name,
                FormatTemperature(CurrentTemperature),
                FormatTemperature(TargetTemperature),
                FormatTemperature(RealTargetTemperature),
                CurrentOperation);
        }
    }
}
