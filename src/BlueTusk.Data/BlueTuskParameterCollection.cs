using System.Collections;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace BlueTusk.Data;

[SuppressMessage(
    "Design",
    "CA1010:Generic interface should also be implemented",
    Justification = "DbParameterCollection defines the required non-generic ADO.NET contract.")]
[SuppressMessage(
    "Usage",
    "CA2201:Do not raise reserved exception types",
    Justification = "ADO.NET collections conventionally use IndexOutOfRangeException for missing names.")]
public sealed class BlueTuskParameterCollection :
    DbParameterCollection,
    IReadOnlyList<BlueTuskParameter>
{
    private BlueTuskParameter? _first;
    private BlueTuskParameter? _second;
    private BlueTuskParameter? _third;
    private BlueTuskParameter? _fourth;
    private List<BlueTuskParameter>? _overflow;
    private int _count;
    private int _version;

    public override int Count => _count;

    public override object SyncRoot => this;

    internal IReadOnlyList<BlueTuskParameter> Items => this;

    BlueTuskParameter IReadOnlyList<BlueTuskParameter>.this[int index] => GetAt(index);

    internal int Version => _version;

    public BlueTuskParameter Add(BlueTuskParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        AddCore(parameter);
        _version++;
        return parameter;
    }

    public override int Add(object value)
    {
        var parameter = RequireParameter(value);
        AddCore(parameter);
        _version++;
        return Count - 1;
    }

    public override void AddRange(Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values)
        {
            Add(value!);
        }
    }

    public override void Clear()
    {
        if (_count != 0)
        {
            _first = null;
            _second = null;
            _third = null;
            _fourth = null;
            _overflow?.Clear();
            _count = 0;
            _version++;
        }
    }

    public override bool Contains(object value) => IndexOf(value) >= 0;

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (array.Rank != 1 || index > array.Length - Count)
        {
            throw new ArgumentException("The destination array does not have enough one-dimensional space.", nameof(array));
        }

        for (var parameterIndex = 0; parameterIndex < Count; parameterIndex++)
        {
            array.SetValue(GetAt(parameterIndex), index + parameterIndex);
        }
    }

    public override IEnumerator GetEnumerator() =>
        ((IEnumerable<BlueTuskParameter>)this).GetEnumerator();

    IEnumerator<BlueTuskParameter> IEnumerable<BlueTuskParameter>.GetEnumerator()
    {
        var version = _version;
        for (var index = 0; index < Count; index++)
        {
            if (version != _version)
            {
                throw new InvalidOperationException(
                    "The parameter collection was modified during enumeration.");
            }

            yield return GetAt(index);
        }
    }

    public override int IndexOf(object value)
    {
        if (value is not BlueTuskParameter parameter)
        {
            return -1;
        }

        for (var index = 0; index < Count; index++)
        {
            if (ReferenceEquals(GetAt(index), parameter))
            {
                return index;
            }
        }

        return -1;
    }

    public override int IndexOf(string parameterName)
    {
        for (var index = 0; index < Count; index++)
        {
            if (string.Equals(
                    GetAt(index).ParameterName,
                    parameterName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    public override void Insert(int index, object value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)Count);
        var parameter = RequireParameter(value);
        AddCore(parameter);
        for (var moveIndex = _count - 1; moveIndex > index; moveIndex--)
        {
            SetAt(moveIndex, GetAt(moveIndex - 1));
        }

        SetAt(index, parameter);

        _version++;
    }

    public override void Remove(object value)
    {
        if (value is BlueTuskParameter parameter)
        {
            var index = IndexOf(parameter);
            if (index >= 0)
            {
                RemoveAtCore(index);
                _version++;
            }
        }
    }

    public override void RemoveAt(int index)
    {
        RemoveAtCore(index);
        _version++;
    }

    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index >= 0)
        {
            RemoveAtCore(index);
            _version++;
        }
    }

    protected override DbParameter GetParameter(int index) => GetAt(index);

    protected override DbParameter GetParameter(string parameterName)
    {
        var index = IndexOf(parameterName);
        return index >= 0
            ? GetAt(index)
            : throw new IndexOutOfRangeException($"Parameter '{parameterName}' was not found.");
    }

    protected override void SetParameter(int index, DbParameter value)
    {
        SetAt(index, RequireParameter(value));
        _version++;
    }

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
        {
            throw new IndexOutOfRangeException($"Parameter '{parameterName}' was not found.");
        }

        SetAt(index, RequireParameter(value));
        _version++;
    }

    private void AddCore(BlueTuskParameter parameter)
    {
        switch (_count)
        {
            case 0:
                _first = parameter;
                break;
            case 1:
                _second = parameter;
                break;
            case 2:
                _third = parameter;
                break;
            case 3:
                _fourth = parameter;
                break;
            default:
                (_overflow ??= []).Add(parameter);
                break;
        }

        _count++;
    }

    private BlueTuskParameter GetAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count);
        return index switch
        {
            0 => _first!,
            1 => _second!,
            2 => _third!,
            3 => _fourth!,
            _ => _overflow![index - 4],
        };
    }

    private void SetAt(int index, BlueTuskParameter parameter)
    {
        _ = GetAt(index);
        switch (index)
        {
            case 0:
                _first = parameter;
                break;
            case 1:
                _second = parameter;
                break;
            case 2:
                _third = parameter;
                break;
            case 3:
                _fourth = parameter;
                break;
            default:
                _overflow![index - 4] = parameter;
                break;
        }
    }

    private void RemoveAtCore(int index)
    {
        _ = GetAt(index);
        for (var moveIndex = index; moveIndex < _count - 1; moveIndex++)
        {
            SetAt(moveIndex, GetAt(moveIndex + 1));
        }

        switch (_count - 1)
        {
            case 0:
                _first = null;
                break;
            case 1:
                _second = null;
                break;
            case 2:
                _third = null;
                break;
            case 3:
                _fourth = null;
                break;
            default:
                _overflow!.RemoveAt(_count - 5);
                break;
        }

        _count--;
    }

    private static BlueTuskParameter RequireParameter(object value) =>
        value as BlueTuskParameter
        ?? throw new ArgumentException("Only BlueTuskParameter instances can be added.", nameof(value));
}
