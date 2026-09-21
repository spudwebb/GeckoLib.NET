namespace GeckoLib.NET.Protocol.Messages
{
    /// <summary>The radio channel the spa is using, and how well it is hearing us.</summary>
    public sealed class ChannelInfo
    {
        public ChannelInfo(int channel, int signalStrength)
        {
            Channel = channel;
            SignalStrength = signalStrength;
        }

        public int Channel { get; }

        /// <summary>Signal strength as a percentage. geckolib caps the reported value at 100.</summary>
        public int SignalStrength { get; }

        public override string ToString()
        {
            return string.Format("channel {0}, signal {1}%", Channel, SignalStrength);
        }
    }

    /// <summary>CURCH request / CHCUR response.</summary>
    public static class ChannelMessage
    {
        /// <summary>Build a CURCH request.</summary>
        public static byte[] BuildRequest(byte sequence)
        {
            return ProtocolBytes.Message(Verbs.CURCH, new[] { sequence });
        }

        /// <summary>Build a CHCUR response (used when acting as a spa, e.g. in tests).</summary>
        public static byte[] BuildResponse(byte channel, byte signalStrength)
        {
            return ProtocolBytes.Message(Verbs.CHCUR, new[] { channel, signalStrength });
        }

        /// <summary>Parse a CHCUR response.</summary>
        public static bool TryParseResponse(byte[] payload, out ChannelInfo info)
        {
            info = null;
            if (!ProtocolBytes.HasVerb(payload, Verbs.CHCUR)) return false;

            byte[] body = ProtocolBytes.Remainder(payload);
            if (body.Length < 2) return false;

            info = new ChannelInfo(body[0], body[1]);
            return true;
        }
    }
}
