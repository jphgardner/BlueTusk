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
    public void Rejects_negative_command_timeouts()
    {
        using var command = new BlueTuskCommand();

        Assert.Throws<ArgumentOutOfRangeException>(() => command.CommandTimeout = -1);
        command.CommandTimeout = 0;
        Assert.Equal(0, command.CommandTimeout);
    }
}
