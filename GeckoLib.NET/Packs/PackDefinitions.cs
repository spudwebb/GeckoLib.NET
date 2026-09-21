using System.Collections.Generic;

namespace GeckoLib.NET.Packs
{
    /// <summary>Identity of a spa pack platform, e.g. InXE.</summary>
    public sealed class PackDefinition
    {
        public PackDefinition(string name, int platformType, string platformSegment, string revision)
        {
            Name = name;
            PlatformType = platformType;
            PlatformSegment = platformSegment;
            Revision = revision;
        }

        /// <summary>Platform name as the definitions spell it, e.g. "InXE".</summary>
        public string Name { get; }

        /// <summary>Numeric platform type, sent with every pack command.</summary>
        public int PlatformType { get; }

        /// <summary>"aMainControl" for a spa pack, "aAccessory" for an add-on like inMix.</summary>
        public string PlatformSegment { get; }

        /// <summary>The SpaPackStruct revision these definitions came from.</summary>
        public string Revision { get; }

        /// <summary>Is this an accessory rather than a main control pack?</summary>
        public bool IsAccessory
        {
            get { return PlatformSegment == "aAccessory"; }
        }

        public override string ToString()
        {
            return string.Format("{0} (type {1}, rev {2})", Name, PlatformType, Revision);
        }
    }

    /// <summary>A config structure: the spa's static configuration.</summary>
    public class ConfigStructDefinition
    {
        public ConfigStructDefinition(
            int version,
            IList<string> outputKeys,
            IList<AccessorDefinition> accessors)
        {
            Version = version;
            OutputKeys = outputKeys;
            Accessors = accessors;
        }

        public int Version { get; }

        /// <summary>Keys describing what each physical output is wired to.</summary>
        public IList<string> OutputKeys { get; }

        public IList<AccessorDefinition> Accessors { get; }
    }

    /// <summary>A log structure: the spa's live state.</summary>
    public sealed class LogStructDefinition : ConfigStructDefinition
    {
        public LogStructDefinition(
            int version,
            int begin,
            int end,
            IList<string> outputKeys,
            IList<string> allDeviceKeys,
            IList<string> userDemandKeys,
            IList<string> errorKeys,
            IList<AccessorDefinition> accessors)
            : base(version, outputKeys, accessors)
        {
            Begin = begin;
            End = end;
            AllDeviceKeys = allDeviceKeys;
            UserDemandKeys = userDemandKeys;
            ErrorKeys = errorKeys;
        }

        /// <summary>First status block offset covered by the log structure.</summary>
        public int Begin { get; }

        /// <summary>One past the last status block offset covered by the log structure.</summary>
        public int End { get; }

        /// <summary>Every device this pack can have, e.g. P1, P2, BL, CP, LI.</summary>
        public IList<string> AllDeviceKeys { get; }

        /// <summary>Keys holding what the user has asked each device to do.</summary>
        public IList<string> UserDemandKeys { get; }

        /// <summary>Keys that report faults.</summary>
        public IList<string> ErrorKeys { get; }
    }
}
