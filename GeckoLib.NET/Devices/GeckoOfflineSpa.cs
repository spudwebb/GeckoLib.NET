using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Packs;
using GeckoLib.NET.Protocol.Messages;
using GeckoLib.NET.Struct;

namespace GeckoLib.NET.Devices
{
    /// <summary>
    /// A spa built from a saved status block rather than a live connection.
    ///
    /// Everything that reads the pack structure works normally; anything that would have
    /// to talk to a spa - key presses, water care, reminders - reports that it could not.
    ///
    /// This exists so a captured status block can be decoded and inspected without a spa
    /// or a network, which is how the device layer is tested against geckolib's snapshot
    /// corpus. It is also handy for reproducing a report from a user who sent a snapshot.
    /// </summary>
    public sealed class GeckoOfflineSpa : IGeckoSpa
    {
        private GeckoOfflineSpa(
            string name,
            string platformKey,
            GeckoStatusBlock structure,
            ConfigStructDefinition config,
            LogStructDefinition log)
        {
            Name = name;
            PlatformKey = platformKey;
            Struct = structure;
            ConfigStruct = config;
            LogStruct = log;
        }

        /// <inheritdoc/>
        public string Name { get; }

        /// <inheritdoc/>
        public string UniqueId
        {
            get { return "offline-" + PlatformKey; }
        }

        /// <inheritdoc/>
        public GeckoStatusBlock Struct { get; }

        /// <inheritdoc/>
        public LogStructDefinition LogStruct { get; }

        /// <inheritdoc/>
        public ConfigStructDefinition ConfigStruct { get; }

        /// <inheritdoc/>
        public string PlatformKey { get; }

        /// <summary>
        /// Build a spa from a 1024-byte status block and the pack identity that goes
        /// with it. Returns null if the definitions are not available.
        /// </summary>
        /// <param name="registry">The pack definitions.</param>
        /// <param name="block">The 1024 status block bytes.</param>
        /// <param name="platformKey">Platform, e.g. "inXE".</param>
        /// <param name="configVersion">Config structure version.</param>
        /// <param name="logVersion">Log structure version.</param>
        /// <param name="name">A name for the spa.</param>
        public static GeckoOfflineSpa Create(
            PackRegistry registry,
            byte[] block,
            string platformKey,
            int configVersion,
            int logVersion,
            string name = "Offline Spa")
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (block == null) throw new ArgumentNullException(nameof(block));
            if (block.Length != GeckoStatusBlock.BLOCK_LENGTH)
            {
                throw new ArgumentException(
                    "Expected " + GeckoStatusBlock.BLOCK_LENGTH + " bytes, got " + block.Length, nameof(block));
            }

            ConfigStructDefinition config;
            LogStructDefinition log;
            if (!registry.TryGetConfig(platformKey, configVersion, out config) ||
                !registry.TryGetLog(platformKey, logVersion, out log))
            {
                return null;
            }

            var structure = new GeckoStatusBlock();

            // An inMix accessory declares itself in the block, so honour it here too.
            IList<AccessorDefinition> inMixConfig = null;
            IList<AccessorDefinition> inMixLog = null;
            if (block[GeckoKeys.InMix.PACK_TYPE_POSITION] == GeckoKeys.InMix.PACK_TYPE)
            {
                ConfigStructDefinition accessoryConfig;
                LogStructDefinition accessoryLog;
                if (registry.TryGetConfig(
                        GeckoKeys.InMix.PLATFORM_KEY, block[GeckoKeys.InMix.CONFIG_LIB_POSITION], out accessoryConfig))
                {
                    inMixConfig = accessoryConfig.Accessors;
                }

                if (registry.TryGetLog(
                        GeckoKeys.InMix.PLATFORM_KEY, block[GeckoKeys.InMix.STATUS_LIB_POSITION], out accessoryLog))
                {
                    inMixLog = accessoryLog.Accessors;
                }
            }

            structure.BuildAccessors(config.Accessors, log.Accessors, inMixConfig, inMixLog);
            structure.ReplaceSegment(0, block);

            return new GeckoOfflineSpa(name, platformKey, structure, config, log);
        }

        /// <summary>Builds the device layer over this spa.</summary>
        public GeckoSpaFacade CreateFacade()
        {
            return new GeckoSpaFacade(this);
        }

        /// <inheritdoc/>
        public Task<bool> PressKeyAsync(int keyCode, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        /// <inheritdoc/>
        public Task<EWatercareMode?> GetWatercareModeAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult((EWatercareMode?)null);
        }

        /// <inheritdoc/>
        public Task<bool> SetWatercareModeAsync(EWatercareMode mode, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        /// <inheritdoc/>
        public Task<IList<Reminder>> GetRemindersAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult((IList<Reminder>)null);
        }

        public Task<bool> SetRemindersAsync(IList<Reminder> reminders, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }
    }
}
