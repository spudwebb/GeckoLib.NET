using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Protocol.Messages;
using GeckoLib.NET.Struct;

namespace GeckoLib.NET.Devices
{
    /// <summary>
    /// What the device layer needs from a spa.
    ///
    /// <see cref="GeckoSpaConnection"/> implements this. Keeping it an interface means
    /// devices can also be built over a status block alone, which is how they are tested
    /// against captured snapshots without a spa or a network.
    /// </summary>
    public interface IGeckoSpa
    {
        /// <summary>The spa's name.</summary>
        string Name { get; }

        /// <summary>Something stable and unique to this spa, used to build device ids.</summary>
        string UniqueId { get; }

        /// <summary>The pack structure.</summary>
        GeckoStatusBlock Struct { get; }

        /// <summary>The log structure definition in use, or null offline.</summary>
        Packs.LogStructDefinition LogStruct { get; }

        /// <summary>The config structure definition in use, or null offline.</summary>
        Packs.ConfigStructDefinition ConfigStruct { get; }

        /// <summary>The platform key, lowercased, e.g. "inxe".</summary>
        string PlatformKey { get; }

        /// <summary>Press a keypad button.</summary>
        Task<bool> PressKeyAsync(int keyCode, CancellationToken cancellationToken);

        /// <summary>Read the water care mode.</summary>
        Task<EWatercareMode?> GetWatercareModeAsync(CancellationToken cancellationToken);

        /// <summary>Change the water care mode.</summary>
        Task<bool> SetWatercareModeAsync(EWatercareMode mode, CancellationToken cancellationToken);

        /// <summary>Read the maintenance reminders.</summary>
        Task<IList<Reminder>> GetRemindersAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Write the maintenance reminders. The protocol has no way to set one, so the
        /// whole set always goes together.
        /// </summary>
        Task<bool> SetRemindersAsync(IList<Reminder> reminders, CancellationToken cancellationToken);
    }

    /// <summary>A device's observable state changed.</summary>
    public sealed class DeviceChangedEventArgs : EventArgs
    {
        public DeviceChangedEventArgs(GeckoDevice device)
        {
            Device = device;
        }

        public GeckoDevice Device { get; }
    }

    /// <summary>
    /// Base of everything in the device layer.
    ///
    /// A device is a friendly view over one or more pack structure fields: it knows what
    /// it is called, whether this spa actually has it, and how to read and change it.
    /// Ported from geckolib automation/base.py.
    /// </summary>
    public abstract class GeckoDevice
    {
        private readonly Dictionary<string, string> _mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _reverse = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <param name="spa">The spa this device belongs to.</param>
        /// <param name="name">Display name, e.g. "Pump 1".</param>
        /// <param name="key">Pack structure key or device key, e.g. "P1".</param>
        protected GeckoDevice(IGeckoSpa spa, string name, string key)
        {
            if (spa == null) throw new ArgumentNullException(nameof(spa));

            Spa = spa;
            Name = name;
            Key = key;
        }

        /// <summary>The spa this device belongs to.</summary>
        protected IGeckoSpa Spa { get; }

        /// <summary>Shorthand for the pack structure.</summary>
        protected GeckoStatusBlock Struct
        {
            get { return Spa.Struct; }
        }

        /// <summary>This device's observable state changed.</summary>
        public event EventHandler<DeviceChangedEventArgs> Changed;

        /// <summary>Display name.</summary>
        public string Name { get; }

        /// <summary>Pack structure key, or a device key like "P1".</summary>
        public string Key { get; }

        /// <summary>Name of the spa this belongs to.</summary>
        public string ParentName
        {
            get { return Spa.Name; }
        }

        /// <summary>Stable identifier, unique across a spa's devices.</summary>
        public string UniqueId
        {
            get { return Spa.UniqueId + "-" + Key; }
        }

        /// <summary>
        /// Does this spa actually have this device? Every device a pack could have is
        /// created, and each decides for itself whether it is fitted.
        /// </summary>
        public bool IsAvailable { get; protected set; }

        /// <summary>
        /// Optional wire value to display value mapping, for devices whose raw values are
        /// not presentable, e.g. "HEAT_W_BOOST" as "Smart Heat".
        /// </summary>
        protected void SetMapping(IDictionary<string, string> mapping)
        {
            _mapping.Clear();
            _reverse.Clear();

            foreach (KeyValuePair<string, string> pair in mapping)
            {
                _mapping[pair.Key] = pair.Value;
                _reverse[pair.Value] = pair.Key;
            }
        }

        /// <summary>Map a wire value to its display value, or pass it through.</summary>
        protected string ToDisplay(string wireValue)
        {
            string display;
            return wireValue != null && _mapping.TryGetValue(wireValue, out display) ? display : wireValue;
        }

        /// <summary>Map a display value back to its wire value, or pass it through.</summary>
        protected string ToWire(string displayValue)
        {
            string wire;
            return displayValue != null && _reverse.TryGetValue(displayValue, out wire) ? wire : displayValue;
        }

        /// <summary>The display values this device offers, in mapping order.</summary>
        protected IList<string> MappedValues
        {
            get { return new List<string>(_mapping.Values); }
        }

        /// <summary>
        /// Watch an accessor and raise <see cref="Changed"/> whenever it moves. Null is
        /// accepted so callers do not have to guard optional fields.
        /// </summary>
        protected void Watch(GeckoStructAccessor accessor)
        {
            if (accessor == null) return;
            accessor.Changed += (sender, e) => RaiseChanged();
        }

        /// <summary>Raise <see cref="Changed"/>.</summary>
        protected void RaiseChanged()
        {
            EventHandler<DeviceChangedEventArgs> handler = Changed;
            if (handler != null) handler(this, new DeviceChangedEventArgs(this));
        }

        /// <summary>A short "key: value" form, for monitoring output.</summary>
        public virtual string Monitor
        {
            get { return ToString(); }
        }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// Base for devices that draw power, so a consumer can total up what the spa is
    /// using and tell whether it is in use at all.
    /// </summary>
    public abstract class GeckoPowerDevice : GeckoDevice
    {
        protected GeckoPowerDevice(IGeckoSpa spa, string name, string key)
            : base(spa, name, key)
        {
        }

        /// <summary>Is this device running?</summary>
        public abstract bool IsOn { get; }

        /// <summary>Rated power draw in watts. Nothing sets this; it is for consumers.</summary>
        public double Power { get; set; }

        /// <summary>Rated power if the device is running, otherwise zero.</summary>
        public double CurrentPower
        {
            get { return IsOn ? Power : 0.0; }
        }
    }
}
