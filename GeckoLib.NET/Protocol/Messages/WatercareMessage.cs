namespace GeckoLib.NET.Protocol.Messages
{
    /// <summary>The spa's water care programmes, in wire order.</summary>
    public enum EWatercareMode
    {
        AwayFromHome = 0,
        Standard = 1,
        EnergySaving = 2,
        SuperEnergySaving = 3,
        Weekender = 4
    }

    /// <summary>
    /// GETWC/WCGET to read the water care mode, SETWC/WCSET to change it.
    /// The mode is not held in the status block, so it has its own verbs.
    /// </summary>
    public static class WatercareMessage
    {
        /// <summary>Number of modes; received values are taken modulo this.</summary>
        public const int MODE_COUNT = 5;

        /// <summary>Display names, indexed by <see cref="EWatercareMode"/>.</summary>
        public static readonly string[] MODE_NAMES =
        {
            "Away From Home",
            "Standard",
            "Energy Saving",
            "Super Energy Saving",
            "Weekender"
        };

        /// <summary>Build a GETWC request.</summary>
        public static byte[] BuildGetRequest(byte sequence)
        {
            return ProtocolBytes.Message(Verbs.GETWC, new[] { sequence });
        }

        /// <summary>Build a WCGET response (used when acting as a spa, e.g. in tests).</summary>
        public static byte[] BuildGetResponse(EWatercareMode mode)
        {
            return ProtocolBytes.Message(Verbs.WCGET, new[] { (byte)mode });
        }

        /// <summary>Build a SETWC command.</summary>
        public static byte[] BuildSetRequest(byte sequence, EWatercareMode mode)
        {
            return ProtocolBytes.Message(Verbs.SETWC, new[] { sequence, (byte)mode });
        }

        /// <summary>Build a WCSET response (used when acting as a spa, e.g. in tests).</summary>
        public static byte[] BuildSetResponse(EWatercareMode mode)
        {
            return ProtocolBytes.Message(Verbs.WCSET, new[] { (byte)mode });
        }

        /// <summary>
        /// Parse a WCGET or WCSET response. geckolib reduces the received value modulo
        /// the mode count, so out-of-range values wrap rather than throwing.
        /// </summary>
        public static bool TryParseMode(byte[] payload, out EWatercareMode mode)
        {
            mode = EWatercareMode.Standard;
            if (!ProtocolBytes.HasVerb(payload, Verbs.WCGET) &&
                !ProtocolBytes.HasVerb(payload, Verbs.WCSET))
            {
                return false;
            }

            byte[] body = ProtocolBytes.Remainder(payload);
            if (body.Length < 1) return false;

            mode = (EWatercareMode)(body[0] % MODE_COUNT);
            return true;
        }

        /// <summary>Display name for a mode.</summary>
        public static string ToDisplayName(EWatercareMode mode)
        {
            int index = (int)mode;
            return index >= 0 && index < MODE_NAMES.Length ? MODE_NAMES[index] : "Unknown";
        }
    }
}
