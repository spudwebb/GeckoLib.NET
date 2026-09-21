using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Protocol;
using GeckoLib.NET.Protocol.Messages;

namespace GeckoLib.NET.Transport
{
    /// <summary>Raised when the spa pushes status block changes.</summary>
    public sealed class StatusChangedEventArgs : EventArgs
    {
        public StatusChangedEventArgs(IList<StatusChange> changes)
        {
            Changes = changes;
        }

        public IList<StatusChange> Changes { get; }
    }

    /// <summary>
    /// The UDP conversation with one spa: framing, sequence numbers, request/response
    /// with retries, and dispatch of the messages the spa sends unprompted.
    ///
    /// Everything here is about moving bytes. Deciding what to send, and what the
    /// answers mean, is the connection layer's job.
    /// </summary>
    public sealed class GeckoUdpTransport : IDisposable
    {
        private const int SIO_UDP_CONNRESET = -1744830452;

        private readonly IPEndPoint _destination;
        private readonly string _clientId;
        private readonly string _spaId;

        // geckolib serialises every exchange behind one lock, and so do we: responses
        // carry no request sequence number, so two exchanges in flight could not be told
        // apart (see Verbs).
        private readonly SemaphoreSlim _exchangeLock = new SemaphoreSlim(1, 1);

        private UdpClient _client;
        private CancellationTokenSource _cts;
        private Task _receiveLoop;
        private PendingExchange _pending;

        // Two independent counters: 1-191 for ordinary protocol traffic, 192-255 for
        // commands (SPACK and the STATQ ack). geckolib async_udp_protocol.py:140-150.
        private int _protocolSequence;
        private int _commandSequence = 191;

        /// <param name="destination">The spa's endpoint.</param>
        /// <param name="clientId">Our client identifier: "IOS" followed by a UUID.</param>
        /// <param name="spaId">The spa's identifier from discovery.</param>
        public GeckoUdpTransport(IPEndPoint destination, string clientId, string spaId)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (clientId == null) throw new ArgumentNullException(nameof(clientId));
            if (spaId == null) throw new ArgumentNullException(nameof(spaId));

            _destination = destination;
            _clientId = clientId;
            _spaId = spaId;
        }

        /// <summary>Optional log sink.</summary>
        public Action<string> Log { get; set; }

        /// <summary>
        /// Log every datagram sent and received. Very noisy - for diagnosing a spa that
        /// is not behaving, not for normal running.
        /// </summary>
        public bool LogDatagrams { get; set; }

        /// <summary>The spa pushed status block changes.</summary>
        public event EventHandler<StatusChangedEventArgs> StatusChanged;

        /// <summary>The EN module reported it cannot reach the CO module.</summary>
        public event EventHandler RfError;

        /// <summary>The spa reported a water care error.</summary>
        public event EventHandler WatercareError;

        /// <summary>Is the socket open?</summary>
        public bool IsOpen
        {
            get { return _client != null; }
        }

        /// <summary>Open the socket and start receiving.</summary>
        public void Open()
        {
            if (_client != null) throw new InvalidOperationException("Already open");

            // Bind explicitly - Windows cannot start an overlapped receive on an unbound
            // socket (WSAEINVAL / WinError 10022).
            _client = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            TryDisableConnReset(_client);

            // The status block arrives as a burst of ~27 datagrams; give the kernel room
            // so none are dropped while we are handling the previous one.
            try { _client.Client.ReceiveBufferSize = 64 * 1024; } catch (SocketException) { }

            _cts = new CancellationTokenSource();
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        /// <summary>Stop receiving and close the socket.</summary>
        public async Task CloseAsync()
        {
            CancellationTokenSource cts = _cts;
            UdpClient client = _client;
            Task loop = _receiveLoop;

            _cts = null;
            _client = null;
            _receiveLoop = null;

            if (cts != null)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }

            // Close the socket first: that is what unblocks a receive in flight.
            if (client != null) client.Close();

            if (loop != null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    WriteLog("Receive loop ended with " + error.GetType().Name);
                }
            }

