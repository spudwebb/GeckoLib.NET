using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Struct;

namespace GeckoLib.NET.Devices
{
    /// <summary>
    /// Everything a spa has, as objects.
    ///
    /// Every device a pack could have is created, and each works out for itself whether
    /// this particular spa has it - so the lists below contain only what is really
    /// fitted, while the individual properties are always non-null and can be asked.
    ///
    /// Ported from geckolib automation/async_facade.py, minus its background update task:
    /// water care and reminders are refreshed by calling <see cref="RefreshAsync"/>, so
    /// the owner decides when the spa gets talked to.
    /// </summary>
    public sealed class GeckoSpaFacade
    {
        private readonly IGeckoSpa _spa;

        public GeckoSpaFacade(IGeckoSpa spa)
        {
            if (spa == null) throw new ArgumentNullException(nameof(spa));
            _spa = spa;

            // What is wired to each output decides which pumps and lights exist.
            if (spa.ConfigStruct != null && spa.LogStruct != null)
            {
                var outputs = new List<string>(spa.ConfigStruct.OutputKeys);
                outputs.AddRange(spa.LogStruct.OutputKeys);
                spa.Struct.BuildConnections(outputs);
            }

            Pump1 = new GeckoPump(spa, "Pump 1", "P1");
            Pump2 = new GeckoPump(spa, "Pump 2", "P2");
            Pump3 = new GeckoPump(spa, "Pump 3", "P3");
            Pump4 = new GeckoPump(spa, "Pump 4", "P4");
            Pump5 = new GeckoPump(spa, "Pump 5", "P5");
            Blower = new GeckoBlower(spa);
            Waterfall = new GeckoWaterfall(spa);
            BubbleGenerator = new GeckoBubbleGenerator(spa);

            Light = new GeckoLightLi(spa);
            Light2 = new GeckoLightL120(spa);

            WaterHeater = new GeckoWaterHeater(spa);
            WaterCare = new GeckoWaterCare(spa);
            Reminders = new GeckoReminders(spa);

            HeatPump = new GeckoHeatPump(spa);
            InGrid = new GeckoInGrid(spa);
            LockMode = new GeckoLockMode(spa);
            EcoMode = new GeckoSwitch(spa, "Economy Mode", "EconActive");
            Standby = new GeckoStandby(spa);

            ErrorSensor = new GeckoErrorSensor(spa);
            Keypad = new GeckoKeypad(spa, this);

            ScanOutputs();

            foreach (GeckoDevice device in AllDevices)
            {
                device.Changed += (sender, e) => RaiseChanged(e.Device);
            }
        }

        /// <summary>Any device's state changed.</summary>
        public event EventHandler<DeviceChangedEventArgs> Changed;

        /// <summary>The spa's name.</summary>
        public string Name
        {
            get { return _spa.Name; }
        }

        /// <summary>The underlying spa.</summary>
        public IGeckoSpa Spa
        {
            get { return _spa; }
        }

        // Every device a pack might have. Ask IsAvailable before using one.

        public GeckoPump Pump1 { get; }
        public GeckoPump Pump2 { get; }
        public GeckoPump Pump3 { get; }
        public GeckoPump Pump4 { get; }
        public GeckoPump Pump5 { get; }
        public GeckoBlower Blower { get; }
        public GeckoWaterfall Waterfall { get; }
        public GeckoBubbleGenerator BubbleGenerator { get; }
        public GeckoLightLi Light { get; }
        public GeckoLightL120 Light2 { get; }
        public GeckoWaterHeater WaterHeater { get; }
        public GeckoWaterCare WaterCare { get; }
        public GeckoReminders Reminders { get; }
        public GeckoHeatPump HeatPump { get; }
        public GeckoInGrid InGrid { get; }
        public GeckoLockMode LockMode { get; }
        public GeckoSwitch EcoMode { get; }
        public GeckoStandby Standby { get; }
        public GeckoErrorSensor ErrorSensor { get; }
        public GeckoKeypad Keypad { get; }

        /// <summary>The pumps this spa has, including the waterfall and bubble generator.</summary>
        public IReadOnlyList<GeckoPump> Pumps { get; private set; }

        /// <summary>The blowers this spa has.</summary>
        public IReadOnlyList<GeckoBlower> Blowers { get; private set; }

        /// <summary>The lights this spa has.</summary>
        public IReadOnlyList<GeckoLight> Lights { get; private set; }

        /// <summary>The readings this spa reports.</summary>
        public IReadOnlyList<GeckoSensor> Sensors { get; private set; }

        /// <summary>The on/off readings this spa reports.</summary>
        public IReadOnlyList<GeckoBinarySensor> BinarySensors { get; private set; }

        /// <summary>The switches this spa has.</summary>
        public IReadOnlyList<GeckoSwitch> Switches { get; private set; }

        /// <summary>Everything that is actually fitted.</summary>
        public IReadOnlyList<GeckoDevice> AllDevices { get; private set; }

        /// <summary>The things a user would drive: pumps, blowers and lights.</summary>
        public IReadOnlyList<GeckoDevice> UserDevices { get; private set; }

