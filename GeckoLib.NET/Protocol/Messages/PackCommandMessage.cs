using System;

namespace GeckoLib.NET.Protocol.Messages
{
    /// <summary>
    /// SPACK commands and their PACKS ack. This is the only way to change anything on
    /// the spa: either write a value into the pack structure, or press a keypad button.
    ///
    /// SPACK uses the COMMAND sequence counter, not the protocol one.
    /// </summary>
    public static class PackCommandMessage
    {
        /// <summary>Command byte for a key press.</summary>
        public const byte PACK_COMMAND_KEY_PRESS = 57;

        /// <summary>Command byte for a structure write.</summary>
        public const byte PACK_COMMAND_SET_VALUE = 70;

        /// <summary>
        /// Build a set-value command. <paramref name="data"/> is the packed new value:
        /// one byte for a byte accessor, two big-endian bytes for a word.
        /// </summary>
        public static byte[] BuildSetValue(
            byte sequence,
            int packType,
            int configVersion,
            int logVersion,
            int position,
            byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            var body = new byte[8];
            body[0] = sequence;
            body[1] = (byte)packType;
            body[2] = (byte)(5 + data.Length);
            body[3] = PACK_COMMAND_SET_VALUE;
            body[4] = (byte)configVersion;
            body[5] = (byte)logVersion;
            ProtocolBytes.WriteUInt16BigEndian(body, 6, position);
            return ProtocolBytes.Message(Verbs.SPACK, body, data);
        }

        /// <summary>Build a key-press command.</summary>
        public static byte[] BuildKeyPress(byte sequence, int packType, int keyCode)
        {
            var body = new byte[5];
            body[0] = sequence;
            body[1] = (byte)packType;
            body[2] = 2;
            body[3] = PACK_COMMAND_KEY_PRESS;
            body[4] = (byte)keyCode;
            return ProtocolBytes.Message(Verbs.SPACK, body);
        }

        /// <summary>Build a PACKS ack (used when acting as a spa, e.g. in tests).</summary>
        public static byte[] BuildAck()
        {
            return ProtocolBytes.Message(Verbs.PACKS);
        }

        /// <summary>Is this the ack for a pack command?</summary>
        public static bool IsAck(byte[] payload)
        {
            return ProtocolBytes.HasVerb(payload, Verbs.PACKS);
        }
    }
}
