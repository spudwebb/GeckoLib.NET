using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Devices;
using GeckoLib.NET.Models;
using GeckoLib.NET.Packs;
using GeckoLib.NET.Protocol;
using GeckoLib.NET.Protocol.Messages;
using GeckoLib.NET.Struct;
using GeckoLib.NET.Transport;

namespace GeckoLib.NET
{
    /// <summary>
    /// A live connection to one spa: the handshake, the status block, and the loops that
    /// keep both current.
    ///
    /// Ported from geckolib's async_spa.py. Reconnect and backoff are deliberately NOT
    /// here - this reports that it has failed and the owner decides what to do, which is
    /// how the other plugins in this tree are built.
    /// </summary>
    public sealed class GeckoSpaConnection : IDisposable, IStatusBlockWriter, IGeckoSpa
    {
        private readonly GeckoSpaDescriptor _descriptor;
        private readonly string _clientId;
        private readonly PackRegistry _registry;

        // A marginal spa dribbles the block out and stalls, so allow several passes.
        private const int STATUS_BLOCK_MAX_ATTEMPTS = 3;

        private GeckoUdpTransport _transport;
        private CancellationTokenSource _cts;
        private Task _pingLoop;
        private Task _resyncLoop;

        private DateTime _lastPingAt = DateTime.MinValue;
        private DateTime _lastDataAt = DateTime.MinValue;
        private DateTime _lastPushAt = DateTime.MinValue;
        private bool _logDatagrams;
        private int _rfErrorCount;
        private int _staleResyncCount;

        /// <param name="descriptor">The spa, as discovery reported it.</param>
        /// <param name="clientId">
        /// Our identifier. The spa keys its push subscription on this, so it should be
        /// stable for a given installation - typically "IOS" plus a fixed UUID.
        /// </param>
        /// <param name="registry">The pack definitions.</param>
        public GeckoSpaConnection(GeckoSpaDescriptor descriptor, string clientId, PackRegistry registry)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (clientId == null) throw new ArgumentNullException(nameof(clientId));
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            _descriptor = descriptor;
            _clientId = clientId;
            _registry = registry;
            Struct = new GeckoStatusBlock { Writer = this };
        }

        /// <summary>Optional log sink.</summary>
        public Action<string> Log { get; set; }

        /// <summary>
        /// Log every datagram sent and received. Diagnostic only - very noisy.
        ///
        /// Can be turned on and off while connected, which is the point: the traffic
        /// worth capturing is usually the traffic happening when someone notices a
        /// problem, not the traffic after a reconnect.
        /// </summary>
        public bool LogDatagrams
        {
            get { return _logDatagrams; }
            set
            {
                _logDatagrams = value;

                GeckoUdpTransport transport = _transport;
                if (transport != null) transport.LogDatagrams = value;
            }
        }

        /// <summary>The connection state changed.</summary>
        public event EventHandler<SpaStateChangedEventArgs> StateChanged;

        /// <summary>A field of the pack structure changed, whether pushed or written.</summary>
        public event EventHandler<AccessorChangedEventArgs> AccessorChanged;

        /// <summary>Where the connection has got to.</summary>
        public ESpaState State { get; private set; } = ESpaState.Idle;

        /// <summary>The spa's pack structure and its accessors.</summary>
        public GeckoStatusBlock Struct { get; }

        /// <summary>The spa as discovery reported it.</summary>
        public GeckoSpaDescriptor Descriptor { get { return _descriptor; } }

        /// <summary>Spa name.</summary>
        public string Name { get { return _descriptor.Name; } }

        /// <summary>The spa's own identifier, used to build device ids.</summary>
        public string UniqueId { get { return _descriptor.Identifier; } }

        /// <summary>The platform key the spa reported, e.g. "inXE".</summary>
        public string PlatformKey { get { return PackInfo == null ? null : PackInfo.PlatformKey; } }

        /// <summary>
        /// The spa's devices. Null until the handshake has loaded the pack, because
        /// which devices exist depends on what the pack says is wired up.
        /// </summary>
        public GeckoSpaFacade Facade { get; private set; }

        /// <summary>Firmware versions, once the handshake has read them.</summary>
        public SpaVersions Versions { get; private set; }

        /// <summary>Radio channel and signal strength, once read.</summary>
        public ChannelInfo Channel { get; private set; }

