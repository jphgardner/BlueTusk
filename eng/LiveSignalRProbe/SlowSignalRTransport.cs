using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace BlueTusk.LiveSignalRProbe;

internal sealed class SlowSignalRTransport
{
    public const string QueryString = "benchmarkSlowClient";
    private WritePause? _pause;

    public async Task ApplyAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Query.ContainsKey(QueryString))
        {
            var feature = context.Features.Get<IHttpWebSocketFeature>();
            if (feature is null || !feature.IsWebSocketRequest)
            {
                throw new InvalidOperationException("The slow client must connect over WebSockets.");
            }
            context.Features.Set<IHttpWebSocketFeature>(new PausingWebSocketFeature(feature, this));
        }
        await next(context);
    }

    public WritePause BeginPause()
    {
        var pause = new WritePause(this);
        if (Interlocked.CompareExchange(ref _pause, pause, null) is not null)
        {
            throw new InvalidOperationException("A WebSocket write is already paused.");
        }
        return pause;
    }

    private Task WaitToWriteAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _pause)?.WaitToWriteAsync(cancellationToken) ?? Task.CompletedTask;

    internal sealed class WritePause(SlowSignalRTransport owner) : IDisposable
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForBlockedWriteAsync(CancellationToken cancellationToken) =>
            _blocked.Task.WaitAsync(cancellationToken);

        public Task WaitToWriteAsync(CancellationToken cancellationToken)
        {
            _blocked.TrySetResult();
            return _released.Task.WaitAsync(cancellationToken);
        }

        public void Dispose()
        {
            _ = Interlocked.CompareExchange(ref owner._pause, null, this);
            _released.TrySetResult();
        }
    }

    private sealed class PausingWebSocketFeature(IHttpWebSocketFeature inner, SlowSignalRTransport transport)
        : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => inner.IsWebSocketRequest;

        public async Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) =>
            new PausingWebSocket(await inner.AcceptAsync(context), transport);
    }

    private sealed class PausingWebSocket(WebSocket inner, SlowSignalRTransport transport) : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
        public override string? CloseStatusDescription => inner.CloseStatusDescription;
        public override WebSocketState State => inner.State;
        public override string? SubProtocol => inner.SubProtocol;

        public override void Abort() => inner.Abort();
        public override void Dispose() => inner.Dispose();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            inner.ReceiveAsync(buffer, cancellationToken);

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReceiveAsync(buffer, cancellationToken);

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
        {
            await transport.WaitToWriteAsync(cancellationToken);
            await inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
        }

        public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken = default)
        {
            await transport.WaitToWriteAsync(cancellationToken);
            await inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
        }

        public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType,
            WebSocketMessageFlags messageFlags, CancellationToken cancellationToken = default)
        {
            await transport.WaitToWriteAsync(cancellationToken);
            await inner.SendAsync(buffer, messageType, messageFlags, cancellationToken);
        }
    }
}
