using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Packs;

namespace GeckoLib.NET.Struct
{
    /// <summary>Raised when a field's decoded value changes.</summary>
    public sealed class AccessorChangedEventArgs : EventArgs
    {
        public AccessorChangedEventArgs(GeckoStructAccessor accessor, object oldValue, object newValue)
        {
            Accessor = accessor;
            OldValue = oldValue;
            NewValue = newValue;
        }

        public GeckoStructAccessor Accessor { get; }
        public object OldValue { get; }
        public object NewValue { get; }
    }

    /// <summary>
    /// One field of the spa's pack structure, bound to a status block.
    ///
    /// Reading decodes bytes out of the block; writing sends a pack command and only
    /// updates the block once the spa acks it. Ported from geckolib accessor.py.
    /// </summary>
    public sealed class GeckoStructAccessor
    {
        /// <summary>Key of the field holding the spa's temperature units.</summary>
        internal const string TEMP_UNITS_KEY = "TempUnits";

        private readonly GeckoStatusBlock _block;

        internal GeckoStructAccessor(GeckoStatusBlock block, AccessorDefinition definition)
        {
            _block = block;
            Definition = definition;
        }

        /// <summary>What this field is and where it lives.</summary>
        public AccessorDefinition Definition { get; }

        /// <summary>The field's decoded value changed.</summary>
        public event EventHandler<AccessorChangedEventArgs> Changed;

        public string Key { get { return Definition.Key; } }

        public string Tag { get { return Definition.Tag; } }

        public EAccessorKind Kind { get { return Definition.Kind; } }

        public int Position { get { return Definition.Position; } }

        public int Length { get { return Definition.Length; } }

        /// <summary>
        /// Can this field be written? The simulator overrides this to make everything
        /// writable; the real definitions mark most fields read-only.
        /// </summary>
        public bool IsWritable { get; internal set; }

        /// <summary>
        /// When true, writes patch the local block instead of going to the spa. Used for
        /// the inMix zones, which are written as one batch rather than field by field.
        /// </summary>
        public bool DirectUpdate { get; set; }

        /// <summary>The decoded value: bool, string, int or double depending on the kind.</summary>
        public object Value
        {
            get { return Decode(_block.Bytes); }
        }

        /// <summary>The value as it comes off the wire, before decoding.</summary>
        public int RawValue
        {
            get { return DecodeRaw(_block.Bytes); }
        }

        /// <summary>Read the raw integer out of a given block.</summary>
        internal int DecodeRaw(byte[] bytes)
        {
            int data = Definition.Length == 2
                ? (bytes[Definition.Position] << 8) | bytes[Definition.Position + 1]
                : bytes[Definition.Position];

            if (Definition.BitPosition.HasValue)
            {
                data = (data >> Definition.BitPosition.Value) & Definition.BitMask;
            }

            return data;
        }

        /// <summary>Decode the value out of a given block.</summary>
        internal object Decode(byte[] bytes)
        {
            int data = DecodeRaw(bytes);

            switch (Definition.Kind)
            {
                case EAccessorKind.Bool:
                    return data == 1;

                case EAccessorKind.Enum:
                    string[] items = Definition.Items;
                    return items != null && data >= 0 && data < items.Length ? items[data] : "Unknown";

                case EAccessorKind.Time:
                    // Hours live in the high byte, minutes in the low byte.
                    return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", data / 256, data % 256);

                case EAccessorKind.Temp:
                    return DecodeTemperature(data);

                default:
                    return data;
            }
        }

        /// <summary>
        /// Temperatures are stored as tenths of a degree Fahrenheit offset from freezing.
        /// The conversion depends on the spa's configured units, which is itself a field,
        /// so it is resolved through the block each time (geckolib accessor.py:388-392).
        /// </summary>
        private double DecodeTemperature(int raw)
        {
            return IsCelsius() ? raw / 18.0 : (raw + 320) / 10.0;
        }

