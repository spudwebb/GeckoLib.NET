using System;

namespace GeckoLib.NET.Protocol.Messages
{
    /// <summary>One segment of the status block, as returned by a STATV response.</summary>
    public sealed class StatusBlockSegment
    {
        public StatusBlockSegment(int sequence, int next, byte[] data)
        {
            Sequence = sequence;
            Next = next;
            Data = data;
        }

        /// <summary>This segment's index in the current transfer.</summary>
        public int Sequence { get; }

        /// <summary>Index of the following segment; zero means this was the last one.</summary>
        public int Next { get; }

        /// <summary>The segment bytes.</summary>
        public byte[] Data { get; }

        /// <summary>Is this the final segment of the transfer?</summary>
        public bool IsLast
        {
            get { return Next == 0; }
        }
    }

    /// <summary>
    /// STATU range request / STATV segment response. A full read is offset 0, length 1024.
    /// </summary>
    public static class StatusBlockMessage
    {
        /// <summary>The whole status block is 1024 bytes.</summary>
        public const int STATUS_BLOCK_LENGTH = 1024;

        /// <summary>Build a STATU request for a byte range.</summary>
        public static byte[] BuildRequest(byte sequence, int start, int length)
        {
            var body = new byte[5];
            body[0] = sequence;
            ProtocolBytes.WriteUInt16BigEndian(body, 1, start);
            ProtocolBytes.WriteUInt16BigEndian(body, 3, length);
            return ProtocolBytes.Message(Verbs.STATU, body);
        }

        /// <summary>Build a STATU request for the entire block.</summary>
        public static byte[] BuildFullRequest(byte sequence)
        {
            return BuildRequest(sequence, 0, STATUS_BLOCK_LENGTH);
        }

        /// <summary>Build a STATV response (used when acting as a spa, e.g. in tests).</summary>
        public static byte[] BuildResponse(byte index, byte next, byte[] block)
        {
            if (block == null) throw new ArgumentNullException(nameof(block));
            var header = new byte[] { index, next, (byte)block.Length };
            return ProtocolBytes.Message(Verbs.STATV, header, block);
        }

        /// <summary>Parse a STATV segment response.</summary>
        public static bool TryParseResponse(byte[] payload, out StatusBlockSegment segment)
        {
            segment = null;
            if (!ProtocolBytes.HasVerb(payload, Verbs.STATV)) return false;

            byte[] body = ProtocolBytes.Remainder(payload);
            if (body.Length < 3) return false;

            int sequence = body[0];
            int next = body[1];
            int length = body[2];
            if (body.Length < 3 + length) return false;

            segment = new StatusBlockSegment(sequence, next, ProtocolBytes.Slice(body, 3, length));
            return true;
        }
    }
}
