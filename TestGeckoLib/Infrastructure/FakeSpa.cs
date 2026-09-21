using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Devices;
using GeckoLib.NET.Packs;
using GeckoLib.NET.Protocol.Messages;
using GeckoLib.NET.Struct;

namespace TestGeckoLib.Infrastructure
{
    /// <summary>
    /// A spa assembled from whatever fields a test needs.
    ///
    /// The conformance harness already checks the device layer against geckolib over
    /// real snapshots; this is for pinning down individual behaviours - what a two-speed
    /// pump does, what the heater reports when the pack has no cooling flag - where a
    /// hand-built structure says more clearly what is being tested.
    /// </summary>
    internal sealed class FakeSpa : IGeckoSpa
    {
        private readonly List<AccessorDefinition> _definitions = new List<AccessorDefinition>();
        private readonly List<string> _outputKeys = new List<string>();
        private readonly List<string> _errorKeys = new List<string>();
        private int _nextPosition = 10;

        public FakeSpa(string platformKey = "inxe")
        {
            PlatformKey = platformKey;
            Struct = new GeckoStatusBlock();
        }

        public string Name { get { return "Fake Spa"; } }

        public string UniqueId { get { return "FAKE"; } }

        public GeckoStatusBlock Struct { get; }

        public LogStructDefinition LogStruct { get; private set; }

        public ConfigStructDefinition ConfigStruct { get; private set; }

        public string PlatformKey { get; }

        /// <summary>Keypad presses this spa was asked to make.</summary>
        public List<int> PressedKeys { get; } = new List<int>();

        /// <summary>The water care mode this fake reports.</summary>
        public EWatercareMode? WatercareMode { get; set; }

        /// <summary>The reminders this fake reports.</summary>
        public IList<Reminder> RemindersToReturn { get; set; }

        /// <summary>Declare a field. Positions are allocated automatically.</summary>
        public FakeSpa With(
            string key,
            EAccessorKind kind,
            int? bitPosition = null,
            string[] items = null,
            int? size = null,
            int? maxItems = null,
            bool writable = true)
        {
            int length = kind == EAccessorKind.Word || kind == EAccessorKind.Time || kind == EAccessorKind.Temp
                ? 2
                : size ?? 1;

            _definitions.Add(new AccessorDefinition(
                key, "LogStructure/Test/" + key, kind, _nextPosition, bitPosition, items, size, maxItems, writable));

            _nextPosition += length;
            return this;
        }

        /// <summary>Declare an output field, which is what decides device availability.</summary>
        public FakeSpa WithOutput(string key, params string[] choices)
        {
            _outputKeys.Add(key);
            return With(key, EAccessorKind.Enum, items: choices);
        }

        /// <summary>Declare a field that reports a fault.</summary>
        public FakeSpa WithError(string key)
        {
            _errorKeys.Add(key);
            return With(key, EAccessorKind.Bool, bitPosition: 0);
        }

        /// <summary>Finish construction and build the accessor table.</summary>
        public FakeSpa Build()
        {
            ConfigStruct = new ConfigStructDefinition(1, _outputKeys, new List<AccessorDefinition>());
            LogStruct = new LogStructDefinition(
                1, 0, GeckoStatusBlock.BLOCK_LENGTH, new string[0], new string[0], new string[0],
                _errorKeys, _definitions);

            Struct.BuildAccessors(_definitions);
            Struct.MakeEverythingWritable();
            return this;
        }

        /// <summary>Set a field's value directly, without going near a spa.</summary>
        public FakeSpa Set(string key, object value)
        {
            GeckoStructAccessor accessor = Struct[key];
            accessor.DirectUpdate = true;
            accessor.SetValueAsync(value).GetAwaiter().GetResult();
            return this;
        }

        /// <summary>
        /// Write a field's raw wire value, skipping any conversion. Useful for
        /// temperatures, where the stored value is tenths of a degree Fahrenheit above
        /// freezing and saying so directly is clearer than converting in the test.
        /// </summary>
        public FakeSpa SetRaw(string key, int rawValue)
        {
            GeckoStructAccessor accessor = Struct[key];
            byte[] bytes = accessor.Length == 2
                ? new[] { (byte)((rawValue >> 8) & 0xFF), (byte)(rawValue & 0xFF) }
                : new[] { (byte)(rawValue & 0xFF) };

            Struct.ReplaceSegment(accessor.Position, bytes);
            return this;
        }

        /// <summary>Build the device layer over this spa.</summary>
        public GeckoSpaFacade CreateFacade()
        {
            // Writes go straight into the local block; there is no spa to ack them.
            foreach (GeckoStructAccessor accessor in Struct.Accessors.Values) accessor.DirectUpdate = true;
            return new GeckoSpaFacade(this);
        }

        public Task<bool> PressKeyAsync(int keyCode, CancellationToken cancellationToken)
        {
            PressedKeys.Add(keyCode);
            return Task.FromResult(true);
        }

        public Task<EWatercareMode?> GetWatercareModeAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(WatercareMode);
        }

        public Task<bool> SetWatercareModeAsync(EWatercareMode mode, CancellationToken cancellationToken)
        {
            WatercareMode = mode;
            return Task.FromResult(true);
        }

        public Task<IList<Reminder>> GetRemindersAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(RemindersToReturn);
        }

        /// <summary>Reminder sets this spa was asked to write.</summary>
        public List<IList<Reminder>> WrittenReminders { get; } = new List<IList<Reminder>>();

        /// <summary>Whether this fake accepts a reminder write.</summary>
        public bool AcceptReminderWrites { get; set; } = true;

        public Task<bool> SetRemindersAsync(IList<Reminder> reminders, CancellationToken cancellationToken)
        {
            WrittenReminders.Add(reminders);
            if (AcceptReminderWrites) RemindersToReturn = reminders;
            return Task.FromResult(AcceptReminderWrites);
        }
    }
}
