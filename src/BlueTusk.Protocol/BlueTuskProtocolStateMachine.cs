namespace BlueTusk.Protocol;

/// <summary>Guards the high-level lifecycle of a PostgreSQL protocol connection.</summary>
public sealed class BlueTuskProtocolStateMachine
{
    private static readonly uint[] AllowedTransitions =
    [
        Mask(BlueTuskConnectionState.TransportConnected),
        Mask(BlueTuskConnectionState.EncryptionNegotiation, BlueTuskConnectionState.Startup, BlueTuskConnectionState.Closing),
        Mask(BlueTuskConnectionState.Startup, BlueTuskConnectionState.Closing),
        Mask(BlueTuskConnectionState.Authentication, BlueTuskConnectionState.Closing),
        Mask(BlueTuskConnectionState.Initialising, BlueTuskConnectionState.Closing),
        Mask(BlueTuskConnectionState.Ready, BlueTuskConnectionState.Closing),
        Mask(
            BlueTuskConnectionState.Executing,
            BlueTuskConnectionState.CopyIn,
            BlueTuskConnectionState.CopyOut,
            BlueTuskConnectionState.CopyBoth,
            BlueTuskConnectionState.Replication,
            BlueTuskConnectionState.Resetting,
            BlueTuskConnectionState.Closing),
        Mask(
            BlueTuskConnectionState.Ready,
            BlueTuskConnectionState.CopyIn,
            BlueTuskConnectionState.CopyOut,
            BlueTuskConnectionState.CopyBoth,
            BlueTuskConnectionState.FailedTransaction,
            BlueTuskConnectionState.Cancelling,
            BlueTuskConnectionState.Closing),
        Mask(BlueTuskConnectionState.Ready, BlueTuskConnectionState.FailedTransaction, BlueTuskConnectionState.Cancelling, BlueTuskConnectionState.Closing),
        Mask(BlueTuskConnectionState.Ready, BlueTuskConnectionState.FailedTransaction, BlueTuskConnectionState.Cancelling, BlueTuskConnectionState.Closing),
        Mask(BlueTuskConnectionState.Ready, BlueTuskConnectionState.Cancelling, BlueTuskConnectionState.Closing),
        Mask(BlueTuskConnectionState.Ready, BlueTuskConnectionState.Cancelling, BlueTuskConnectionState.Closing),
        Mask(BlueTuskConnectionState.Executing, BlueTuskConnectionState.Resetting, BlueTuskConnectionState.Ready, BlueTuskConnectionState.Closing),
        Mask(BlueTuskConnectionState.Ready, BlueTuskConnectionState.FailedTransaction, BlueTuskConnectionState.Closing),
        Mask(BlueTuskConnectionState.Ready, BlueTuskConnectionState.Closing),
        Mask(BlueTuskConnectionState.Disconnected),
    ];

    private int _state = (int)BlueTuskConnectionState.Disconnected;

    public BlueTuskConnectionState State =>
        (BlueTuskConnectionState)Volatile.Read(ref _state);

    public void TransitionTo(BlueTuskConnectionState next)
    {
        while (true)
        {
            var current = State;
            ValidateTransition(current, next);
            if (Interlocked.CompareExchange(ref _state, (int)next, (int)current) == (int)current)
            {
                return;
            }
        }
    }

    public bool TryTransition(
        BlueTuskConnectionState expected,
        BlueTuskConnectionState next)
    {
        if (State != expected)
        {
            return false;
        }

        ValidateTransition(expected, next);
        return Interlocked.CompareExchange(ref _state, (int)next, (int)expected) == (int)expected;
    }

    private static void ValidateTransition(
        BlueTuskConnectionState current,
        BlueTuskConnectionState next)
    {
        if ((uint)current >= (uint)AllowedTransitions.Length ||
            (uint)next >= (uint)AllowedTransitions.Length ||
            (AllowedTransitions[(int)current] & (1u << (int)next)) == 0)
        {
            throw new InvalidOperationException(
                $"Protocol state cannot transition from {current} to {next}.");
        }
    }

    private static uint Mask(params BlueTuskConnectionState[] states)
    {
        var mask = 0u;
        foreach (var state in states)
        {
            mask |= 1u << (int)state;
        }

        return mask;
    }
}
