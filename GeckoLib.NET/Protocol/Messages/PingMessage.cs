namespace GeckoLib.NET.Protocol.Messages
{
    /// <summary>
    /// APING keepalive. Unusually, the request carries no sequence byte; the response is
    /// the same verb with one trailing byte.
    /// </summary>
    public static class PingMessage
    {
        /// <summary>Build a ping request.</summary>
        public static byte[] BuildRequest()
        {
            return ProtocolBytes.Message(Verbs.APING);
        }

        /// <summary>Build a ping response (used when acting as a spa, e.g. in tests).</summary>
        public static byte[] BuildResponse(byte sequence)
        {
            return ProtocolBytes.Message(Verbs.APING, new[] { sequence });
        }

        /// <summary>Read the optional sequence byte from a ping response.</summary>
        public static byte? ParseSequence(byte[] payload)
        {
            byte[] remainder = ProtocolBytes.Remainder(payload);
            return remainder.Length > 0 ? remainder[0] : (byte?)null;
        }
    }
}
