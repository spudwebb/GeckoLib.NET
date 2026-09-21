using System;
using System.Collections.Generic;

namespace GeckoLib.NET.Protocol
{
    /// <summary>
    /// The PACKT envelope that wraps every message except discovery:
    ///
    /// <code><![CDATA[
    /// <PACKT><SRCCN>src</SRCCN><DESCN>dst</DESCN><DATAS>payload</DATAS></PACKT>
    /// ]]></code>
    ///
    /// geckolib parses this with a greedy DOTALL regex over binary data
    /// (driver/protocol/packet.py:69-98), which misreads any payload that happens to
    /// contain the tag bytes. This scans for the delimiters instead.
    /// </summary>
    public static class GeckoPacket
    {
        private const string PACKET_OPEN = "<PACKT>";
        private const string PACKET_CLOSE = "</PACKT>";
        private const string SRCCN_OPEN = "<SRCCN>";
        private const string SRCCN_CLOSE = "</SRCCN>";
        private const string DESCN_OPEN = "<DESCN>";
        private const string DESCN_CLOSE = "</DESCN>";
        private const string DATAS_OPEN = "<DATAS>";
        private const string DATAS_CLOSE = "</DATAS>";

        /// <summary>
        /// Wrap a payload. Note the source is the client's identifier and the
        /// destination is the spa's - geckolib's parms tuple has them the other way
        /// round on the way out (packet.py:42-46).
        /// </summary>
        public static byte[] Wrap(string source, string destination, byte[] payload)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            var packet = new List<byte>(payload.Length + 96);
            packet.AddRange(ProtocolBytes.FromText(PACKET_OPEN));
            packet.AddRange(ProtocolBytes.FromText(SRCCN_OPEN));
            packet.AddRange(ProtocolBytes.FromText(source));
            packet.AddRange(ProtocolBytes.FromText(SRCCN_CLOSE));
            packet.AddRange(ProtocolBytes.FromText(DESCN_OPEN));
            packet.AddRange(ProtocolBytes.FromText(destination));
            packet.AddRange(ProtocolBytes.FromText(DESCN_CLOSE));
            packet.AddRange(ProtocolBytes.FromText(DATAS_OPEN));
            packet.AddRange(payload);
            packet.AddRange(ProtocolBytes.FromText(DATAS_CLOSE));
            packet.AddRange(ProtocolBytes.FromText(PACKET_CLOSE));
            return packet.ToArray();
        }

        /// <summary>Is this datagram a PACKT envelope?</summary>
        public static bool IsPacket(byte[] datagram)
        {
            return StartsWith(datagram, PACKET_OPEN, 0) &&
                   datagram.Length >= PACKET_OPEN.Length + PACKET_CLOSE.Length &&
                   StartsWith(datagram, PACKET_CLOSE, datagram.Length - PACKET_CLOSE.Length);
        }

        /// <summary>
        /// Unwrap a received envelope. Returns false for anything malformed.
        /// </summary>
        public static bool TryUnwrap(byte[] datagram, out string source, out string destination, out byte[] payload)
        {
            source = null;
            destination = null;
            payload = null;

            if (!IsPacket(datagram))
            {
                return false;
            }

            int cursor = PACKET_OPEN.Length;
            int end = datagram.Length - PACKET_CLOSE.Length;

            if (!TryReadField(datagram, ref cursor, end, SRCCN_OPEN, SRCCN_CLOSE, out source)) return false;
            if (!TryReadField(datagram, ref cursor, end, DESCN_OPEN, DESCN_CLOSE, out destination)) return false;

            // The payload is binary, so it is delimited by position rather than searched
            // for: it runs from the end of <DATAS> to the start of the closing tag, which
            // must be the last thing before </PACKT>.
            if (!StartsWith(datagram, DATAS_OPEN, cursor)) return false;
            cursor += DATAS_OPEN.Length;

            int payloadEnd = end - DATAS_CLOSE.Length;
            if (payloadEnd < cursor) return false;
            if (!StartsWith(datagram, DATAS_CLOSE, payloadEnd)) return false;

            payload = ProtocolBytes.Slice(datagram, cursor, payloadEnd - cursor);
            return true;
        }

        private static bool TryReadField(
            byte[] datagram, ref int cursor, int limit, string open, string close, out string value)
        {
            value = null;
            if (!StartsWith(datagram, open, cursor)) return false;
            cursor += open.Length;

            int start = cursor;
            int found = IndexOf(datagram, close, start, limit);
            if (found < 0) return false;

            value = ProtocolBytes.ToText(datagram, start, found - start);
            cursor = found + close.Length;
            return true;
        }

        private static bool StartsWith(byte[] buffer, string text, int offset)
        {
            if (buffer == null || offset < 0 || offset + text.Length > buffer.Length) return false;
            for (int i = 0; i < text.Length; i++)
            {
                if (buffer[offset + i] != (byte)text[i]) return false;
            }

            return true;
        }

        private static int IndexOf(byte[] buffer, string text, int start, int limit)
        {
            for (int i = start; i + text.Length <= limit; i++)
            {
                if (StartsWith(buffer, text, i)) return i;
            }

            return -1;
        }
    }
}
