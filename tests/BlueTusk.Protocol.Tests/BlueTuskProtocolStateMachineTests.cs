namespace BlueTusk.Protocol.Tests;

public sealed class BlueTuskProtocolStateMachineTests
{
    [Fact]
    public void Supports_the_normal_connection_lifecycle()
    {
        var state = new BlueTuskProtocolStateMachine();

        state.TransitionTo(BlueTuskConnectionState.TransportConnected);
        state.TransitionTo(BlueTuskConnectionState.EncryptionNegotiation);
        state.TransitionTo(BlueTuskConnectionState.Startup);
        state.TransitionTo(BlueTuskConnectionState.Authentication);
        state.TransitionTo(BlueTuskConnectionState.Initialising);
        state.TransitionTo(BlueTuskConnectionState.Ready);
        state.TransitionTo(BlueTuskConnectionState.Executing);
        state.TransitionTo(BlueTuskConnectionState.Ready);

        Assert.Equal(BlueTuskConnectionState.Ready, state.State);
    }

    [Fact]
    public void Rejects_an_invalid_transition()
    {
        var state = new BlueTuskProtocolStateMachine();

        var exception = Assert.Throws<InvalidOperationException>(
            () => state.TransitionTo(BlueTuskConnectionState.Executing));

        Assert.Contains("Disconnected", exception.Message, StringComparison.Ordinal);
        Assert.Equal(BlueTuskConnectionState.Disconnected, state.State);
    }
}

