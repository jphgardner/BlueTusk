using System.Collections;
using System.Collections.Immutable;

namespace BlueTusk.Live;

// Persistent chunks keep the row payload compact. Updating one row copies at
// most 256 rows and a logarithmic path of chunk references, independent of the
// complete result size. Public snapshots expose only the read-only list.
internal sealed class LiveSnapshotRows<T> : IReadOnlyList<T>
{
    private const int ChunkSize = 256;
    private readonly ImmutableList<T[]> _chunks;

    private LiveSnapshotRows(ImmutableList<T[]> chunks, int count)
    {
        _chunks = chunks;
        Count = count;
    }

    public int Count { get; }

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return _chunks[index / ChunkSize][index % ChunkSize];
        }
    }

    internal static Builder CreateBuilder(int count) => new(count);

    internal Builder ToBuilder() => new(this);

    public IEnumerator<T> GetEnumerator()
    {
        foreach (var chunk in _chunks)
        {
            foreach (var row in chunk)
            {
                yield return row;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal sealed class Builder
    {
        private readonly ImmutableList<T[]>.Builder _chunks;
        private readonly HashSet<int>? _copiedChunks;
        private readonly int _count;
        private bool _completed;

        internal Builder(LiveSnapshotRows<T> previous)
        {
            _chunks = previous._chunks.ToBuilder();
            _copiedChunks = [];
            _count = previous.Count;
        }

        internal Builder(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            _chunks = ImmutableList.CreateBuilder<T[]>();
            _count = count;
            for (var remaining = count; remaining > 0;)
            {
                var length = Math.Min(ChunkSize, remaining);
                _chunks.Add(new T[length]);
                remaining -= length;
            }
        }

        public T this[int index]
        {
            set
            {
                if (_completed)
                {
                    throw new InvalidOperationException("The snapshot builder has already completed.");
                }
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
                var chunkIndex = index / ChunkSize;
                if (_copiedChunks is not null && _copiedChunks.Add(chunkIndex))
                {
                    _chunks[chunkIndex] = (T[])_chunks[chunkIndex].Clone();
                }
                _chunks[chunkIndex][index % ChunkSize] = value;
            }
        }

        internal LiveSnapshotRows<T> ToImmutable()
        {
            _completed = true;
            return new LiveSnapshotRows<T>(_chunks.ToImmutable(), _count);
        }
    }
}