        private bool IsCelsius()
        {
            GeckoStructAccessor units;
            if (!_block.Accessors.TryGetValue(TEMP_UNITS_KEY, out units))
            {
                // Before the accessors are built there is nothing to consult. geckolib
                // would throw here; Fahrenheit is the raw unit, so assume that.
                return false;
            }

            return Equals(units.Value, "C");
        }

        /// <summary>
        /// Write a new value. Unless <see cref="DirectUpdate"/> is set this sends a pack
        /// command and waits for the spa to ack before the local block changes.
        /// </summary>
        /// <param name="value">The value in decoded form, matching <see cref="Kind"/>.</param>
        /// <param name="alwaysSend">Send even if the encoded value is unchanged.</param>
        /// <param name="cancellationToken">Abandons the write if the spa does not ack.</param>
        public async Task<bool> SetValueAsync(
            object value,
            bool alwaysSend = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!IsWritable)
            {
                throw new InvalidOperationException("Accessor " + Tag + " is not writable");
            }

            int encoded = Encode(value);

            int existing = Definition.Length == 2
                ? (_block.Bytes[Definition.Position] << 8) | _block.Bytes[Definition.Position + 1]
                : _block.Bytes[Definition.Position];

            if (Definition.BitPosition.HasValue)
            {
                // Merge into the surrounding bits rather than overwriting them.
                int mask = Definition.BitMask << Definition.BitPosition.Value;
                encoded = (existing & ~mask) | ((encoded & Definition.BitMask) << Definition.BitPosition.Value);
            }

            if (encoded == existing && !alwaysSend)
            {
                return true;
            }

            if (DirectUpdate)
            {
                _block.ReplaceSegment(Definition.Position, PackValue(encoded));
                return true;
            }

            return await _block.WriteAsync(Definition.Position, Definition.Length, encoded, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>Turn a decoded value into the integer that goes on the wire.</summary>
        internal int Encode(object value)
        {
            switch (Definition.Kind)
            {
                case EAccessorKind.Enum:
                {
                    string wanted = Convert.ToString(value, CultureInfo.InvariantCulture);
                    string[] items = Definition.Items;
                    if (items != null)
                    {
                        for (int i = 0; i < items.Length; i++)
                        {
                            if (string.Equals(items[i], wanted, StringComparison.Ordinal)) return i;
                        }
                    }

                    throw new ArgumentException(
                        "'" + wanted + "' is not one of the values " + Tag + " accepts", nameof(value));
                }

                case EAccessorKind.Time:
                {
                    string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    string[] parts = text.Split(':');
                    if (parts.Length != 2)
                    {
                        throw new ArgumentException("Expected a time as hh:mm, got '" + text + "'", nameof(value));
                    }

                    int hours = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    int minutes = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    return (hours * 256) + (minutes % 256);
                }

                case EAccessorKind.Bool:
                {
                    bool flag = value is bool
                        ? (bool)value
                        : string.Equals(
                            Convert.ToString(value, CultureInfo.InvariantCulture),
                            "true",
                            StringComparison.OrdinalIgnoreCase);
                    return flag ? 1 : 0;
                }

                case EAccessorKind.Temp:
                {
                    double temperature = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    return (int)(IsCelsius() ? temperature * 18.0 : (temperature * 10.0) - 320);
                }

                default:
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Pack an encoded integer into its wire bytes, big-endian.</summary>
        internal byte[] PackValue(int value)
        {
            if (Definition.Length == 2)
            {
                return new[] { (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF) };
            }

            return new[] { (byte)(value & 0xFF) };
        }

        /// <summary>
        /// Called when part of the block changed. Fires <see cref="Changed"/> only if
        /// this field overlaps the change AND its decoded value actually moved.
        /// </summary>
        internal void OnBlockChanged(byte[] previous, byte[] current)
        {
            object oldValue = Decode(previous);
            object newValue = Decode(current);
            if (Equals(oldValue, newValue)) return;

            EventHandler<AccessorChangedEventArgs> handler = Changed;
            if (handler != null)
            {
                handler(this, new AccessorChangedEventArgs(this, oldValue, newValue));
            }
        }

        public override string ToString()
        {
            return string.Format("{0}: {1}", Key, Value);
        }
    }
}
