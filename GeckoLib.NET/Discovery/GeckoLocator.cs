using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Models;
using GeckoLib.NET.Protocol;

namespace GeckoLib.NET.Discovery
{
    /// <summary>
    /// Finds in.touch2 spa modules on the local network by UDP broadcast.
    /// Mirrors geckolib's async_locator.py.
    /// </summary>
    public sealed class GeckoLocator : IDisposable
    {
        // Windows raises WSAECONNRESET on a UDP socket when a previous send drew an ICMP
        // "port unreachable" back. Broadcasting to a whole subnet makes that likely, and
        // it would otherwise tear down the receive loop. This turns the behaviour off.
        private const int SIO_UDP_CONNRESET = -1744830452;

        private bool _disposed;

        /// <summary>
        /// Optional log sink. The library deliberately takes a delegate rather than
        /// depending on a logging framework.
        /// </summary>
        public Action<string> Log { get; set; }

        /// <summary>
        /// Broadcast for spas and collect the replies.
        ///
        /// Returns once a spa has answered and <see cref="GeckoConstants.DISCOVERY_INITIAL_TIMEOUT"/>
        /// has elapsed, or immediately on the first match when <paramref name="staticIp"/>
        /// is given, or empty-handed after <see cref="GeckoConstants.DISCOVERY_TIMEOUT"/>.
        /// </summary>
        /// <param name="staticIp">
        /// Send to this address instead of broadcasting. Use when the spa is on another
        /// subnet, or when broadcast traffic is filtered.
        /// </param>
        /// <param name="cancellationToken">Cancels the discovery run.</param>
        public async Task<IReadOnlyList<GeckoSpaDescriptor>> DiscoverAsync(
            string staticIp = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GeckoLocator));

            IPAddress targetAddress;
            if (string.IsNullOrEmpty(staticIp))
            {
                targetAddress = IPAddress.Parse(GeckoConstants.BROADCAST_ADDRESS);
            }
            else if (!IPAddress.TryParse(staticIp, out targetAddress))
            {
                throw new ArgumentException("Not a valid IP address: " + staticIp, nameof(staticIp));
            }

            var target = new IPEndPoint(targetAddress, GeckoConstants.INTOUCH2_PORT);
            var found = new List<GeckoSpaDescriptor>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(GeckoConstants.DISCOVERY_TIMEOUT);

                // Bind explicitly. An unbound socket cannot start an overlapped receive on
                // Windows - the first read fails with WSAEINVAL (WinError 10022).
                using (var client = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
                {
                    client.EnableBroadcast = true;
                    TryDisableConnReset(client);

                    byte[] request = HelloMessage.BuildBroadcastRequest();
                    var started = Stopwatch.StartNew();
                    TimeSpan nextBroadcast = TimeSpan.Zero;
                    Task<UdpReceiveResult> pending = null;

                    try
                    {
                        while (!timeout.IsCancellationRequested)
                        {
                            if (started.Elapsed >= nextBroadcast)
                            {
                                await client.SendAsync(request, request.Length, target).ConfigureAwait(false);
                                nextBroadcast = started.Elapsed + GeckoConstants.BROADCAST_INTERVAL;
                            }

                            if (pending == null)
                            {
                                pending = client.ReceiveAsync();
                            }

                            TimeSpan wait = nextBroadcast - started.Elapsed;
                            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;

                            Task completed = await Task
                                .WhenAny(pending, Task.Delay(wait, timeout.Token))
                                .ConfigureAwait(false);

                            if (completed == pending)
                            {
                                UdpReceiveResult result = await pending.ConfigureAwait(false);
                                pending = null;
                                Handle(result, found, seen);
                            }

                            if (found.Count > 0 &&
                                (!string.IsNullOrEmpty(staticIp) ||
                                 started.Elapsed >= GeckoConstants.DISCOVERY_INITIAL_TIMEOUT))
                            {
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Ran out of time, or the caller cancelled. Either way, report
                        // whatever answered before that happened.
                    }
                    finally
                    {
                        // Closing the socket faults any receive still in flight; observe it
                        // so it does not surface as an unobserved task exception.
                        if (pending != null)
                        {
                            _ = pending.ContinueWith(
                                t => { var _ = t.Exception; },
                                TaskContinuationOptions.OnlyOnFaulted |
                                TaskContinuationOptions.ExecuteSynchronously);
                        }
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return found;
        }

        private void Handle(UdpReceiveResult result, List<GeckoSpaDescriptor> found, HashSet<string> seen)
        {
            HelloMessage message;
            if (!HelloMessage.TryParse(result.Buffer, out message))
            {
                // A spa with a live push subscription keeps sending <PACKT>-wrapped status
                // updates to whichever client subscribed. Those land here during discovery
                // and are perfectly valid - just addressed to somebody else.
                WriteLog(GeckoPacket.IsPacket(result.Buffer)
                    ? "Ignoring in-session traffic from " + result.RemoteEndPoint
                    : "Ignoring unrecognised datagram from " + result.RemoteEndPoint +
                      " (" + result.Buffer.Length + " bytes)");
                return;
            }

            // Our own broadcast comes straight back to us, and the phone app announces
            // itself the same way. Neither is a spa.
            if (message.Kind != EHelloKind.SpaResponse)
            {
                return;
            }

            if (!seen.Add(message.SpaIdentifier))
            {
                return;
            }

            var descriptor = new GeckoSpaDescriptor(
                message.SpaIdentifier,
                message.SpaName,
                result.RemoteEndPoint.Address,
                result.RemoteEndPoint.Port);

            found.Add(descriptor);
            WriteLog("Discovered " + descriptor);
        }

        private static void TryDisableConnReset(UdpClient client)
        {
            try
            {
                client.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (PlatformNotSupportedException)
            {
                // Not Windows - the behaviour this works around does not exist there.
            }
            catch (SocketException)
            {
                // Some stacks reject the control code. Harmless.
            }
        }

        private void WriteLog(string message)
        {
            Action<string> log = Log;
            if (log != null)
            {
                try
                {
                    log(message);
                }
                catch
                {
                    // A broken log sink must not break discovery.
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _disposed = true;
        }
    }
}
