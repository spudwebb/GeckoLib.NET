namespace GeckoLib.NET.Protocol.Messages
{
    /// <summary>Firmware versions of the EN (network) and CO (spa) modules.</summary>
    public sealed class SpaVersions
    {
        public SpaVersions(int enBuild, int enMajor, int enMinor, int coBuild, int coMajor, int coMinor)
        {
            EnBuild = enBuild;
            EnMajor = enMajor;
            EnMinor = enMinor;
            CoBuild = coBuild;
            CoMajor = coMajor;
            CoMinor = coMinor;
        }

        public int EnBuild { get; }
        public int EnMajor { get; }
        public int EnMinor { get; }
        public int CoBuild { get; }
        public int CoMajor { get; }
        public int CoMinor { get; }

        /// <summary>Formatted the way geckolib reports it, e.g. "70 v14.0".</summary>
        public string EnVersion
        {
            get { return string.Format("{0} v{1}.{2}", EnBuild, EnMajor, EnMinor); }
        }

        /// <summary>Formatted the way geckolib reports it, e.g. "69 v11.0".</summary>
        public string CoVersion
        {
            get { return string.Format("{0} v{1}.{2}", CoBuild, CoMajor, CoMinor); }
        }

        public override string ToString()
        {
            return "EN " + EnVersion + ", CO " + CoVersion;
        }
    }

    /// <summary>AVERS request / SVERS response. Response format is ">HBBHBB".</summary>
    public static class VersionMessage
    {
        /// <summary>Build an AVERS request.</summary>
        public static byte[] BuildRequest(byte sequence)
        {
            return ProtocolBytes.Message(Verbs.AVERS, new[] { sequence });
        }

        /// <summary>Build an SVERS response (used when acting as a spa, e.g. in tests).</summary>
        public static byte[] BuildResponse(SpaVersions versions)
        {
            var body = new byte[8];
            ProtocolBytes.WriteUInt16BigEndian(body, 0, versions.EnBuild);
            body[2] = (byte)versions.EnMajor;
            body[3] = (byte)versions.EnMinor;
            ProtocolBytes.WriteUInt16BigEndian(body, 4, versions.CoBuild);
            body[6] = (byte)versions.CoMajor;
            body[7] = (byte)versions.CoMinor;
            return ProtocolBytes.Message(Verbs.SVERS, body);
        }

        /// <summary>Parse an SVERS response.</summary>
        public static bool TryParseResponse(byte[] payload, out SpaVersions versions)
        {
            versions = null;
            if (!ProtocolBytes.HasVerb(payload, Verbs.SVERS)) return false;

            byte[] body = ProtocolBytes.Remainder(payload);
            if (body.Length < 8) return false;

            versions = new SpaVersions(
                ProtocolBytes.ReadUInt16BigEndian(body, 0), body[2], body[3],
                ProtocolBytes.ReadUInt16BigEndian(body, 4), body[6], body[7]);
            return true;
        }
    }
}
