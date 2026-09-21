using System;

namespace GeckoLib.NET.Packs
{
    /// <summary>
    /// The value types a pack structure field can have.
    ///
    /// <see cref="Temp"/> is stored as a word but carries a unit conversion, so it is a
    /// kind of its own even though the spa reports it as a Word.
    /// </summary>
    public enum EAccessorKind
    {
        Byte,
        Word,
        Time,
        Bool,
        Enum,
        Temp
    }

    /// <summary>
    /// One field of a spa pack structure, as declared in the pack definitions.
    ///
    /// This is pure data - where the field lives in the status block and how to decode
    /// it. <see cref="Struct.GeckoStructAccessor"/> binds it to an actual block.
    /// </summary>
    public sealed class AccessorDefinition
    {
        public AccessorDefinition(
            string key,
            string path,
            EAccessorKind kind,
            int position,
            int? bitPosition,
            string[] items,
            int? size,
            int? maxItems,
            bool isWritable)
        {
            Key = key;
            Path = path;
            Kind = kind;
            Position = position;
            BitPosition = bitPosition;
            Items = items;
            Size = size;
            MaxItems = maxItems;
            IsWritable = isWritable;

            // Length and format, per geckolib accessor.py:76-85. A declared size wins,
            // except that Word and Time are always two bytes.
            Length = 1;
            if (size.HasValue) Length = size.Value;
            if (kind == EAccessorKind.Word || kind == EAccessorKind.Time || kind == EAccessorKind.Temp)
            {
                Length = 2;
            }

            // Bit mask, per accessor.py:63-64 and :87-89. A bit position alone means a
            // single bit; a declared item count widens it to hold that many values.
            if (bitPosition.HasValue) BitMask = 1;
            if (maxItems.HasValue)
            {
                BitMask = (1 << BitsToHold(maxItems.Value)) - 1;
            }
        }

        /// <summary>
        /// How many bits it takes to hold <paramref name="itemCount"/> distinct values,
        /// i.e. ceil(log2(max(n, 2))).
        ///
        /// Done with integer arithmetic on purpose. geckolib uses math.log2, which is
        /// exact for powers of two, but .NET Framework has no Math.Log2 and the
        /// Math.Log(x, 2) fallback is a division of two logarithms - close enough to a
        /// power of two to round the wrong way, which would silently widen the mask and
        /// corrupt every bit-packed field.
        /// </summary>
        internal static int BitsToHold(int itemCount)
        {
            int wanted = Math.Max(itemCount, 2);
            int bits = 0;
            while ((1 << bits) < wanted) bits++;
            return bits;
        }

        /// <summary>The name this field is looked up by, e.g. "RhWaterTemp".</summary>
        public string Key { get; }

        /// <summary>Full structure path, e.g. "LogStructure/RealTimeTemp/RhWaterTemp".</summary>
        public string Path { get; }

        /// <summary>Last segment of <see cref="Path"/>.</summary>
        public string Tag
        {
            get
            {
                int slash = Path.LastIndexOf('/');
                return slash < 0 ? Path : Path.Substring(slash + 1);
            }
        }

        public EAccessorKind Kind { get; }

        /// <summary>Byte offset into the 1024-byte status block.</summary>
        public int Position { get; }

        /// <summary>How many bytes this field occupies: 1 or 2.</summary>
        public int Length { get; }

        /// <summary>Bit offset within the value, when the field is bit-packed.</summary>
        public int? BitPosition { get; }

        /// <summary>Mask applied after shifting by <see cref="BitPosition"/>.</summary>
        public int BitMask { get; }

        /// <summary>Enum member names, indexed by raw value. Null unless <see cref="Kind"/> is Enum.</summary>
        public string[] Items { get; }

        /// <summary>Declared size in bytes, when the definition gives one.</summary>
        public int? Size { get; }

        /// <summary>Declared number of enum values, used to derive <see cref="BitMask"/>.</summary>
        public int? MaxItems { get; }

        /// <summary>Can this field be written? Read-only fields have no rw flag in the definitions.</summary>
        public bool IsWritable { get; }

        public override string ToString()
        {
            return string.Format("{0} ({1} @ {2})", Key, Kind, Position);
        }
    }
}
