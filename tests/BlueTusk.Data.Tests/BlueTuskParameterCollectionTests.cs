namespace BlueTusk.Data.Tests;

public sealed class BlueTuskParameterCollectionTests
{
    [Fact]
    public void Maintains_typed_parameters_by_name_and_ordinal()
    {
        var parameters = new BlueTuskParameterCollection();
        var parameter = new BlueTuskParameter<int>(42) { ParameterName = "answer" };

        parameters.Add(parameter);

        Assert.Same(parameter, parameters[0]);
        Assert.Same(parameter, parameters["ANSWER"]);
        Assert.Equal(42, parameter.TypedValue);
    }

    [Fact]
    public void Maintains_order_across_collection_mutations()
    {
        var first = new BlueTuskParameter<int>(1) { ParameterName = "first" };
        var second = new BlueTuskParameter<int>(2) { ParameterName = "second" };
        var inserted = new BlueTuskParameter<int>(3) { ParameterName = "inserted" };
        var replacement = new BlueTuskParameter<int>(4) { ParameterName = "replacement" };
        var parameters = new BlueTuskParameterCollection();

        parameters.Add(first);
        parameters.Add(second);
        parameters.Insert(0, inserted);
        parameters[1] = replacement;

        Assert.Equal([inserted, replacement, second], parameters.Cast<BlueTuskParameter>());
        Assert.Equal(1, parameters.IndexOf("replacement"));

        parameters.RemoveAt(0);
        parameters.Remove(second);
        var copied = new BlueTuskParameter[1];
        parameters.CopyTo(copied, 0);

        Assert.Single(parameters);
        Assert.Same(replacement, copied[0]);

        parameters.Clear();
        Assert.Empty(parameters.Cast<BlueTuskParameter>());
    }

    [Fact]
    public void Rejects_negative_command_timeouts()
    {
        using var command = new BlueTuskCommand();

        Assert.Throws<ArgumentOutOfRangeException>(() => command.CommandTimeout = -1);
        command.CommandTimeout = 0;
        Assert.Equal(0, command.CommandTimeout);
    }

    [Fact]
    public void Validates_the_sequential_portal_fetch_size()
    {
        using var command = new BlueTuskCommand();

        Assert.Equal(0, command.SequentialFetchSize);
        Assert.Throws<ArgumentOutOfRangeException>(() => command.SequentialFetchSize = -1);
        command.SequentialFetchSize = 128;
        Assert.Equal(128, command.SequentialFetchSize);
    }
}
