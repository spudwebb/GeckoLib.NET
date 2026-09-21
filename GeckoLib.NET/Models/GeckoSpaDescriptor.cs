using System;
using System.Net;

namespace GeckoLib.NET.Models
{
    /// <summary>
    /// A spa found by <see cref="Discovery.GeckoLocator"/>, and everything needed to
    /// open a connection to it.
    /// </summary>
    public sealed class GeckoSpaDescriptor
    {
        /// <param name="identifier">The spa's own identifier, e.g. "SPA01:02:03:04:05:06".</param>
        /// <param name="name">The user-visible spa name.</param>
        /// <param name="address">Address the spa answered from.</param>
        /// <param name="port">Port the spa answered from.</param>
        public GeckoSpaDescriptor(string identifier, string name, IPAddress address, int port)
        {
            if (identifier == null) throw new ArgumentNullException(nameof(identifier));
            if (address == null) throw new ArgumentNullException(nameof(address));

            Identifier = identifier;
            Name = name;
            Address = address;
            Port = port;
        }

        /// <summary>The spa's own identifier, used as the destination id in packet framing.</summary>
        public string Identifier { get; }

        /// <summary>The user-visible spa name, as configured on the spa itself.</summary>
        public string Name { get; }

        /// <summary>Address the spa answered discovery from.</summary>
        public IPAddress Address { get; }

        /// <summary>Port the spa answered discovery from.</summary>
        public int Port { get; }

        /// <summary>The spa's endpoint.</summary>
        public IPEndPoint EndPoint
        {
            get { return new IPEndPoint(Address, Port); }
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return string.Format("{0} - {1} @ {2}:{3}", Name, Identifier, Address, Port);
        }
    }
}
