using System.Collections.Generic;
using GeckoLib.NET.Protocol;
using GeckoLib.NET.Protocol.Messages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestGeckoLib.Infrastructure;

namespace TestGeckoLib
{
    /// <summary>
    /// Byte-exact checks for every message the library builds or parses.
    ///
    /// Every expectation here is lifted from geckolib's own protocol tests
    /// (tests/test_protocol_*.py), which were themselves taken from Wireshark captures
    /// of the real app. They are the closest thing to a specification that exists.
    /// </summary>
    [TestClass]
    public class MessageTests
    {
        [TestMethod]
        public void APingRequestIsJustTheVerbAndTheResponseCarriesASequence()
        {
            Wire.AssertEqual(Wire.Build("APING"), PingMessage.BuildRequest());
            Wire.AssertEqual(Wire.Build("APING", 0), PingMessage.BuildResponse(0));

            Assert.AreEqual((byte)0, PingMessage.ParseSequence(Wire.Build("APING", 0)));
            Assert.IsNull(PingMessage.ParseSequence(Wire.Build("APING")));
        }

        [TestMethod]
        public void AVersionRequestCarriesASequenceAndTheResponseSixFields()
        {
            Wire.AssertEqual(Wire.Build("AVERS", 1), VersionMessage.BuildRequest(1));

            byte[] response = Wire.Build("SVERS", 0, 1, 2, 3, 0, 4, 5, 6);
            Wire.AssertEqual(response, VersionMessage.BuildResponse(new SpaVersions(1, 2, 3, 4, 5, 6)));

            Assert.IsTrue(VersionMessage.TryParseResponse(response, out SpaVersions versions));
            Assert.AreEqual(1, versions.EnBuild);
            Assert.AreEqual(2, versions.EnMajor);
            Assert.AreEqual(3, versions.EnMinor);
            Assert.AreEqual(4, versions.CoBuild);
            Assert.AreEqual(5, versions.CoMajor);
            Assert.AreEqual(6, versions.CoMinor);
            Assert.AreEqual("1 v2.3", versions.EnVersion);
        }

        [TestMethod]
        public void AChannelResponseGivesTheChannelAndSignalStrength()
        {
            Wire.AssertEqual(Wire.Build("CURCH", 1), ChannelMessage.BuildRequest(1));
            Wire.AssertEqual(Wire.Build("CHCUR", 0x0a, 0x21), ChannelMessage.BuildResponse(10, 33));

            Assert.IsTrue(ChannelMessage.TryParseResponse(Wire.Build("CHCUR", 0x0a, 0x21), out ChannelInfo info));
            Assert.AreEqual(10, info.Channel);
            Assert.AreEqual(33, info.SignalStrength);
        }

        [TestMethod]
        public void TheConfigFileResponseNamesThePackAndItsStructureVersions()
        {
            Wire.AssertEqual(Wire.Build("SFILE", 1), ConfigFileMessage.BuildRequest(1));
            Wire.AssertEqual(
                Wire.Build("FILES,inXM_C07.xml,inXM_S08.xml"),
                ConfigFileMessage.BuildResponse("inXM", 7, 8));

            Assert.IsTrue(ConfigFileMessage.TryParseResponse(
                Wire.Build("FILES,inXM_C09.xml,inXM_S09.xml"), out PackFileInfo info));
            Assert.AreEqual("inXM", info.PlatformKey);
            Assert.AreEqual(9, info.ConfigVersion);
            Assert.AreEqual(9, info.LogVersion);
        }

        [TestMethod]
        public void AConfigFileResponseNamingTwoDifferentPlatformsIsRejected()
        {
            Assert.IsFalse(ConfigFileMessage.TryParseResponse(
                Wire.Build("FILES,inXM_C09.xml,inYE_S09.xml"), out _));
        }

        /// <summary>
        /// Two platform names the spa reports differ from the names the pack definitions
        /// use, and geckolib rewrites them (const.py:172-175).
        /// </summary>
        [TestMethod]
        public void ThePlatformNamesThatNeedRewritingAreRewritten()
        {
            Assert.IsTrue(ConfigFileMessage.TryParseResponse(
                Wire.Build("FILES,MrSt_C03.xml,MrSt_S03.xml"), out PackFileInfo mrSteam));
            Assert.AreEqual("MrSteam", mrSteam.PlatformKey);

            Assert.IsTrue(ConfigFileMessage.TryParseResponse(
                Wire.Build("FILES,MASI_C01.xml,MASI_S01.xml"), out PackFileInfo masIbc));
            Assert.AreEqual("MAS-IBC-32K", masIbc.PlatformKey);
        }

