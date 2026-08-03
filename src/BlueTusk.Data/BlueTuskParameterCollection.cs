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
public sealed class BlueTuskParameterCollection : DbParameterCollection
{
    private readonly List<BlueTuskParameter> _items = [];
    private int _version;

    public override int Count => _items.Count;

    public override object SyncRoot => ((ICollection)_items).SyncRoot;

    internal IReadOnlyList<BlueTuskParameter> Items => _items;

    internal int Version => _version;

    public BlueTuskParameter Add(BlueTuskParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        _items.Add(parameter);
        _version++;
        return parameter;
    }

    public override int Add(object value)
    {
        var parameter = RequireParameter(value);
        _items.Add(parameter);
        _version++;
        return _items.Count - 1;
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
        if (_items.Count != 0)
        {
            _items.Clear();
            _version++;
        }
    }

    public override bool Contains(object value) => value is BlueTuskParameter parameter && _items.Contains(parameter);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(object value) => value is BlueTuskParameter parameter ? _items.IndexOf(parameter) : -1;

    public override int IndexOf(string parameterName) =>
        _items.FindIndex(parameter => string.Equals(parameter.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));

    public override void Insert(int index, object value)
    {
        _items.Insert(index, RequireParameter(value));
        _version++;
    }

    public override void Remove(object value)
    {
        if (value is BlueTuskParameter parameter)
        {
            if (_items.Remove(parameter))
            {
                _version++;
            }
        }
    }

    public override void RemoveAt(int index)
    {
        _items.RemoveAt(index);
        _version++;
    }

    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index >= 0)
        {
            _items.RemoveAt(index);
            _version++;
        }
    }

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName)
    {
        var index = IndexOf(parameterName);
        return index >= 0
            ? _items[index]
            : throw new IndexOutOfRangeException($"Parameter '{parameterName}' was not found.");
    }

    protected override void SetParameter(int index, DbParameter value)
    {
        _items[index] = RequireParameter(value);
        _version++;
    }

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
        {
            throw new IndexOutOfRangeException($"Parameter '{parameterName}' was not found.");
        }

        _items[index] = RequireParameter(value);
        _version++;
    }

    private static BlueTuskParameter RequireParameter(object value) =>
        value as BlueTuskParameter
        ?? throw new ArgumentException("Only BlueTuskParameter instances can be added.", nameof(value));
}
