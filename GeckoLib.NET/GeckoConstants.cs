using System;
using System.Text;

namespace GeckoLib.NET
{
    /// <summary>
    /// Protocol and timing constants for the in.touch2 protocol.
    /// Values mirror geckolib's const.py and config.py.
    /// </summary>
    public static class GeckoConstants
    {
        /// <summary>The UDP port every in.touch2 module listens on.</summary>
        public const int INTOUCH2_PORT = 10022;

        /// <summary>
        /// Discovery broadcast address. This must be the literal dotted quad: the
        /// <c><![CDATA[<broadcast>]]></c> alias that some stacks accept is rejected by
        /// the Windows async socket path with WSAEINVAL (WinError 10022).
        /// </summary>
        public const string BROADCAST_ADDRESS = "255.255.255.255";

        /// <summary>How often the locator re-sends its discovery broadcast.</summary>
        public static readonly TimeSpan BROADCAST_INTERVAL = TimeSpan.FromSeconds(1);

        /// <summary>
        /// How long to keep listening before settling for the spas found so far.
        /// Discovery only stops this early if at least one spa has answered.
        /// </summary>
        public static readonly TimeSpan DISCOVERY_INITIAL_TIMEOUT = TimeSpan.FromSeconds(4);

        /// <summary>Hard upper bound on a discovery run.</summary>
        public static readonly TimeSpan DISCOVERY_TIMEOUT = TimeSpan.FromSeconds(10);

        private static readonly Encoding _messageEncoding = Encoding.GetEncoding(28591);

        /// <summary>
        /// Text encoding for everything on the wire: latin-1 (ISO-8859-1), not UTF-8.
        /// Note this cannot use Encoding.Latin1, which only exists on .NET 5 and later.
        /// </summary>
        public static Encoding MessageEncoding
        {
            get { return _messageEncoding; }
        }
    }
}
