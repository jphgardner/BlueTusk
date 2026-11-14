#if NET5_0
using System.Threading.Channels;

namespace BlueTusk.Compatibility;

// Coalesce ticks while the current heartbeat is in flight, as PeriodicTimer does.
internal sealed class PeriodicTimer : IDisposable
{
    private readonly Channel<bool> _ticks = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });
    private readonly Timer _timer;

    public PeriodicTimer(TimeSpan interval)
    {
        _timer = new Timer(static state => ((ChannelWriter<bool>)state!).TryWrite(true),
            _ticks.Writer, interval, interval);
    }

    public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken) =>
        await _ticks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) &&
        _ticks.Reader.TryRead(out _);

    public void Dispose()
    {
        _timer.Dispose();
        _ticks.Writer.TryComplete();
    }
}
#endif
