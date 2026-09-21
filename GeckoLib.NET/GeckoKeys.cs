namespace GeckoLib.NET
{
    /// <summary>
    /// Pack structure field names the library itself depends on. Everything else is
    /// looked up by whatever key the pack definitions declare.
    /// </summary>
    public static class GeckoKeys
    {
        /// <summary>Numeric pack type, sent with every pack command.</summary>
        public const string PACK_TYPE = "PackType";

        /// <summary>Which units the spa reports temperatures in, "C" or "F".</summary>
        public const string TEMP_UNITS = "TempUnits";

        /// <summary>Low-level configuration number.</summary>
        public const string CONFIG_NUMBER = "ConfigNumber";

        // Pack firmware identity. Newer packs report Core, older ones only Conf.
        public const string PACK_CORE_ID = "PackCoreID";
        public const string PACK_CORE_REV = "PackCoreRev";
        public const string PACK_CORE_REL = "PackCoreRel";
        public const string PACK_CONFIG_ID = "PackConfID";
        public const string PACK_CONFIG_REV = "PackConfRev";
        public const string PACK_CONFIG_REL = "PackConfRel";

        /// <summary>Keypad button codes, from geckolib const.py:117-129.</summary>
        public static class Keypad
        {
            public const int PUMP_1 = 1;
            public const int PUMP_2 = 2;
            public const int PUMP_3 = 3;
            public const int PUMP_4 = 4;
            public const int PUMP_5 = 5;
            public const int BLOWER = 6;
            public const int LIGHT = 16;
            public const int LIGHT_120 = 17;
            public const int UP = 21;
            public const int DOWN = 22;
            public const int WATERFALL = 23;
            public const int AUX = 24;
            public const int ECO_MODE = 0;
        }

        /// <summary>
        /// The inMix lighting accessory is detected by peeking at fixed offsets in the
        /// status block, before any accessors exist (geckolib async_spastruct.py:322-333).
        /// </summary>
        public static class InMix
        {
            /// <summary>Offset of the accessory's pack type byte.</summary>
            public const int PACK_TYPE_POSITION = 628;

            /// <summary>The value that byte has when an inMix is fitted.</summary>
            public const int PACK_TYPE = 14;

            /// <summary>Offset of the accessory's config structure version.</summary>
            public const int CONFIG_LIB_POSITION = 634;

            /// <summary>Offset of the accessory's log structure version.</summary>
            public const int STATUS_LIB_POSITION = 635;

            /// <summary>Platform key the accessory's definitions are filed under.</summary>
            public const string PLATFORM_KEY = "inmix";
        }
    }
}
