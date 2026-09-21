using System;
using System.Collections.Generic;

namespace GeckoLib.NET.Protocol.Messages
{
    /// <summary>One changed word of the status block, as pushed by the spa.</summary>
    public sealed class StatusChange
    {
        public StatusChange(int position, byte[] data)
        {
            Position = position;
            Data = data;
        }

        /// <summary>Offset into the status block.</summary>
        public int Position { get; }

        /// <summary>The new bytes. Always two of them in practice.</summary>
        public byte[] Data { get; }

        public override string ToString()
        {
            return string.Format("@{0}={1}", Position, ProtocolBytes.Describe(Data));
        }
    }

    /// <summary>
    /// STATP push and its STATQ ack.
    ///
    /// Once subscribed, the spa pushes changes unprompted. The client must ack each push
    /// immediately, using the COMMAND sequence counter rather than the protocol one
    /// (geckolib statusblock.py:150-165); the subscription lapses otherwise.
    /// </summary>
    public static class PartialStatusMessage
    {
        /// <summary>Each change is a 2-byte position followed by 2 bytes of data.</summary>
        private const int CHANGE_STRIDE = 4;

        /// <summary>Build a STATQ ack. The sequence comes from the command counter.</summary>
        public static byte[] BuildAck(byte commandSequence)
        {
            return ProtocolBytes.Message(Verbs.STATQ, new[] { commandSequence });
        }

        /// <summary>Build a STATP push (used when acting as a spa, e.g. in tests).</summary>
        public static byte[] BuildPush(IList<StatusChange> changes)
        {
            if (changes == null) throw new ArgumentNullException(nameof(changes));

            var body = new List<byte> { (byte)changes.Count };
            foreach (StatusChange change in changes)
            {
                var position = new byte[2];
                ProtocolBytes.WriteUInt16BigEndian(position, 0, change.Position);
                body.AddRange(position);
                body.AddRange(change.Data);
            }

            byte[] message = ProtocolBytes.Message(Verbs.STATP, body.ToArray());
            if (message.Length % 2 != 0)
            {
                throw new ArgumentException("Change data must be even length", nameof(changes));
            }

            return message;
        }

        /// <summary>Parse a STATP push.</summary>
        public static bool TryParsePush(byte[] payload, out IList<StatusChange> changes)
        {
            changes = null;
            if (!ProtocolBytes.HasVerb(payload, Verbs.STATP)) return false;

            byte[] body = ProtocolBytes.Remainder(payload);
            if (body.Length < 1) return false;

            int count = body[0];
            var parsed = new List<StatusChange>(count);
            for (int i = 0; i < count; i++)
            {
                int offset = 1 + (i * CHANGE_STRIDE);
                if (offset + CHANGE_STRIDE > body.Length) return false;

                parsed.Add(new StatusChange(
                    ProtocolBytes.ReadUInt16BigEndian(body, offset),
                    ProtocolBytes.Slice(body, offset + 2, 2)));
            }

            changes = parsed;
            return true;
        }
    }
}
