namespace BlueTusk.Protocol;

/// <summary>Guards the high-level lifecycle of a PostgreSQL protocol connection.</summary>
public sealed class BlueTuskProtocolStateMachine
{
    private static readonly Dictionary<BlueTuskConnectionState, BlueTuskConnectionState[]> AllowedTransitions =
        new Dictionary<BlueTuskConnectionState, BlueTuskConnectionState[]>
        {
            [BlueTuskConnectionState.Disconnected] = [BlueTuskConnectionState.TransportConnected],
            [BlueTuskConnectionState.TransportConnected] =
                [BlueTuskConnectionState.EncryptionNegotiation, BlueTuskConnectionState.Startup, BlueTuskConnectionState.Closing],
            [BlueTuskConnectionState.EncryptionNegotiation] =
                [BlueTuskConnectionState.Startup, BlueTuskConnectionState.Closing],
            [BlueTuskConnectionState.Startup] =
                [BlueTuskConnectionState.Authentication, BlueTuskConnectionState.Closing],
            [BlueTuskConnectionState.Authentication] =
                [BlueTuskConnectionState.Initialising, BlueTuskConnectionState.Closing],
            [BlueTuskConnectionState.Initialising] =
                [BlueTuskConnectionState.Ready, BlueTuskConnectionState.Closing],
            [BlueTuskConnectionState.Ready] =
                [
                    BlueTuskConnectionState.Executing,
                    BlueTuskConnectionState.CopyIn,
                    BlueTuskConnectionState.CopyOut,
                    BlueTuskConnectionState.CopyBoth,
                    BlueTuskConnectionState.Replication,
                    BlueTuskConnectionState.Resetting,
                    BlueTuskConnectionState.Closing,
                ],
            [BlueTuskConnectionState.Executing] =
                [
                    BlueTuskConnectionState.Ready,
                    BlueTuskConnectionState.CopyIn,
                    BlueTuskConnectionState.CopyOut,
                    BlueTuskConnectionState.CopyBoth,
                    BlueTuskConnectionState.FailedTransaction,
                    BlueTuskConnectionState.Cancelling,
                    BlueTuskConnectionState.Closing,
                ],
            [BlueTuskConnectionState.CopyIn] =
                [BlueTuskConnectionState.Ready, BlueTuskConnectionState.FailedTransaction, BlueTuskConnectionState.Cancelling, BlueTuskConnectionState.Closing],
            [BlueTuskConnectionState.CopyOut] =
                [BlueTuskConnectionState.Ready, BlueTuskConnectionState.FailedTransaction, BlueTuskConnectionState.Cancelling, BlueTuskConnectionState.Closing],
            [BlueTuskConnectionState.CopyBoth] =
                [BlueTuskConnectionState.Ready, BlueTuskConnectionState.Cancelling, BlueTuskConnectionState.Closing],
            [BlueTuskConnectionState.Replication] =
                [BlueTuskConnectionState.Ready, BlueTuskConnectionState.Cancelling, BlueTuskConnectionState.Closing],
            [BlueTuskConnectionState.FailedTransaction] =
                [BlueTuskConnectionState.Executing, BlueTuskConnectionState.Resetting, BlueTuskConnectionState.Ready, BlueTuskConnectionState.Closing],
            [BlueTuskConnectionState.Cancelling] =
                [BlueTuskConnectionState.Ready, BlueTuskConnectionState.FailedTransaction, BlueTuskConnectionState.Closing],
            [BlueTuskConnectionState.Resetting] =
                [BlueTuskConnectionState.Ready, BlueTuskConnectionState.Closing],
            [BlueTuskConnectionState.Closing] = [BlueTuskConnectionState.Disconnected],
        };

    private readonly object _sync = new();
    private BlueTuskConnectionState _state = BlueTuskConnectionState.Disconnected;

    public BlueTuskConnectionState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public void TransitionTo(BlueTuskConnectionState next)
    {
        lock (_sync)
        {
            if (!AllowedTransitions.TryGetValue(_state, out var allowed) || !allowed.Contains(next))
            {
                throw new InvalidOperationException($"Protocol state cannot transition from {_state} to {next}.");
            }

            _state = next;
        }
    }

    public bool TryTransition(
        BlueTuskConnectionState expected,
        BlueTuskConnectionState next)
    {
        lock (_sync)
        {
            if (_state != expected)
            {
                return false;
            }

            if (!AllowedTransitions.TryGetValue(_state, out var allowed) || !allowed.Contains(next))
            {
                throw new InvalidOperationException($"Protocol state cannot transition from {_state} to {next}.");
            }

            _state = next;
            return true;
        }
    }
}
