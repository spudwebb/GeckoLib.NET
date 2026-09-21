using System;
using GeckoLib.NET;

namespace GeckoLib.NET.Protocol
{
    /// <summary>What a received HELLO datagram turned out to be.</summary>
    public enum EHelloKind
    {
        /// <summary>A discovery broadcast - ours, echoed back to us, or another client's.</summary>
        BroadcastRequest,

        /// <summary>Another client announcing itself (the phone app identifies as IOS/AND).</summary>
        ClientIdentifier,

        /// <summary>A spa answering discovery.</summary>
        SpaResponse
    }

    /// <summary>
    /// The HELLO message used for discovery.
    ///
    /// Discovery is the one exchange that is not wrapped in the PACKT envelope - it goes
    /// out bare to the broadcast address. Mirrors geckolib's driver/protocol/hello.py.
    /// </summary>
    public sealed class HelloMessage
    {
        private const string OPEN = "<HELLO>";
        private const string CLOSE = "</HELLO>";

        /// <summary>The content a discovery broadcast carries.</summary>
        private const string BROADCAST_CONTENT = "1";

        private HelloMessage(EHelloKind kind, string spaIdentifier, string spaName, string clientIdentifier)
        {
            Kind = kind;
            SpaIdentifier = spaIdentifier;
            SpaName = spaName;
            ClientIdentifier = clientIdentifier;
        }

        /// <summary>What this message is.</summary>
        public EHelloKind Kind { get; }

        /// <summary>Spa identifier, set when <see cref="Kind"/> is <see cref="EHelloKind.SpaResponse"/>.</summary>
        public string SpaIdentifier { get; }

        /// <summary>Spa name, set when <see cref="Kind"/> is <see cref="EHelloKind.SpaResponse"/>.</summary>
        public string SpaName { get; }

        /// <summary>Client identifier, set when <see cref="Kind"/> is <see cref="EHelloKind.ClientIdentifier"/>.</summary>
        public string ClientIdentifier { get; }

        /// <summary>
        /// Build the discovery broadcast datagram, <c><![CDATA[<HELLO>1</HELLO>]]></c>.
        /// </summary>
        public static byte[] BuildBroadcastRequest()
        {
            return GeckoConstants.MessageEncoding.GetBytes(OPEN + BROADCAST_CONTENT + CLOSE);
        }

        /// <summary>
        /// Parse a received datagram. Returns false for anything that is not a
        /// well-formed HELLO message.
        /// </summary>
        public static bool TryParse(byte[] datagram, out HelloMessage message)
        {
            message = null;
            if (datagram == null || datagram.Length < OPEN.Length + CLOSE.Length)
            {
                return false;
            }

            string text = GeckoConstants.MessageEncoding.GetString(datagram);
            if (!text.StartsWith(OPEN, StringComparison.Ordinal) ||
                !text.EndsWith(CLOSE, StringComparison.Ordinal))
            {
                return false;
            }

            string content = text.Substring(OPEN.Length, text.Length - OPEN.Length - CLOSE.Length);

            if (content == BROADCAST_CONTENT)
            {
                message = new HelloMessage(EHelloKind.BroadcastRequest, null, null, null);
                return true;
            }

            if (content.StartsWith("IOS", StringComparison.Ordinal) ||
                content.StartsWith("AND", StringComparison.Ordinal))
            {
                message = new HelloMessage(EHelloKind.ClientIdentifier, null, null, content);
                return true;
            }

            // A spa answers "<identifier>|<name>". geckolib splits on every "|" and unpacks
            // into two, so a name containing "|" raises there; splitting on the first
            // separator only is equivalent for well-formed input and does not throw.
            int separator = content.IndexOf('|');
            if (separator < 0)
            {
                message = new HelloMessage(EHelloKind.SpaResponse, content, "Unnamed SPA", null);
                return true;
            }

            string identifier = content.Substring(0, separator);
            string name = content.Substring(separator + 1);
            if (identifier.Length == 0)
            {
                return false;
            }

            message = new HelloMessage(
                EHelloKind.SpaResponse,
                identifier,
                name.Length == 0 ? "Unnamed SPA" : name,
                null);
            return true;
        }
    }
}
