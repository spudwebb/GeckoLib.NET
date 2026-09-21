using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GeckoLib.NET.Packs
{
    /// <summary>
    /// Looks up spa pack definitions by platform and version.
    ///
    /// geckolib resolves these by importing a Python module whose name it computes -
    /// "inxe", "inxe-cfg-60", "inxe-log-58" (async_spastruct.py:214-240). We keep the
    /// same keys, but they index into the extracted data file instead.
    ///
    /// The definitions are held as raw JSON and parsed on demand: a spa needs three of
    /// the 187 entries, so materialising them all would waste most of the work.
    /// </summary>
    public sealed class PackRegistry
    {
        private readonly byte[] _json;
        private readonly string[][] _enumTables;

        private PackRegistry(byte[] json, string[][] enumTables, string revision)
        {
            _json = json;
            _enumTables = enumTables;
            Revision = revision;
        }

        /// <summary>The SpaPackStruct revision the definitions were extracted from.</summary>
        public string Revision { get; }

        /// <summary>Load the definitions embedded in this assembly.</summary>
        public static PackRegistry LoadEmbedded()
        {
            return Load(PackDataResource.ReadAllBytes());
        }

        /// <summary>Load definitions from decompressed JSON.</summary>
        public static PackRegistry Load(byte[] json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));

            string revision = null;
            var enumTables = new List<string[]>();

            using (JsonTextReader reader = OpenReader(json))
            {
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.PropertyName || reader.Depth != 1) continue;

                    string name = (string)reader.Value;
                    if (name == "revision")
                    {
                        revision = reader.ReadAsString();
                    }
                    else if (name == "enumTables")
                    {
                        reader.Read();
                        var tables = JArray.Load(reader);
                        foreach (JToken table in tables)
                        {
                            enumTables.Add(table.ToObject<string[]>());
                        }
                    }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }

                    if (revision != null && enumTables.Count > 0) break;
                }
            }

            return new PackRegistry(json, enumTables.ToArray(), revision);
        }

        /// <summary>
        /// Look up a platform. <paramref name="platformKey"/> is matched case-insensitively,
        /// as geckolib lowercases it before resolving.
        /// </summary>
        public bool TryGetPack(string platformKey, out PackDefinition pack)
        {
            pack = null;
            JObject entry = FindEntry("packs", Normalise(platformKey));
            if (entry == null) return false;

            pack = new PackDefinition(
                (string)entry["name"],
                (int)entry["plateformType"],
                (string)entry["plateformSegment"],
                (string)entry["revision"]);
            return true;
        }

        /// <summary>Look up a config structure for a platform and version.</summary>
        public bool TryGetConfig(string platformKey, int version, out ConfigStructDefinition config)
        {
            config = null;
            string key = string.Format(CultureInfo.InvariantCulture, "{0}-cfg-{1}", Normalise(platformKey), version);
            JObject entry = FindEntry("configs", key);
            if (entry == null) return false;

            config = new ConfigStructDefinition(
                (int)entry["version"],
                entry["outputKeys"].ToObject<string[]>(),
                ReadAccessors((JArray)entry["accessors"]));
            return true;
        }

        /// <summary>Look up a log structure for a platform and version.</summary>
        public bool TryGetLog(string platformKey, int version, out LogStructDefinition log)
        {
            log = null;
            string key = string.Format(CultureInfo.InvariantCulture, "{0}-log-{1}", Normalise(platformKey), version);
            JObject entry = FindEntry("logs", key);
            if (entry == null) return false;

            log = new LogStructDefinition(
                (int)entry["version"],
                (int)entry["begin"],
                (int)entry["end"],
                entry["outputKeys"].ToObject<string[]>(),
                entry["allDeviceKeys"].ToObject<string[]>(),
                entry["userDemandKeys"].ToObject<string[]>(),
                entry["errorKeys"].ToObject<string[]>(),
                ReadAccessors((JArray)entry["accessors"]));
            return true;
        }

        private static string Normalise(string platformKey)
        {
            return platformKey == null ? null : platformKey.ToLowerInvariant();
        }

        /// <summary>
        /// Stream to one named entry under one top-level section, skipping everything
        /// else rather than building the whole document.
        /// </summary>
        private JObject FindEntry(string section, string key)
        {
            if (key == null) return null;

            using (JsonTextReader reader = OpenReader(_json))
            {
                if (!SeekProperty(reader, section, 1)) return null;

                reader.Read();
                if (reader.TokenType != JsonToken.StartObject) return null;

                while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                {
                    bool match = string.Equals((string)reader.Value, key, StringComparison.Ordinal);
                    reader.Read();
                    if (match) return JObject.Load(reader);
                    reader.Skip();
                }
            }

            return null;
        }

        private static bool SeekProperty(JsonTextReader reader, string name, int depth)
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonToken.PropertyName || reader.Depth != depth) continue;
                if (string.Equals((string)reader.Value, name, StringComparison.Ordinal)) return true;

                reader.Read();
                reader.Skip();
            }

            return false;
        }

        private IList<AccessorDefinition> ReadAccessors(JArray records)
        {
            var accessors = new List<AccessorDefinition>(records.Count);
            foreach (JToken token in records)
            {
                // [key, path, kind, pos, bitpos, enumTableIndex, size, maxitems, rw]
                var record = (JArray)token;
                int? enumIndex = ToNullableInt(record[5]);

                accessors.Add(new AccessorDefinition(
                    (string)record[0],
                    (string)record[1],
                    ParseKind((string)record[2]),
                    (int)record[3],
                    ToNullableInt(record[4]),
                    enumIndex.HasValue ? _enumTables[enumIndex.Value] : null,
                    ToNullableInt(record[6]),
                    ToNullableInt(record[7]),
                    record[8].Type != JTokenType.Null));
            }

            return accessors;
        }

        private static int? ToNullableInt(JToken token)
        {
            return token == null || token.Type == JTokenType.Null ? (int?)null : (int)token;
        }

        private static EAccessorKind ParseKind(string kind)
        {
            switch (kind)
            {
                case "Byte": return EAccessorKind.Byte;
                case "Word": return EAccessorKind.Word;
                case "Time": return EAccessorKind.Time;
                case "Bool": return EAccessorKind.Bool;
                case "Enum": return EAccessorKind.Enum;
                case "Temp": return EAccessorKind.Temp;
                default: throw new InvalidDataException("Unknown accessor kind: " + kind);
            }
        }

        private static JsonTextReader OpenReader(byte[] json)
        {
            return new JsonTextReader(new StreamReader(new MemoryStream(json, false)));
        }
    }
}
