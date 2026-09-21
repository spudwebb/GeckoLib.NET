using System;
using System.Collections.Generic;

namespace GeckoLib.NET.Protocol
{
    /// <summary>
    /// Byte-level helpers for the in.touch2 wire format.
    ///
    /// The protocol is big-endian throughout, with one exception: reminder records are
    /// little-endian (see <see cref="ReadInt16LittleEndian"/>). Text is latin-1.
    /// </summary>
    internal static class ProtocolBytes
    {
        /// <summary>Every verb on the wire is exactly five ASCII bytes.</summary>
        public const int VERB_LENGTH = 5;

        public static byte[] FromText(string text)
        {
            return GeckoConstants.MessageEncoding.GetBytes(text);
        }

        public static string ToText(byte[] bytes, int offset, int count)
        {
            return GeckoConstants.MessageEncoding.GetString(bytes, offset, count);
        }

        /// <summary>Does <paramref name="buffer"/> begin with this verb?</summary>
        public static bool HasVerb(byte[] buffer, string verb)
        {
            if (buffer == null || buffer.Length < verb.Length) return false;
            for (int i = 0; i < verb.Length; i++)
            {
                if (buffer[i] != (byte)verb[i]) return false;
            }

            return true;
        }

        /// <summary>The five-character verb at the head of a payload, or null if too short.</summary>
        public static string ReadVerb(byte[] payload)
        {
            if (payload == null || payload.Length < VERB_LENGTH) return null;
            return ToText(payload, 0, VERB_LENGTH);
        }

        /// <summary>Everything after the verb.</summary>
        public static byte[] Remainder(byte[] payload)
        {
            if (payload == null || payload.Length <= VERB_LENGTH) return new byte[0];
            var remainder = new byte[payload.Length - VERB_LENGTH];
            Buffer.BlockCopy(payload, VERB_LENGTH, remainder, 0, remainder.Length);
            return remainder;
        }

        public static ushort ReadUInt16BigEndian(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        public static void WriteUInt16BigEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 1] = (byte)(value & 0xFF);
        }

        /// <summary>
        /// Reminder records carry a SIGNED little-endian day count, unlike everything
        /// else on the wire (geckolib reminders.py:78 packs them as
        /// <c><![CDATA[<BhB]]></c>).
        /// </summary>
        public static short ReadInt16LittleEndian(byte[] buffer, int offset)
        {
            return (short)(buffer[offset] | (buffer[offset + 1] << 8));
        }

        public static void WriteInt16LittleEndian(byte[] buffer, int offset, short value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        /// <summary>Concatenate a verb and its payload bytes.</summary>
        public static byte[] Message(string verb, params byte[][] parts)
        {
            var result = new List<byte>(FromText(verb));
            if (parts != null)
            {
                foreach (byte[] part in parts)
                {
                    if (part != null) result.AddRange(part);
                }
            }

            return result.ToArray();
        }

        /// <summary>Copy a slice out of a buffer.</summary>
        public static byte[] Slice(byte[] buffer, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(buffer, offset, result, 0, count);
            return result;
        }

        /// <summary>Render bytes the way Python's repr does, for log and test messages.</summary>
        public static string Describe(byte[] buffer)
        {
            if (buffer == null) return "<null>";
            var text = new System.Text.StringBuilder(buffer.Length + 8);
            foreach (byte value in buffer)
            {
                if (value >= 32 && value < 127)
                {
                    text.Append((char)value);
                }
                else
                {
                    text.Append("\\x").Append(value.ToString("x2"));
                }
            }

            return text.ToString();
        }
    }
}