        /// <summary>Which pack and structure versions the spa reported.</summary>
        public PackFileInfo PackInfo { get; private set; }

        /// <summary>The pack definition in use.</summary>
        public PackDefinition Pack { get; private set; }

        /// <summary>The log structure definition in use.</summary>
        public LogStructDefinition LogStruct { get; private set; }

        /// <summary>The config structure definition in use.</summary>
        public ConfigStructDefinition ConfigStruct { get; private set; }

        /// <summary>Pack firmware version, formatted the way geckolib reports it.</summary>
        public string PackVersion { get; private set; }

        /// <summary>Low-level configuration number.</summary>
        public int? ConfigNumber { get; private set; }

        /// <summary>Is the handshake complete and the spa answering?</summary>
        public bool IsConnected
        {
            get { return State == ESpaState.Connected; }
        }

        /// <summary>How many RF errors the spa has reported on this connection.</summary>
        public int RfErrorCount { get { return _rfErrorCount; } }

        /// <summary>When the spa last sent us anything.</summary>
        public DateTime LastDataAt { get { return _lastDataAt; } }

        /// <summary>
        /// Run the connection handshake. Returns true once the pack structure is loaded
        /// and the spa is ready to use.
        /// </summary>
        public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_transport != null) throw new InvalidOperationException("Already connected");

            SetState(ESpaState.Connecting, null);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = _cts.Token;

            _transport = new GeckoUdpTransport(_descriptor.EndPoint, _clientId, _descriptor.Identifier)
            {
                Log = WriteLog,
                LogDatagrams = _logDatagrams
            };
            _transport.StatusChanged += OnStatusPushed;
            _transport.RfError += OnRfError;
            _transport.Open();

            try
            {
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    handshake.CancelAfter(GeckoTimings.CONNECTION_TIMEOUT);
                    if (!await HandshakeAsync(handshake.Token).ConfigureAwait(false))
                    {
                        return false;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                SetState(ESpaState.ErrorNeedsAttention, "Handshake timed out");
                return false;
            }

            _lastPingAt = DateTime.UtcNow;
            _lastDataAt = DateTime.UtcNow;
            SetState(ESpaState.Connected, null);

            _pingLoop = Task.Run(() => PingLoopAsync(token));
            _resyncLoop = Task.Run(() => ResyncLoopAsync(token));
            return true;
        }

        private async Task<bool> HandshakeAsync(CancellationToken token)
        {
            // 1. Firmware versions.
            byte[] response = await _transport.ExchangeAsync(
                seq => VersionMessage.BuildRequest(seq), Verbs.SVERS, null, token).ConfigureAwait(false);
            SpaVersions versions;
            if (response == null || !VersionMessage.TryParseResponse(response, out versions))
            {
                SetState(ESpaState.ErrorNeedsAttention, "No response to version request");
                return false;
            }

            Versions = versions;
            WriteLog("Versions " + versions);

            // 2. Radio channel.
            response = await _transport.ExchangeAsync(
                seq => ChannelMessage.BuildRequest(seq), Verbs.CHCUR, null, token).ConfigureAwait(false);
            ChannelInfo channel;
            if (response == null || !ChannelMessage.TryParseResponse(response, out channel))
            {
                SetState(ESpaState.ErrorNeedsAttention, "No response to channel request");
                return false;
            }

            Channel = channel;
            WriteLog("Radio " + channel);

            // 3. Which pack definitions to load.
            response = await _transport.ExchangeAsync(
                seq => ConfigFileMessage.BuildRequest(seq),
                Verbs.FILES,
                TimeSpan.FromTicks(GeckoTimings.PROTOCOL_TIMEOUT.Ticks * 3),
                token).ConfigureAwait(false);

            PackFileInfo packInfo;
            if (response == null || !ConfigFileMessage.TryParseResponse(response, out packInfo))
            {
                SetState(ESpaState.ErrorNeedsAttention, "No response to config file request");
                return false;
            }

            PackInfo = packInfo;
            WriteLog("Pack " + packInfo);

            // 4. The whole status block.
            if (!await ReadStatusBlockAsync(0, GeckoStatusBlock.BLOCK_LENGTH, token).ConfigureAwait(false))
            {
                SetState(ESpaState.ErrorNeedsAttention, "Could not read the status block");
                return false;
            }

            _lastDataAt = DateTime.UtcNow;

            // 5. Pack definitions.
            if (!LoadPackDefinitions(packInfo)) return false;

            // 6. Accessories, then the accessor table.
            IList<AccessorDefinition> inMixConfig;
            IList<AccessorDefinition> inMixLog;
            DetectAccessories(out inMixConfig, out inMixLog);

            Struct.AccessorChanged -= OnAccessorChanged;
            Struct.AccessorChanged += OnAccessorChanged;
            Struct.BuildAccessors(ConfigStruct.Accessors, LogStruct.Accessors, inMixConfig, inMixLog);

            ReadPackIdentity();

            Facade = new GeckoSpaFacade(this);
            WriteLog(string.Format(
                "Facade built: {0} pump(s), {1} blower(s), {2} light(s), {3} sensor(s)",
                Facade.Pumps.Count, Facade.Blowers.Count, Facade.Lights.Count,
                Facade.Sensors.Count + Facade.BinarySensors.Count));

            return true;
        }

        private bool LoadPackDefinitions(PackFileInfo packInfo)
        {
            PackDefinition pack;
            if (!_registry.TryGetPack(packInfo.PlatformKey, out pack))
            {
                SetState(ESpaState.ErrorNotSupported, "No definitions for pack " + packInfo.PlatformKey);
                return false;
            }

            ConfigStructDefinition config;
            if (!_registry.TryGetConfig(packInfo.PlatformKey, packInfo.ConfigVersion, out config))
            {
                SetState(
                    ESpaState.ErrorNotSupported,
                    string.Format("No config version {0} for pack {1}", packInfo.ConfigVersion, packInfo.PlatformKey));
                return false;
            }

            LogStructDefinition log;
            if (!_registry.TryGetLog(packInfo.PlatformKey, packInfo.LogVersion, out log))
            {
                SetState(
                    ESpaState.ErrorNotSupported,
                    string.Format("No log version {0} for pack {1}", packInfo.LogVersion, packInfo.PlatformKey));
                return false;
            }

            Pack = pack;
            ConfigStruct = config;
            LogStruct = log;
            return true;
        }

        /// <summary>
        /// Look for an inMix lighting accessory. This has to peek at fixed offsets
        /// because it runs before the accessor table exists.
        /// </summary>
        private void DetectAccessories(
            out IList<AccessorDefinition> inMixConfig,
            out IList<AccessorDefinition> inMixLog)
        {
            inMixConfig = null;
            inMixLog = null;

            byte[] block = Struct.Bytes;
            if (block[GeckoKeys.InMix.PACK_TYPE_POSITION] != GeckoKeys.InMix.PACK_TYPE)
            {
                return;
            }

            int configVersion = block[GeckoKeys.InMix.CONFIG_LIB_POSITION];
            int logVersion = block[GeckoKeys.InMix.STATUS_LIB_POSITION];

            ConfigStructDefinition config;
            if (_registry.TryGetConfig(GeckoKeys.InMix.PLATFORM_KEY, configVersion, out config))
            {
                inMixConfig = config.Accessors;
            }
            else
            {
                WriteLog("inMix detected but no config version " + configVersion);
            }

            LogStructDefinition log;
            if (_registry.TryGetLog(GeckoKeys.InMix.PLATFORM_KEY, logVersion, out log))
            {
                inMixLog = log.Accessors;
            }
            else
            {
                WriteLog("inMix detected but no log version " + logVersion);
            }

            WriteLog(string.Format("inMix accessory config {0} log {1}", configVersion, logVersion));
        }

        private void ReadPackIdentity()
        {
            GeckoStructAccessor packType = Struct[GeckoKeys.PACK_TYPE];
            if (packType != null) PackVersion = Convert.ToString(packType.Value, CultureInfo.InvariantCulture);

            // Newer packs report a Core identity; older ones only have Conf.
            if (Struct[GeckoKeys.PACK_CORE_ID] != null)
            {
                PackVersion = string.Format(
                    "{0} v{1}.{2}",
                    Struct[GeckoKeys.PACK_CORE_ID].Value,
                    Struct[GeckoKeys.PACK_CORE_REV].Value,
                    Struct[GeckoKeys.PACK_CORE_REL].Value);
            }
            else if (Struct[GeckoKeys.PACK_CONFIG_ID] != null)
            {
                PackVersion = string.Format(
                    "{0} v{1}.{2}",
                    Struct[GeckoKeys.PACK_CONFIG_ID].Value,
                    Struct[GeckoKeys.PACK_CONFIG_REV].Value,
                    Struct[GeckoKeys.PACK_CONFIG_REL].Value);
            }

            GeckoStructAccessor configNumber = Struct[GeckoKeys.CONFIG_NUMBER];
            if (configNumber != null) ConfigNumber = Convert.ToInt32(configNumber.Value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Read a range of the status block.
        ///
        /// The spa answers one request with a run of segments, accumulated while their
        /// sequence numbers stay in step (geckolib async_spastruct.py:99-161).
        ///
        /// Unlike geckolib, a stalled transfer is not thrown away. A spa with a marginal
        /// RF link to its controller routinely sends the first N segments and then stops,
        /// and re-requesting the whole block just repeats the same partial run forever.
        /// Keeping what arrived and asking for the rest turns that into progress.
        /// </summary>
        public async Task<bool> ReadStatusBlockAsync(
            int start, int length, CancellationToken cancellationToken = default(CancellationToken))
        {
            var block = new byte[length];
            int have = 0;
            int attempts = 0;

            while (have < length && attempts < STATUS_BLOCK_MAX_ATTEMPTS)
            {
                attempts++;
                int got = await ReadRangeAsync(start + have, length - have, block, have, cancellationToken)
                    .ConfigureAwait(false);

                if (got > 0)
                {
                    have += got;
                    continue;
                }

                WriteLog(string.Format(
                    "Status block stalled at {0} of {1} bytes (attempt {2})", have, length, attempts));
            }

            if (have < length)
            {
                WriteLog(string.Format("Only read {0} of {1} status block bytes", have, length));
                return false;
            }

            Struct.ReplaceSegment(start, block);
            _lastDataAt = DateTime.UtcNow;
            return true;
        }

        /// <summary>
        /// One STATU exchange. Returns how many contiguous bytes arrived, which may be
        /// fewer than asked for if the spa stopped part-way.
        /// </summary>
        private async Task<int> ReadRangeAsync(
            int start, int length, byte[] into, int offset, CancellationToken cancellationToken)
        {
            var segments = new List<byte[]>();
            int nextExpected = 0;
            int total = 0;

            await _transport.ExchangeStreamAsync(
                seq => StatusBlockMessage.BuildRequest(seq, start, length),
                Verbs.STATV,
                payload =>
                {
                    StatusBlockSegment segment;
                    if (!StatusBlockMessage.TryParseResponse(payload, out segment)) return false;

                    if (segment.Sequence == nextExpected)
                    {
                        segments.Add(segment.Data);
                        total += segment.Data.Length;
                        nextExpected = segment.Next;
                        return segment.IsLast;
                    }

                    // Out of sequence. Everything after the gap is unusable, so stop
                    // collecting and keep the contiguous run we already have.
                    return true;
                },
                TimeSpan.FromTicks(GeckoTimings.PROTOCOL_TIMEOUT.Ticks * 3),
                cancellationToken,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            int copied = 0;
            foreach (byte[] segment in segments)
            {
                int room = Math.Min(segment.Length, into.Length - offset - copied);
                if (room <= 0) break;
                Buffer.BlockCopy(segment, 0, into, offset + copied, room);
                copied += room;
            }

            return copied;
        }

        /// <summary>Press a keypad button. See <see cref="GeckoKeys.Keypad"/>.</summary>
        public async Task<bool> PressKeyAsync(
            int keyCode, CancellationToken cancellationToken = default(CancellationToken))
        {
            RequirePackType();

            byte[] response = await _transport.ExchangeAsync(
                _ => PackCommandMessage.BuildKeyPress(
                    _transport.NextCommandSequence(), Pack.PlatformType, keyCode),
                Verbs.PACKS,
                TimeSpan.FromTicks(GeckoTimings.PROTOCOL_TIMEOUT.Ticks * 3),
                cancellationToken).ConfigureAwait(false);

            return response != null;
        }

        /// <summary>Read the water care mode. It is not part of the status block.</summary>
        public async Task<EWatercareMode?> GetWatercareModeAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            byte[] response = await _transport.ExchangeAsync(
                seq => WatercareMessage.BuildGetRequest(seq), Verbs.WCGET, null, cancellationToken)
                .ConfigureAwait(false);

            EWatercareMode mode;
            if (response == null || !WatercareMessage.TryParseMode(response, out mode)) return null;
            return mode;
        }

        /// <summary>Change the water care mode.</summary>
        public async Task<bool> SetWatercareModeAsync(
            EWatercareMode mode, CancellationToken cancellationToken = default(CancellationToken))
        {
            byte[] response = await _transport.ExchangeAsync(
                seq => WatercareMessage.BuildSetRequest(seq, mode), Verbs.WCSET, null, cancellationToken)
                .ConfigureAwait(false);

            return response != null;
        }

        /// <summary>Read the maintenance reminders.</summary>
        public async Task<IList<Reminder>> GetRemindersAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            byte[] response = await _transport.ExchangeAsync(
                seq => ReminderMessage.BuildRequest(seq), Verbs.RMREQ, null, cancellationToken)
                .ConfigureAwait(false);

            IList<Reminder> reminders;
            if (response == null || !ReminderMessage.TryParseResponse(response, out reminders)) return null;
            return reminders;
        }

        /// <summary>
        /// Write the maintenance reminders.
        /// </summary>
        /// <remarks>
        /// SETRM carries the whole set of ten slots, so a caller changing one reminder
        /// has to send back the others unchanged or the spa will forget them.
        /// </remarks>
        public async Task<bool> SetRemindersAsync(
            IList<Reminder> reminders, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (reminders == null) throw new ArgumentNullException(nameof(reminders));

            byte[] response = await _transport.ExchangeAsync(
                seq => ReminderMessage.BuildSetRequest(seq, reminders), Verbs.RMSET, null, cancellationToken)
                .ConfigureAwait(false);

            return response != null;
        }

        /// <inheritdoc/>
        async Task<bool> IStatusBlockWriter.WriteAsync(
            int position, int length, int value, CancellationToken cancellationToken)
        {
            RequirePackType();

            byte[] data = length == 2
                ? new[] { (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF) }
                : new[] { (byte)(value & 0xFF) };

            byte[] response = await _transport.ExchangeAsync(
                _ => PackCommandMessage.BuildSetValue(
                    _transport.NextCommandSequence(),
                    Pack.PlatformType,
                    PackInfo.ConfigVersion,
                    PackInfo.LogVersion,
                    position,
                    data),
                Verbs.PACKS,
                TimeSpan.FromTicks(GeckoTimings.PROTOCOL_TIMEOUT.Ticks * 3),
                cancellationToken).ConfigureAwait(false);

            if (response == null)
            {
                WriteLog(string.Format("Spa did not ack the write at {0}", position));
                return false;
            }

            // Only now is the local block allowed to move.
            Struct.ReplaceSegment(position, data);
            return true;
        }

        private void RequirePackType()
        {
            if (Pack == null || PackInfo == null)
            {
                throw new InvalidOperationException("Not connected; the pack is not loaded");
            }
        }

        private async Task PingLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    byte[] response = await _transport.ExchangeAsync(
                        _ => PingMessage.BuildRequest(), Verbs.APING, null, token).ConfigureAwait(false);

                    if (response != null)
                    {
                        _lastPingAt = DateTime.UtcNow;
                        _lastDataAt = DateTime.UtcNow;
                    }
                    else if (DateTime.UtcNow - _lastPingAt > GeckoTimings.Current.PingDeviceNotRespondingTimeout)
                    {
                        SetState(ESpaState.ErrorPingMissed, "Spa stopped answering pings");
                        return;
                    }

                    await GeckoTimings.SleepAsync(GeckoTimings.PING_FREQUENCY, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception error)
                {
                    WriteLog("Ping loop error: " + error.Message);
                }
            }
        }

        /// <summary>
        /// Re-read the log range periodically. This both confirms the push subscription
        /// is alive and re-arms it: if the block moved while no push arrived, the spa has
        /// silently dropped us (geckolib async_spa.py:678-732).
        /// </summary>
        private async Task ResyncLoopAsync(CancellationToken token)
        {
            // An absolute deadline, so an early wake-up from a profile change cannot keep
            // pushing the resync further out.
            DateTime due = DateTime.UtcNow + GeckoTimings.SPA_STATUS_RESYNC_FREQUENCY;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    TimeSpan wait = due - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero)
                    {
                        await GeckoTimings.SleepAsync(wait, token).ConfigureAwait(false);
                        continue;
                    }

                    due = DateTime.UtcNow + GeckoTimings.SPA_STATUS_RESYNC_FREQUENCY;
                    await ResyncAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception error)
                {
                    WriteLog("Resync loop error: " + error.Message);
                }
            }
        }

        /// <summary>
        /// Re-read the log range and report whether the push subscription looks healthy.
        /// </summary>
        public async Task<bool> ResyncAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (LogStruct == null) return false;

            DateTime lastPushBefore = _lastPushAt;
            byte[] before = Struct.Snapshot();

            int begin = LogStruct.Begin;
            int length = LogStruct.End - LogStruct.Begin;
            if (!await ReadStatusBlockAsync(begin, length, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            bool changed = false;
            for (int i = begin; i < begin + length; i++)
            {
                if (before[i] != Struct.Bytes[i]) { changed = true; break; }
            }

            if (changed && _lastPushAt == lastPushBefore)
            {
                // The spa's state moved but it never told us, so we are not subscribed.
                _staleResyncCount++;
                WriteLog("Resync found changes with no push - subscription may be stale (" +
                         _staleResyncCount + ")");

                if (_staleResyncCount >= GeckoTimings.STALE_RESYNCS_BEFORE_RECONNECT)
                {
                    SetState(ESpaState.ErrorNeedsAttention, "Push subscription is stale");
                    return false;
                }
            }
            else
            {
                _staleResyncCount = 0;
            }

            return true;
        }

        private void OnStatusPushed(object sender, StatusChangedEventArgs e)
        {
            _lastPushAt = DateTime.UtcNow;
            _lastDataAt = DateTime.UtcNow;
            _staleResyncCount = 0;

            foreach (StatusChange change in e.Changes)
            {
                try
                {
                    Struct.ReplaceSegment(change.Position, change.Data);
                }
                catch (ArgumentOutOfRangeException)
                {
                    WriteLog("Ignoring out-of-range push " + change);
                }
            }
        }

        private void OnRfError(object sender, EventArgs e)
        {
            _rfErrorCount++;
            if (_rfErrorCount >= GeckoTimings.MAX_RF_ERRORS_BEFORE_HALT)
            {
                SetState(ESpaState.ErrorRfFault, "Too many RF errors");
            }
        }

        private void OnAccessorChanged(object sender, AccessorChangedEventArgs e)
        {
            EventHandler<AccessorChangedEventArgs> handler = AccessorChanged;
            if (handler != null) handler(this, e);
        }

        private void SetState(ESpaState state, string reason)
        {
            if (State == state) return;

            ESpaState previous = State;
            State = state;
            WriteLog("State " + previous + " -> " + state + (reason == null ? string.Empty : " (" + reason + ")"));

            EventHandler<SpaStateChangedEventArgs> handler = StateChanged;
            if (handler != null) handler(this, new SpaStateChangedEventArgs(previous, state, reason));
        }

        /// <summary>Close the connection.</summary>
        public async Task DisconnectAsync()
        {
            CancellationTokenSource cts = _cts;
            GeckoUdpTransport transport = _transport;

            _cts = null;
            _transport = null;

            if (cts != null)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }

            if (transport != null)
            {
                transport.StatusChanged -= OnStatusPushed;
                transport.RfError -= OnRfError;
                await transport.CloseAsync().ConfigureAwait(false);
                transport.Dispose();
            }

            await WhenSettled(_pingLoop).ConfigureAwait(false);
            await WhenSettled(_resyncLoop).ConfigureAwait(false);
            _pingLoop = null;
            _resyncLoop = null;

            if (cts != null) cts.Dispose();
            SetState(ESpaState.Idle, null);
        }

        private static async Task WhenSettled(Task task)
        {
            if (task == null) return;
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Loops end by cancellation; nothing here is actionable.
            }
        }

        private void WriteLog(string message)
        {
            Action<string> log = Log;
            if (log == null) return;
            try { log(message); } catch { /* a broken sink must not break the connection */ }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            try
            {
                DisconnectAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Disposal is best effort.
            }
        }
    }
}
