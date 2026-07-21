namespace BlueTusk.Transport.Tests;

public sealed class BlueTuskEndpointTests
{
    [Fact]
    public void Tcp_rejects_invalid_port()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new BlueTuskEndpoint.Tcp("localhost", 0));

        Assert.Equal("port", exception.ParamName, ignoreCase: true);
    }

    [Fact]
    public void Tcp_rejects_blank_host()
    {
        Assert.Throws<ArgumentException>(() => new BlueTuskEndpoint.Tcp(" "));
    }

    [Fact]
    public async Task Socket_transport_requires_a_connection_before_io()
    {
        await using var transport = new BlueTuskSocketTransport();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transport.ReadAsync(new byte[1], CancellationToken.None));
    }
}