        [TestMethod]
        public void AStatusBlockRequestCarriesTheStartAndLengthBigEndian()
        {
            // 637 is 0x027d.
            Wire.AssertEqual(
                Wire.Build("STATU", 1, 0x00, 0x00, 0x02, 0x7d),
                StatusBlockMessage.BuildRequest(1, 0, 637));

            // A full read is the whole 1024 bytes, 0x0400.
            Wire.AssertEqual(
                Wire.Build("STATU", 1, 0x00, 0x00, 0x04, 0x00),
                StatusBlockMessage.BuildFullRequest(1));
        }

        [TestMethod]
        public void AStatusBlockSegmentReportsItsIndexTheNextOneAndItsData()
        {
            byte[] response = Wire.Build("STATV", 3, 4, 4, 1, 2, 3, 4);
            Wire.AssertEqual(response, StatusBlockMessage.BuildResponse(3, 4, Wire.Build(1, 2, 3, 4)));

            Assert.IsTrue(StatusBlockMessage.TryParseResponse(response, out StatusBlockSegment segment));
            Assert.AreEqual(3, segment.Sequence);
            Assert.AreEqual(4, segment.Next);
            Assert.IsFalse(segment.IsLast);
            Wire.AssertEqual(Wire.Build(1, 2, 3, 4), segment.Data);
        }

        [TestMethod]
        public void AStatusBlockSegmentWithNoSuccessorIsTheLastOne()
        {
            Assert.IsTrue(StatusBlockMessage.TryParseResponse(
                Wire.Build("STATV", 3, 0, 4, 1, 2, 3, 4), out StatusBlockSegment segment));

            Assert.AreEqual(3, segment.Sequence);
            Assert.AreEqual(0, segment.Next);
            Assert.IsTrue(segment.IsLast);
        }

        [TestMethod]
        public void APartialStatusPushListsEachChangedPositionAndItsTwoBytes()
        {
            var changes = new List<StatusChange>
            {
                new StatusChange(365, Wire.Build(0x03, 0x84)),
                new StatusChange(366, Wire.Build(0x84, 0x0c))
            };

            // 365 is 0x016d, 366 is 0x016e.
            Wire.AssertEqual(
                Wire.Build("STATP", 2, 0x01, 0x6d, 0x03, 0x84, 0x01, 0x6e, 0x84, 0x0c),
                PartialStatusMessage.BuildPush(changes));
        }

        [TestMethod]
        public void APartialStatusPushIsParsedBackIntoItsChanges()
        {
            Assert.IsTrue(PartialStatusMessage.TryParsePush(
                Wire.Build("STATP", 3, 0x01, 0x6d, 0x03, 0x84, 0x01, 0x6e, 0x84, 0x0c, 0x01, 0x6f, 0x01, 0x02),
                out IList<StatusChange> changes));

            Assert.AreEqual(3, changes.Count);
            Assert.AreEqual(365, changes[0].Position);
            Wire.AssertEqual(Wire.Build(0x03, 0x84), changes[0].Data);
            Assert.AreEqual(367, changes[2].Position);
            Wire.AssertEqual(Wire.Build(0x01, 0x02), changes[2].Data);
        }

        [TestMethod]
        public void APartialStatusAckCarriesTheCommandSequence()
        {
            Wire.AssertEqual(Wire.Build("STATQ", 192), PartialStatusMessage.BuildAck(192));
        }

        [TestMethod]
        public void AKeyPressCommandNamesThePackTypeAndTheKey()
        {
            // seq 1, pack type 6, length 2, command 57 (0x39), key 1.
            Wire.AssertEqual(
                Wire.Build("SPACK", 0x01, 0x06, 0x02, 0x39, 0x01),
                PackCommandMessage.BuildKeyPress(1, 6, 1));
        }

        [TestMethod]
        public void ASetValueCommandCarriesTheStructureVersionsPositionAndData()
        {
            // seq 1, pack type 6, length 5+2, command 70 (0x46), config 9, log 9,
            // position 15 (0x000f), value 702 (0x02be).
            Wire.AssertEqual(
                Wire.Build("SPACK", 0x01, 0x06, 0x07, 0x46, 0x09, 0x09, 0x00, 0x0f, 0x02, 0xbe),
                PackCommandMessage.BuildSetValue(1, 6, 9, 9, 15, Wire.Build(0x02, 0xbe)));
        }

        [TestMethod]
        public void APackCommandIsAcknowledgedWithABareVerb()
        {
            Wire.AssertEqual(Wire.Build("PACKS"), PackCommandMessage.BuildAck());
            Assert.IsTrue(PackCommandMessage.IsAck(Wire.Build("PACKS")));
            Assert.IsFalse(PackCommandMessage.IsAck(Wire.Build("OTHER")));
        }

