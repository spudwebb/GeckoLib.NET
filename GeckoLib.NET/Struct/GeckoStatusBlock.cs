using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Packs;

namespace GeckoLib.NET.Struct
{
    /// <summary>Sends a value to the spa. Implemented by the connection layer.</summary>
    public interface IStatusBlockWriter
    {
        /// <summary>
        /// Write <paramref name="value"/> into the spa's pack structure and wait for the
        /// ack. Returns false if the spa did not accept it.
        /// </summary>
        Task<bool> WriteAsync(int position, int length, int value, CancellationToken cancellationToken);
    }

    /// <summary>
    /// The spa's 1024-byte pack structure and the accessors that read it.
    ///
    /// The block is patched from two directions: full reads at connect time, and the
    /// two-byte pushes the spa sends whenever something changes. Either way, only the
    /// fields whose decoded value actually moved raise a change.
    ///
    /// geckolib notifies every accessor on every patch and rebuilds the whole block by
    /// concatenation (async_spastruct.py:38-49). This mutates in place and uses an
    /// offset index, because a 2-byte push should not walk 1000 accessors.
    /// </summary>
    public sealed class GeckoStatusBlock
    {
        /// <summary>The pack structure is always 1024 bytes.</summary>
        public const int BLOCK_LENGTH = 1024;

        private readonly byte[] _bytes = new byte[BLOCK_LENGTH];
        private readonly Dictionary<string, GeckoStructAccessor> _accessors =
            new Dictionary<string, GeckoStructAccessor>(StringComparer.Ordinal);

        // For each byte offset, the accessors that overlap it.
        private readonly List<GeckoStructAccessor>[] _index = new List<GeckoStructAccessor>[BLOCK_LENGTH];

        /// <summary>Raised for each field whose value changed, after the block is patched.</summary>
        public event EventHandler<AccessorChangedEventArgs> AccessorChanged;

        /// <summary>Sends writes to the spa. Set by the connection layer.</summary>
        public IStatusBlockWriter Writer { get; set; }

        /// <summary>The live block. Treat as read-only.</summary>
        internal byte[] Bytes
        {
            get { return _bytes; }
        }

        /// <summary>Every accessor, by key.</summary>
        public IReadOnlyDictionary<string, GeckoStructAccessor> Accessors
        {
            get { return _accessors; }
        }

        /// <summary>Look up an accessor, or null if this pack does not have that field.</summary>
        public GeckoStructAccessor this[string key]
        {
            get
            {
                GeckoStructAccessor accessor;
                return _accessors.TryGetValue(key, out accessor) ? accessor : null;
            }
        }

        /// <summary>A copy of the current block.</summary>
        public byte[] Snapshot()
        {
            var copy = new byte[BLOCK_LENGTH];
            Buffer.BlockCopy(_bytes, 0, copy, 0, BLOCK_LENGTH);
            return copy;
        }

        /// <summary>Zero the block and forget every accessor.</summary>
        public void Reset()
        {
            Array.Clear(_bytes, 0, _bytes.Length);
            _accessors.Clear();
            Array.Clear(_index, 0, _index.Length);
        }

        /// <summary>
        /// Build the accessor table from the pack definitions.
        ///
        /// Order matters: later definitions win on a key collision, which is how the
        /// inMix accessory overrides base fields (geckolib async_spastruct.py:60-75).
        /// </summary>
        public void BuildAccessors(params IList<AccessorDefinition>[] definitionSets)
        {
            _accessors.Clear();
            Array.Clear(_index, 0, _index.Length);

            foreach (IList<AccessorDefinition> definitions in definitionSets)
            {
                if (definitions == null) continue;

                foreach (AccessorDefinition definition in definitions)
                {
                    if (definition.Position + definition.Length > BLOCK_LENGTH)
                    {
                        // Nothing in the shipped definitions does this, but a corrupt
                        // data file should not produce an index-out-of-range at runtime.
                        continue;
                    }

                    var accessor = new GeckoStructAccessor(this, definition)
                    {
                        IsWritable = definition.IsWritable
                    };

                    _accessors[definition.Key] = accessor;
                }
            }

            foreach (GeckoStructAccessor accessor in _accessors.Values)
            {
                for (int offset = accessor.Position; offset < accessor.Position + accessor.Length; offset++)
                {
                    if (_index[offset] == null) _index[offset] = new List<GeckoStructAccessor>();
                    _index[offset].Add(accessor);
                }
            }
        }

