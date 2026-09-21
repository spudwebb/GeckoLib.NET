using GeckoLib.NET.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestGeckoLib.Infrastructure;

namespace TestGeckoLib
{
    /// <summary>
    /// The PACKT envelope. Expectations come from geckolib
    /// tests/test_protocol_packet.py, which uses source "DESTID" and destination "SRCID".
    /// </summary>
    [TestClass]
    public class PacketTests
    {
        [TestMethod]
        public void APayloadIsWrappedInTheEnvelopeWithTheSourceFirst()
        {
            byte[] packet = GeckoPacket.Wrap("DESTID", "SRCID", Wire.Build("CONTENT"));

            Wire.AssertEqual(
                Wire.Build("<PACKT><SRCCN>DESTID</SRCCN><DESCN>SRCID</DESCN><DATAS>CONTENT</DATAS></PACKT>"),
                packet);
        }

        [TestMethod]
        public void AReceivedEnvelopeYieldsItsSourceDestinationAndPayload()
        {
            byte[] datagram = Wire.Build(
                "<PACKT><SRCCN>SRCID</SRCCN><DESCN>DESTID</DESCN><DATAS>DATA</DATAS></PACKT>");

            bool unwrapped = GeckoPacket.TryUnwrap(datagram, out string source, out string destination, out byte[] payload);

            Assert.IsTrue(unwrapped);
            Assert.AreEqual("SRCID", source);
            Assert.AreEqual("DESTID", destination);
            Wire.AssertEqual(Wire.Build("DATA"), payload);
        }

        [TestMethod]
        public void OnlyAWellFormedEnvelopeIsAccepted()
        {
            Assert.IsTrue(GeckoPacket.IsPacket(Wire.Build("<PACKT></PACKT>")));
            Assert.IsFalse(GeckoPacket.IsPacket(Wire.Build("<PACKT></PACKT")));
            Assert.IsFalse(GeckoPacket.IsPacket(Wire.Build("<PACKT></PACKT> ")));
            Assert.IsFalse(GeckoPacket.IsPacket(Wire.Build("<SOMETHING>")));
        }

        [TestMethod]
        public void AnEnvelopeWithoutTheExpectedFieldsIsRejected()
        {
            Assert.IsFalse(GeckoPacket.TryUnwrap(
                Wire.Build("<PACKT></PACKT>"), out _, out _, out _));

            Assert.IsFalse(GeckoPacket.TryUnwrap(
                Wire.Build("<PACKT><SRCCN>A</SRCCN><DATAS>X</DATAS></PACKT>"), out _, out _, out _));
        }

        /// <summary>
        /// geckolib matches the envelope with a greedy regex, so a payload containing the
        /// literal tag bytes confuses it. Scanning by position does not.
        /// </summary>
        [TestMethod]
        public void APayloadContainingTheClosingTagBytesIsStillReadCorrectly()
        {
            byte[] payload = Wire.Build("STATV", 1, 0, 8, "</DATAS>");
            byte[] datagram = GeckoPacket.Wrap("SPA", "CLIENT", payload);

            Assert.IsTrue(GeckoPacket.TryUnwrap(datagram, out string source, out string destination, out byte[] roundTripped));

            Assert.AreEqual("SPA", source);
            Assert.AreEqual("CLIENT", destination);
            Wire.AssertEqual(payload, roundTripped);
        }

        [TestMethod]
        public void AnEmptyPayloadSurvivesTheRoundTrip()
        {
            byte[] datagram = GeckoPacket.Wrap("SPA", "CLIENT", new byte[0]);

            Assert.IsTrue(GeckoPacket.TryUnwrap(datagram, out _, out _, out byte[] payload));
            Assert.AreEqual(0, payload.Length);
        }
    }
}
