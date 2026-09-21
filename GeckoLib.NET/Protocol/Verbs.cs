namespace GeckoLib.NET.Protocol
{
    /// <summary>
    /// The five-character verbs that open every in.touch2 message.
    ///
    /// Requests and their responses use different verbs, and the response never echoes
    /// the request's sequence number, so exchanges are correlated by response verb.
    /// </summary>
    internal static class Verbs
    {
        /// <summary>Keepalive, request and response.</summary>
        public const string APING = "APING";

        /// <summary>Firmware version request / response.</summary>
        public const string AVERS = "AVERS";
        public const string SVERS = "SVERS";

        /// <summary>Radio channel request / response.</summary>
        public const string CURCH = "CURCH";
        public const string CHCUR = "CHCUR";

        /// <summary>Pack config file names request / response.</summary>
        public const string SFILE = "SFILE";
        public const string FILES = "FILES";

        /// <summary>Status block range request / segment response.</summary>
        public const string STATU = "STATU";
        public const string STATV = "STATV";

        /// <summary>Partial status push, and the ack the client must send back.</summary>
        public const string STATP = "STATP";
        public const string STATQ = "STATQ";

        /// <summary>Pack command (set value / key press) and its ack.</summary>
        public const string SPACK = "SPACK";
        public const string PACKS = "PACKS";

        /// <summary>Watercare mode get / set, with responses.</summary>
        public const string GETWC = "GETWC";
        public const string WCGET = "WCGET";
        public const string SETWC = "SETWC";
        public const string WCSET = "WCSET";
        public const string WCERR = "WCERR";

        /// <summary>Reminders request / response and set / ack.</summary>
        public const string REQRM = "REQRM";
        public const string RMREQ = "RMREQ";
        public const string SETRM = "SETRM";
        public const string RMSET = "RMSET";

        /// <summary>The EN module cannot reach the CO module.</summary>
        public const string RFERR = "RFERR";
    }
}