        /// <summary>
        /// Is anything running? geckolib uses this to decide whether to poll the spa hard
        /// or back off.
        /// </summary>
        public bool IsInUse
        {
            get
            {
                foreach (GeckoPump pump in Pumps) { if (pump.IsOn) return true; }
                foreach (GeckoBlower blower in Blowers) { if (blower.IsOn) return true; }
                foreach (GeckoLight light in Lights) { if (light.IsOn) return true; }
                return false;
            }
        }

        /// <summary>Find a device by key, or null.</summary>
        public GeckoDevice GetDevice(string key)
        {
            foreach (GeckoDevice device in AllDevices)
            {
                if (string.Equals(device.Key, key, StringComparison.OrdinalIgnoreCase)) return device;
            }

            return null;
        }

        /// <summary>
        /// Refresh the things that are not in the status block, so do not arrive by push:
        /// the water care mode and the reminders.
        /// </summary>
        public async Task RefreshAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (WaterCare.IsAvailable) await WaterCare.RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (Reminders.IsAvailable) await Reminders.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        private void ScanOutputs()
        {
            Pumps = Available(new GeckoPump[] { Pump1, Pump2, Pump3, Pump4, Pump5, Waterfall, BubbleGenerator });
            Blowers = Available(new[] { Blower });
            Lights = Available(new GeckoLight[] { Light, Light2 });

            var sensors = new List<GeckoSensor>();
            AddSensor(sensors, "Smart Winter Mode:Risk", "SwmRisk");

            // A state sensor for each fitted device, reporting what the spa is doing
            // rather than what the user asked for.
            AddDeviceStateSensor(sensors, Pump1, "Pump 1 State", "P1");
            AddDeviceStateSensor(sensors, Pump2, "Pump 2 State", "P2");
            AddDeviceStateSensor(sensors, Pump3, "Pump 3 State", "P3");
            AddDeviceStateSensor(sensors, Pump4, "Pump 4 State", "P4");
            AddDeviceStateSensor(sensors, Pump5, "Pump 5 State", "P5");
            AddDeviceStateSensor(sensors, Blower, "Blower State", "BL");
            AddDeviceStateSensor(sensors, Waterfall, "Waterfall State", "Waterfall");
            Sensors = sensors;

            var binary = new List<GeckoBinarySensor>();
            AddBinarySensor(binary, "Circulating Pump", "CP", GeckoDeviceClass.PUMP);
            AddBinarySensor(binary, "Pump Run", "PumpRun", GeckoDeviceClass.PUMP);
            AddBinarySensor(binary, "Ozone", "O3", GeckoDeviceClass.OTHER);
            AddBinarySensor(binary, "Smart Winter Mode:Active", "SwmActive", GeckoDeviceClass.OTHER);
            AddBinarySensor(binary, "Filter Status:Clean", "Clean", GeckoDeviceClass.OTHER);
            AddBinarySensor(binary, "Filter Status:Purge", "Purge", GeckoDeviceClass.OTHER);
            AddBinarySensor(binary, "Heating", "Heating", GeckoDeviceClass.OTHER);
            BinarySensors = binary;

            Switches = Available(new[] { EcoMode });

            var all = new List<GeckoDevice>();
            all.AddRange(Pumps);
            all.AddRange(Blowers);
            all.AddRange(Lights);
            all.AddRange(Switches);
            all.AddRange(Sensors);
            all.AddRange(BinarySensors);
            all.Add(ErrorSensor);

            foreach (GeckoDevice device in new GeckoDevice[]
            {
                WaterHeater, WaterCare, Reminders, HeatPump, InGrid, LockMode, Standby, Keypad
            })
            {
                if (device.IsAvailable) all.Add(device);
            }

            AllDevices = all;

            var user = new List<GeckoDevice>();
            user.AddRange(Pumps);
            user.AddRange(Blowers);
            user.AddRange(Lights);
            UserDevices = user;
        }

        private void AddSensor(List<GeckoSensor> sensors, string name, string key)
        {
            GeckoStructAccessor accessor = _spa.Struct[key];
            if (accessor == null) return;
            sensors.Add(new GeckoSensor(_spa, name, accessor));
        }

        private void AddDeviceStateSensor(List<GeckoSensor> sensors, GeckoDevice device, string name, string key)
        {
            if (!device.IsAvailable) return;

            GeckoStructAccessor accessor = _spa.Struct[key];
            if (accessor == null) return;
            sensors.Add(new GeckoSensor(_spa, name, accessor));
        }

        private void AddBinarySensor(List<GeckoBinarySensor> sensors, string name, string key, string deviceClass)
        {
            GeckoStructAccessor accessor = _spa.Struct[key];
            if (accessor == null) return;
            sensors.Add(new GeckoBinarySensor(_spa, name, accessor, deviceClass));
        }

        private static IReadOnlyList<T> Available<T>(IEnumerable<T> devices) where T : GeckoDevice
        {
            return devices.Where(d => d.IsAvailable).ToList();
        }

        private void RaiseChanged(GeckoDevice device)
        {
            EventHandler<DeviceChangedEventArgs> handler = Changed;
            if (handler != null) handler(this, new DeviceChangedEventArgs(device));
        }
    }
}
