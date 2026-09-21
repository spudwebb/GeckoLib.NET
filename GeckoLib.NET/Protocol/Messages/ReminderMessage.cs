using System;
using System.Collections.Generic;

namespace GeckoLib.NET.Protocol.Messages
{
    /// <summary>Maintenance reminder types, in wire order.</summary>
    public enum EReminderType
    {
        Invalid = 0,
        RinseFilter = 1,
        CleanFilter = 2,
        ChangeWater = 3,
        CheckSpa = 4,
        ChangeOzonator = 5,
        ChangeVisionCartridge = 6
    }

    /// <summary>A maintenance reminder and how many days until it is due.</summary>
    public sealed class Reminder
    {
        public Reminder(EReminderType type, short days)
        {
            Type = type;
            Days = days;
        }

        public EReminderType Type { get; }

        /// <summary>Days until due. Signed - an overdue reminder goes negative.</summary>
        public short Days { get; }

        public override string ToString()
        {
            return string.Format("{0} due in {1} days", Type, Days);
        }
    }

    /// <summary>
    /// REQRM/RMREQ to read reminders, SETRM/RMSET to write them.
    ///
    /// Each record is three fields packed LITTLE-endian - type, a signed day count and a
    /// push flag - which is the one place the protocol departs from big-endian
    /// (geckolib reminders.py:78 packs them as <c><![CDATA[<BhB]]></c>).
    /// </summary>
    public static class ReminderMessage
    {
        private const int RECORD_LENGTH = 4;

        /// <summary>Build a REQRM request.</summary>
        public static byte[] BuildRequest(byte sequence)
        {
            return ProtocolBytes.Message(Verbs.REQRM, new[] { sequence });
        }

        /// <summary>Build an RMREQ response (used when acting as a spa, e.g. in tests).</summary>
        public static byte[] BuildResponse(IList<Reminder> reminders)
        {
            return ProtocolBytes.Message(Verbs.RMREQ, EncodeRecords(reminders));
        }

        /// <summary>Build a SETRM command.</summary>
        public static byte[] BuildSetRequest(byte sequence, IList<Reminder> reminders)
        {
            return ProtocolBytes.Message(Verbs.SETRM, new[] { sequence }, EncodeRecords(reminders));
        }

        /// <summary>Build an RMSET ack (used when acting as a spa, e.g. in tests).</summary>
        public static byte[] BuildSetAck()
        {
            return ProtocolBytes.Message(Verbs.RMSET);
        }

        /// <summary>Parse an RMREQ response. Unknown reminder types are skipped.</summary>
        public static bool TryParseResponse(byte[] payload, out IList<Reminder> reminders)
        {
            reminders = null;
            if (!ProtocolBytes.HasVerb(payload, Verbs.RMREQ)) return false;

            reminders = DecodeRecords(ProtocolBytes.Remainder(payload));
            return true;
        }

        /// <summary>Is this the ack for a reminder write?</summary>
        public static bool IsSetAck(byte[] payload)
        {
            return ProtocolBytes.HasVerb(payload, Verbs.RMSET);
        }

        private static byte[] EncodeRecords(IList<Reminder> reminders)
        {
            if (reminders == null) throw new ArgumentNullException(nameof(reminders));

            var body = new byte[reminders.Count * RECORD_LENGTH];
            for (int i = 0; i < reminders.Count; i++)
            {
                int offset = i * RECORD_LENGTH;
                body[offset] = (byte)reminders[i].Type;
                ProtocolBytes.WriteInt16LittleEndian(body, offset + 1, reminders[i].Days);
                body[offset + 3] = 1;
            }

            return body;
        }

        private static IList<Reminder> DecodeRecords(byte[] body)
        {
            var reminders = new List<Reminder>();
            for (int offset = 0; offset + RECORD_LENGTH <= body.Length; offset += RECORD_LENGTH)
            {
                byte type = body[offset];
                short days = ProtocolBytes.ReadInt16LittleEndian(body, offset + 1);

                if (!Enum.IsDefined(typeof(EReminderType), (int)type))
                {
                    // geckolib logs and skips these rather than failing the exchange.
                    continue;
                }

                reminders.Add(new Reminder((EReminderType)type, days));
            }

            return reminders;
        }
    }
}