        [TestMethod]
        public void WatercareModesAreReadAndWrittenByTheirIndex()
        {
            Wire.AssertEqual(Wire.Build("GETWC", 1), WatercareMessage.BuildGetRequest(1));
            Wire.AssertEqual(Wire.Build("WCGET", 3), WatercareMessage.BuildGetResponse(EWatercareMode.SuperEnergySaving));
            Wire.AssertEqual(Wire.Build("SETWC", 1, 2), WatercareMessage.BuildSetRequest(1, EWatercareMode.EnergySaving));
            Wire.AssertEqual(Wire.Build("WCSET", 2), WatercareMessage.BuildSetResponse(EWatercareMode.EnergySaving));

            Assert.IsTrue(WatercareMessage.TryParseMode(Wire.Build("WCGET", 3), out EWatercareMode mode));
            Assert.AreEqual(EWatercareMode.SuperEnergySaving, mode);
            Assert.AreEqual("Super Energy Saving", WatercareMessage.ToDisplayName(mode));
        }

        /// <summary>
        /// geckolib reduces a received water care mode modulo the number of modes rather
        /// than rejecting it, so an out-of-range value wraps.
        /// </summary>
        [TestMethod]
        public void AnOutOfRangeWatercareModeWrapsRatherThanFailing()
        {
            Assert.IsTrue(WatercareMessage.TryParseMode(Wire.Build("WCGET", 7), out EWatercareMode mode));
            Assert.AreEqual(EWatercareMode.EnergySaving, mode);
        }

        /// <summary>
        /// Reminder records are the one little-endian structure in the protocol, and the
        /// day count is signed - an overdue reminder is negative.
        /// </summary>
        [TestMethod]
        public void ReminderRecordsArePackedLittleEndianWithASignedDayCount()
        {
            var reminders = new List<Reminder>
            {
                new Reminder(EReminderType.RinseFilter, -13),
                new Reminder(EReminderType.CleanFilter, 257),
                new Reminder(EReminderType.ChangeWater, 2),
                new Reminder(EReminderType.CheckSpa, 512),
                new Reminder(EReminderType.ChangeOzonator, 0),
                new Reminder(EReminderType.ChangeVisionCartridge, 128),
                new Reminder(EReminderType.Invalid, 1)
            };

            Wire.AssertEqual(
                Wire.Build(
                    "RMREQ",
                    0x01, 0xf3, 0xff, 0x01,
                    0x02, 0x01, 0x01, 0x01,
                    0x03, 0x02, 0x00, 0x01,
                    0x04, 0x00, 0x02, 0x01,
                    0x05, 0x00, 0x00, 0x01,
                    0x06, 0x80, 0x00, 0x01,
                    0x00, 0x01, 0x00, 0x01),
                ReminderMessage.BuildResponse(reminders));
        }

        [TestMethod]
        public void ARealReminderResponseIsParsedBackIntoDaysRemaining()
        {
            // Captured from a real spa; geckolib test_protocol_reminders.py.
            byte[] payload = Wire.Build(
                "RMREQ",
                0x01, 0x01, 0x00, 0x01,
                0x02, 0x1f, 0x00, 0x01,
                0x03, 0x29, 0x00, 0x01,
                0x04, 0xa9, 0x02, 0x01,
                0x00, 0x01, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00);

            Assert.IsTrue(ReminderMessage.TryParseResponse(payload, out IList<Reminder> reminders));

            Assert.AreEqual(10, reminders.Count);
            Assert.AreEqual(EReminderType.RinseFilter, reminders[0].Type);
            Assert.AreEqual(1, reminders[0].Days);
            Assert.AreEqual(EReminderType.CleanFilter, reminders[1].Type);
            Assert.AreEqual(31, reminders[1].Days);
            Assert.AreEqual(EReminderType.ChangeWater, reminders[2].Type);
            Assert.AreEqual(41, reminders[2].Days);
            Assert.AreEqual(EReminderType.CheckSpa, reminders[3].Type);
            Assert.AreEqual(681, reminders[3].Days);
            Assert.AreEqual(EReminderType.Invalid, reminders[4].Type);
        }

        [TestMethod]
        public void ARequestForRemindersCarriesOnlyASequence()
        {
            Wire.AssertEqual(Wire.Build("REQRM", 1), ReminderMessage.BuildRequest(1));
            Wire.AssertEqual(Wire.Build("RMSET"), ReminderMessage.BuildSetAck());
            Assert.IsTrue(ReminderMessage.IsSetAck(Wire.Build("RMSET")));
        }

        [TestMethod]
        public void ASetRemindersCommandCarriesTheSequenceThenTheRecords()
        {
            var reminders = new List<Reminder> { new Reminder(EReminderType.RinseFilter, -13) };

            Wire.AssertEqual(
                Wire.Build("SETRM", 1, 0x01, 0xf3, 0xff, 0x01),
                ReminderMessage.BuildSetRequest(1, reminders));
        }
    }
}