        /// <summary>
        /// Patch part of the block and raise changes for the fields it touched.
        /// </summary>
        public void ReplaceSegment(int offset, byte[] segment)
        {
            if (segment == null) throw new ArgumentNullException(nameof(segment));
            if (offset < 0 || offset + segment.Length > BLOCK_LENGTH)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offset),
                    string.Format("Segment of {0} bytes at {1} does not fit the block", segment.Length, offset));
            }

            // Only the overlapping accessors can have changed, so gather them before the
            // write and compare against a copy of just that window.
            var affected = new HashSet<GeckoStructAccessor>();
            for (int i = offset; i < offset + segment.Length; i++)
            {
                List<GeckoStructAccessor> at = _index[i];
                if (at == null) continue;
                foreach (GeckoStructAccessor accessor in at) affected.Add(accessor);
            }

            byte[] previous = affected.Count == 0 ? null : Snapshot();
            Buffer.BlockCopy(segment, 0, _bytes, offset, segment.Length);

            if (previous == null) return;

            foreach (GeckoStructAccessor accessor in affected)
            {
                object oldValue = accessor.Decode(previous);
                object newValue = accessor.Decode(_bytes);
                if (Equals(oldValue, newValue)) continue;

                accessor.OnBlockChanged(previous, _bytes);

                EventHandler<AccessorChangedEventArgs> handler = AccessorChanged;
                if (handler != null)
                {
                    handler(this, new AccessorChangedEventArgs(accessor, oldValue, newValue));
                }
            }
        }

        /// <summary>The accessors that overlap a byte range. Used to name pushed changes.</summary>
        public IEnumerable<GeckoStructAccessor> AccessorsAt(int offset, int length)
        {
            var seen = new HashSet<GeckoStructAccessor>();
            for (int i = offset; i < offset + length && i < BLOCK_LENGTH; i++)
            {
                List<GeckoStructAccessor> at = _index[i];
                if (at == null) continue;
                foreach (GeckoStructAccessor accessor in at)
                {
                    if (seen.Add(accessor)) yield return accessor;
                }
            }

            // seen is the dedupe set; the yield above is the result.
        }

        /// <summary>
        /// What each physical output is wired to, e.g. "P1H", "BLO", "LI".
        ///
        /// Built from the config structure's output keys, dropping the ones that report
        /// nothing connected. Devices use this to decide whether they exist at all.
        /// </summary>
        public IReadOnlyList<string> Connections { get; private set; } = new string[0];

        /// <summary>
        /// Work out what is wired to each output. Call once the accessors are built.
        /// </summary>
        public void BuildConnections(IEnumerable<string> outputKeys)
        {
            var connections = new List<string>();
            foreach (string key in outputKeys)
            {
                GeckoStructAccessor accessor = this[key];
                if (accessor == null) continue;

                var value = accessor.Value as string;
                if (value == null || value == "NA" || value == "Not_Set") continue;

                connections.Add(value);
            }

            Connections = connections;
        }

        /// <summary>Make every field writable. Only useful when talking to the simulator.</summary>
        public void MakeEverythingWritable()
        {
            foreach (GeckoStructAccessor accessor in _accessors.Values)
            {
                accessor.IsWritable = true;
            }
        }

        internal async Task<bool> WriteAsync(int position, int length, int value, CancellationToken cancellationToken)
        {
            IStatusBlockWriter writer = Writer;
            if (writer == null)
            {
                throw new InvalidOperationException("No writer is attached; the spa is not connected");
            }

            return await writer.WriteAsync(position, length, value, cancellationToken).ConfigureAwait(false);
        }
    }
}
