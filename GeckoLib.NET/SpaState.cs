using System;

namespace GeckoLib.NET
{
    /// <summary>
    /// Where a spa connection has got to. Values match geckolib's GeckoSpaState so logs
    /// and diagnostics line up between the two implementations.
    /// </summary>
    public enum ESpaState
    {
        Idle = 1,
        LocatingSpas = 2,
        LocatedSpas = 3,
        Connecting = 4,
        SpaReady = 5,
        Connected = 10,

        /// <summary>Discovery found nothing.</summary>
        ErrorSpaNotFound = 50,

        /// <summary>The handshake did not complete; needs a human to look.</summary>
        ErrorNeedsAttention = 51,

        /// <summary>The spa stopped answering pings.</summary>
        ErrorPingMissed = 52,

        /// <summary>The network module cannot reach the spa controller.</summary>
        ErrorRfFault = 53,

        /// <summary>No pack definitions for what this spa reports.</summary>
        ErrorNotSupported = 100
    }

    /// <summary>The state of a spa connection changed.</summary>
    public sealed class SpaStateChangedEventArgs : EventArgs
    {
        public SpaStateChangedEventArgs(ESpaState oldState, ESpaState newState, string reason)
        {
            OldState = oldState;
            NewState = newState;
            Reason = reason;
        }

        public ESpaState OldState { get; }

        public ESpaState NewState { get; }

        /// <summary>Why, when there is something useful to say. May be null.</summary>
        public string Reason { get; }

        public override string ToString()
        {
            return Reason == null
                ? string.Format("{0} -> {1}", OldState, NewState)
                : string.Format("{0} -> {1} ({2})", OldState, NewState, Reason);
        }
    }

    /// <summary>Human-readable descriptions of <see cref="ESpaState"/>.</summary>
    public static class SpaStateText
    {
        /// <summary>A short description suitable for a status display.</summary>
        public static string Describe(ESpaState state)
        {
            switch (state)
            {
                case ESpaState.Idle: return "Idle";
                case ESpaState.LocatingSpas: return "Locating spas";
                case ESpaState.LocatedSpas: return "Located spas";
                case ESpaState.Connecting: return "Connecting";
                case ESpaState.SpaReady: return "Spa ready";
                case ESpaState.Connected: return "Connected";
                case ESpaState.ErrorSpaNotFound: return "Spa not found";
                case ESpaState.ErrorNeedsAttention: return "Needs attention";
                case ESpaState.ErrorPingMissed: return "Not responding";
                case ESpaState.ErrorRfFault: return "RF fault";
                case ESpaState.ErrorNotSupported: return "Spa not supported";
                default: return "Unknown";
            }
        }
    }
}
