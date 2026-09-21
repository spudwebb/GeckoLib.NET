using System;
using System.Threading;
using System.Threading.Tasks;

namespace GeckoLib.NET
{
    /// <summary>
    /// Timing profile for the connection, ported from geckolib's config.py.
    ///
    /// geckolib runs two profiles and switches between them: while any device on the spa
    /// is running it polls hard ("active"), and when everything is off it backs off
    /// ("idle") to avoid hammering the spa. Switching profiles wakes every pending wait
    /// so the new intervals take effect immediately.
    /// </summary>
    public sealed class GeckoTimings
    {
        /// <summary>The profile used while something on the spa is running.</summary>
        public static readonly GeckoTimings Active = new GeckoTimings(
            pingDeviceNotResponding: TimeSpan.FromSeconds(10),
            facadeUpdate: TimeSpan.FromSeconds(28800),
            spaPackRefresh: TimeSpan.FromSeconds(28800),
            isActive: true);

        /// <summary>The profile used while the spa is quiet.</summary>
        public static readonly GeckoTimings Idle = new GeckoTimings(
            pingDeviceNotResponding: TimeSpan.FromSeconds(120),
            facadeUpdate: TimeSpan.FromSeconds(3600),
            spaPackRefresh: TimeSpan.FromSeconds(3600),
            isActive: false);

        private static GeckoTimings _current = Idle;

        // Cancelled and replaced whenever the profile changes, to wake every SleepAsync.
        // Deliberately never disposed: a waiter may be holding a token from it, and
        // profile changes are rare enough that letting the GC take them is the right
        // trade against an ObjectDisposedException race.
        private static CancellationTokenSource _changed = new CancellationTokenSource();

        private GeckoTimings(
            TimeSpan pingDeviceNotResponding,
            TimeSpan facadeUpdate,
            TimeSpan spaPackRefresh,
            bool isActive)
        {
            PingDeviceNotRespondingTimeout = pingDeviceNotResponding;
            FacadeUpdateFrequency = facadeUpdate;
            SpaPackRefreshFrequency = spaPackRefresh;
            IsActive = isActive;
        }

        /// <summary>The profile currently in force.</summary>
        public static GeckoTimings Current
        {
            get { return _current; }
        }

        /// <summary>
        /// Switch profile. Any wait started through <see cref="SleepAsync"/> returns
        /// early so the new intervals apply at once.
        /// </summary>
        public static void SetActive(bool active)
        {
            GeckoTimings wanted = active ? Active : Idle;
            if (ReferenceEquals(wanted, _current))
            {
                return;
            }

            _current = wanted;
            ReleaseWaiters();
        }

        /// <summary>Wake everything currently waiting in <see cref="SleepAsync"/>.</summary>
        public static void ReleaseWaiters()
        {
            CancellationTokenSource previous =
                Interlocked.Exchange(ref _changed, new CancellationTokenSource());
            previous.Cancel();
        }

        /// <summary>
        /// Sleep, but wake early if the timing profile changes. Returns true if the whole
        /// delay elapsed, false if it was cut short by a profile change.
        /// </summary>
        public static async Task<bool> SleepAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            CancellationToken changed = _changed.Token;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(changed, cancellationToken))
            {
                try
                {
                    await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    // A genuine cancellation propagates; a profile change does not.
                    cancellationToken.ThrowIfCancellationRequested();
                    return false;
                }
            }
        }

        /// <summary>Is this the active (fast-polling) profile?</summary>
        public bool IsActive { get; }

        /// <summary>Silence for longer than this means the spa has stopped answering.</summary>
        public TimeSpan PingDeviceNotRespondingTimeout { get; }

        /// <summary>How often to refresh data that is not pushed.</summary>
        public TimeSpan FacadeUpdateFrequency { get; }

        /// <summary>How often to re-read the pack metadata.</summary>
        public TimeSpan SpaPackRefreshFrequency { get; }

        // The rest do not differ between profiles.

        /// <summary>How long to wait for a response before giving up on an exchange.</summary>
        public static readonly TimeSpan PROTOCOL_TIMEOUT = TimeSpan.FromSeconds(4);

        /// <summary>How long to wait before re-sending an unanswered request.</summary>
        public static readonly TimeSpan PAUSE_BETWEEN_RETRIES = TimeSpan.FromMilliseconds(400);

        /// <summary>How often to ping while connected.</summary>
        public static readonly TimeSpan PING_FREQUENCY = TimeSpan.FromSeconds(2);

        /// <summary>How often to re-read the log range to confirm the push subscription is alive.</summary>
        public static readonly TimeSpan SPA_STATUS_RESYNC_FREQUENCY = TimeSpan.FromSeconds(300);

        /// <summary>How long to wait for the whole connection handshake.</summary>
        public static readonly TimeSpan CONNECTION_TIMEOUT = TimeSpan.FromSeconds(45);

        /// <summary>Consecutive stale resyncs before giving up and reconnecting.</summary>
        public const int STALE_RESYNCS_BEFORE_RECONNECT = 2;

        /// <summary>RF errors tolerated before the connection is declared unusable.</summary>
        public const int MAX_RF_ERRORS_BEFORE_HALT = 50;
    }
}
