using System;
using System.Collections.Generic;

namespace GeckoLib.NET.Protocol.Messages
{
    /// <summary>
    /// Which spa pack the spa is running, and which config and log structure versions.
    /// This is what selects the pack definitions to load.
    /// </summary>
    public sealed class PackFileInfo
    {
        public PackFileInfo(string platformKey, int configVersion, int logVersion)
        {
            PlatformKey = platformKey;
            ConfigVersion = configVersion;
            LogVersion = logVersion;
        }

        /// <summary>Platform name, e.g. "inXE", with the known aliases already resolved.</summary>
        public string PlatformKey { get; }

        public int ConfigVersion { get; }

        public int LogVersion { get; }

        public override string ToString()
        {
            return string.Format("{0} config {1} log {2}", PlatformKey, ConfigVersion, LogVersion);
        }
    }

    /// <summary>
    /// SFILE request / FILES response. The response looks like
    /// <c>FILES,inYT_C61.xml,inYT_S61.xml</c>.
    /// </summary>
    public static class ConfigFileMessage
    {
        /// <summary>
        /// Platform names the spa reports that do not match the pack definition names.
        /// From geckolib const.py:172-175.
        /// </summary>
        private static readonly Dictionary<string, string> PACK_NAME_ADJUSTMENTS =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "MrSt", "MrSteam" },
                { "MASI", "MAS-IBC-32K" }
            };

        /// <summary>Build an SFILE request.</summary>
        public static byte[] BuildRequest(byte sequence)
        {
            return ProtocolBytes.Message(Verbs.SFILE, new[] { sequence });
        }

        /// <summary>Build a FILES response (used when acting as a spa, e.g. in tests).</summary>
        public static byte[] BuildResponse(string platformKey, int configVersion, int logVersion)
        {
            string body = string.Format(
                ",{0}_C{1:00}.xml,{0}_S{2:00}.xml", platformKey, configVersion, logVersion);
            return ProtocolBytes.Message(Verbs.FILES, ProtocolBytes.FromText(body));
        }

        /// <summary>
        /// Parse a FILES response. Returns false if the two filenames disagree about the
        /// platform, which geckolib treats as a hard error.
        /// </summary>
        public static bool TryParseResponse(byte[] payload, out PackFileInfo info)
        {
            info = null;
            if (!ProtocolBytes.HasVerb(payload, Verbs.FILES)) return false;

            // Skip the verb AND the leading comma.
            if (payload.Length <= ProtocolBytes.VERB_LENGTH + 1) return false;
            string text = ProtocolBytes.ToText(
                payload,
                ProtocolBytes.VERB_LENGTH + 1,
                payload.Length - ProtocolBytes.VERB_LENGTH - 1);

            text = text.Replace(".xml", string.Empty);
            string[] files = text.Split(',');
            if (files.Length < 2) return false;

            string[] config = files[0].Split('_');
            string[] log = files[1].Split('_');
            if (config.Length < 2 || log.Length < 2) return false;
            if (!string.Equals(config[0], log[0], StringComparison.Ordinal)) return false;

            string platformKey = config[0];
            string adjusted;
            if (PACK_NAME_ADJUSTMENTS.TryGetValue(platformKey, out adjusted))
            {
                platformKey = adjusted;
            }

            int configVersion;
            int logVersion;
            if (!int.TryParse(config[1].Substring(1), out configVersion)) return false;
            if (!int.TryParse(log[1].Substring(1), out logVersion)) return false;

            info = new PackFileInfo(platformKey, configVersion, logVersion);
            return true;
        }
    }
}