            if (cts != null) cts.Dispose();
            FailPending(new OperationCanceledException("Transport closed"));
        }

        /// <summary>Next sequence number for ordinary protocol traffic (1-191).</summary>
        public byte NextProtocolSequence()
        {
            int next = Interlocked.Increment(ref _protocolSequence);
            if (next > 191)
            {
                Interlocked.Exchange(ref _protocolSequence, 1);
                next = 1;
            }

            return (byte)next;
        }

        /// <summary>Next sequence number for commands (192-255).</summary>
        public byte NextCommandSequence()
        {
            int next = Interlocked.Increment(ref _commandSequence);
            if (next > 255)
            {
                Interlocked.Exchange(ref _commandSequence, 192);
                next = 192;
            }

            return (byte)next;
        }

        /// <summary>Wrap a payload and send it. Fire and forget, as UDP is.</summary>
        public void Send(byte[] payload)
        {
            UdpClient client = _client;
            if (client == null)
            {
                WriteLog("Dropping send, transport is closed");
                return;
            }

            byte[] datagram = GeckoPacket.Wrap(_clientId, _spaId, payload);
            if (LogDatagrams) WriteLog("TX " + ProtocolBytes.Describe(payload));

            try
            {
                client.Send(datagram, datagram.Length, _destination);
            }
            catch (SocketException error)
            {
                WriteLog("Send failed: " + error.Message);
            }
            catch (ObjectDisposedException)
            {
                // Raced with CloseAsync.
            }
        }

        /// <summary>
        /// Send a request and wait for the matching response, re-sending while there is
        /// no answer. Returns the response payload, or null if it never came.
        /// </summary>
        /// <param name="buildRequest">Builds the request given a fresh sequence number.</param>
        /// <param name="responseVerb">Verb the answer will carry.</param>
        /// <param name="timeout">Overall budget; null uses the protocol default.</param>
        /// <param name="cancellationToken">Abandons the exchange.</param>
        public async Task<byte[]> ExchangeAsync(
            Func<byte, byte[]> buildRequest,
            string responseVerb,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            byte[] response = null;
            bool ok = await ExchangeStreamAsync(
                buildRequest,
                responseVerb,
                payload => { response = payload; return true; },
                timeout,
                cancellationToken).ConfigureAwait(false);

            return ok ? response : null;
        }

        /// <summary>
        /// Send a request and feed every matching response to <paramref name="onResponse"/>
        /// until it reports completion. Used for the status block, which answers one
        /// request with a run of STATV segments.
        /// </summary>
        /// <param name="buildRequest">Builds the request given a fresh sequence number.</param>
        /// <param name="responseVerb">Verb the answers will carry.</param>
        /// <param name="onResponse">Returns true when the exchange is complete.</param>
        /// <param name="timeout">Overall budget; null uses the protocol default.</param>
        /// <param name="cancellationToken">Abandons the exchange.</param>
        /// <param name="retryInterval">
        /// How long to wait for progress before re-sending. Exchanges that answer with a
        /// run of datagrams need this longer than the default, or a retry lands in the
        /// middle of the run and restarts it.
        /// </param>
        public async Task<bool> ExchangeStreamAsync(
            Func<byte, byte[]> buildRequest,
            string responseVerb,
            Func<byte[], bool> onResponse,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default(CancellationToken),
            TimeSpan? retryInterval = null)
        {
            if (buildRequest == null) throw new ArgumentNullException(nameof(buildRequest));
            if (onResponse == null) throw new ArgumentNullException(nameof(onResponse));

            TimeSpan budget = timeout ?? GeckoTimings.PROTOCOL_TIMEOUT;
            TimeSpan retryAfter = retryInterval ?? GeckoTimings.PAUSE_BETWEEN_RETRIES;

            await _exchangeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DateTime deadline = DateTime.UtcNow + budget;
                int attempts = 0;

                while (DateTime.UtcNow < deadline)
                {
                    attempts++;
                    cancellationToken.ThrowIfCancellationRequested();

                    var exchange = new PendingExchange(responseVerb, onResponse);
                    Interlocked.Exchange(ref _pending, exchange);

                    Send(buildRequest(NextProtocolSequence()));

                    // retryAfter is an IDLE timeout, not a fixed interval from the send.
                    // A real spa dribbles the status block out one segment every ~140ms
                    // and can take several seconds over it; resending part-way through
                    // restarts the transfer and it never finishes. geckolib gets this
                    // right by resetting its packet timeout on every segment received
                    // (async_spastruct.py:120-129), so we do the same.
                    bool timedOut = false;
                    while (!timedOut && DateTime.UtcNow < deadline)
                    {
                        Task progress = exchange.ProgressTask;

                        using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            Task finished = await Task.WhenAny(
                                exchange.Completion.Task,
                                progress,
                                Task.Delay(retryAfter, idle.Token)).ConfigureAwait(false);

                            idle.Cancel();

                            if (finished == exchange.Completion.Task)
                            {
                                return await exchange.Completion.Task.ConfigureAwait(false);
                            }

                            // A response arrived but the exchange is not finished yet, so
                            // keep waiting rather than re-sending.
                            timedOut = finished != progress;
                        }
                    }

                    // Nothing for a whole idle period. Loop to build a fresh request, so
                    // the retry carries a new sequence number exactly as geckolib does.
                    // Deliberately silent: an unreachable spa would otherwise log every
                    // few hundred milliseconds and bury everything else.
                }

                WriteLog(string.Format("No {0} after {1} attempts in {2:0.0}s",
                    responseVerb, attempts, budget.TotalSeconds));
                return false;
            }
            finally
            {
                Interlocked.Exchange(ref _pending, null);
                _exchangeLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            UdpClient client = _client;

            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await client.ReceiveAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException error)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    WriteLog("Receive failed: " + error.Message);
                    continue;
                }

                try
                {
                    Dispatch(result);
                }
                catch (Exception error)
                {
                    WriteLog("Error handling datagram: " + error);
                }
            }
        }

        private void Dispatch(UdpReceiveResult result)
        {
            string source;
            string destination;
            byte[] payload;

            if (!GeckoPacket.TryUnwrap(result.Buffer, out source, out destination, out payload))
            {
                // Discovery replies and other stray traffic land here.
                if (LogDatagrams) WriteLog("RX (not a packet) " + ProtocolBytes.Describe(result.Buffer));
                return;
            }

            // The spa also talks to other clients; ignore anything not addressed to us.
            if (!string.Equals(destination, _clientId, StringComparison.Ordinal) ||
                !string.Equals(source, _spaId, StringComparison.Ordinal))
            {
                if (LogDatagrams)
                {
                    WriteLog(string.Format(
                        "RX (not ours: src={0} dst={1}) {2}",
                        source, destination, ProtocolBytes.Describe(payload)));
                }

                return;
            }

            if (LogDatagrams) WriteLog("RX " + ProtocolBytes.Describe(payload));

            string verb = ProtocolBytes.ReadVerb(payload);
            if (verb == null) return;

            switch (verb)
            {
                case Verbs.STATP:
                    HandlePush(payload);
                    return;

                case Verbs.RFERR:
                    Raise(RfError);
                    return;

                case Verbs.WCERR:
                    Raise(WatercareError);
                    return;
            }

            PendingExchange exchange = _pending;
            if (exchange != null && string.Equals(verb, exchange.ResponseVerb, StringComparison.Ordinal))
            {
                exchange.Deliver(payload);
                return;
            }

            WriteLog("Unhandled " + verb);
        }

        private void HandlePush(byte[] payload)
        {
            // Ack first, before doing anything with the contents: the spa drops the
            // subscription if the acks stop. Note this uses the command counter.
            Send(PartialStatusMessage.BuildAck(NextCommandSequence()));

            IList<StatusChange> changes;
            if (!PartialStatusMessage.TryParsePush(payload, out changes))
            {
                WriteLog("Malformed STATP push");
                return;
            }

            EventHandler<StatusChangedEventArgs> handler = StatusChanged;
            if (handler != null)
            {
                handler(this, new StatusChangedEventArgs(changes));
            }
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void FailPending(Exception error)
        {
            PendingExchange exchange = Interlocked.Exchange(ref _pending, null);
            if (exchange != null) exchange.Fail(error);
        }

        private static void TryDisableConnReset(UdpClient client)
        {
            try
            {
                client.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (PlatformNotSupportedException) { }
            catch (SocketException) { }
        }

        private void WriteLog(string message)
        {
            Action<string> log = Log;
            if (log == null) return;
            try { log(message); } catch { /* a broken sink must not break the protocol */ }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            try
            {
                CloseAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Disposal is best effort.
            }

            _exchangeLock.Dispose();
        }

        /// <summary>One in-flight request and the responses it is collecting.</summary>
        private sealed class PendingExchange
        {
            private readonly Func<byte[], bool> _onResponse;

            private TaskCompletionSource<bool> _progress =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public PendingExchange(string responseVerb, Func<byte[], bool> onResponse)
            {
                ResponseVerb = responseVerb;
                _onResponse = onResponse;
                Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public string ResponseVerb { get; }

            public TaskCompletionSource<bool> Completion { get; }

            /// <summary>
            /// Completes when the next response arrives. Read it BEFORE awaiting: the
            /// replacement is installed before the old one completes, so a response that
            /// lands in between still wakes the waiter.
            /// </summary>
            public Task ProgressTask
            {
                get { return _progress.Task; }
            }

            public void Deliver(byte[] payload)
            {
                try
                {
                    bool complete = _onResponse(payload);

                    TaskCompletionSource<bool> previous = Interlocked.Exchange(
                        ref _progress,
                        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                    previous.TrySetResult(true);

                    if (complete) Completion.TrySetResult(true);
                }
                catch (Exception error)
                {
                    Completion.TrySetException(error);
                }
            }

            public void Fail(Exception error)
            {
                Completion.TrySetException(error);
            }
        }
    }
}
