namespace BlueTusk.Data.Tests;

public sealed class BlueTuskMultiplexingTests
{
    [Fact]
    public void Connection_string_and_builder_enable_bounded_multiplexing()
    {
        var connectionSettings = new BlueTuskConnectionStringBuilder(
            "Host=db.example.test;Username=worker;Multiplexing=true;Maximum Pool Size=4");
        using var fromConnectionString = BlueTuskDataSource.Create(
            connectionSettings.ConnectionString);
        using var fromBuilder = new BlueTuskDataSourceBuilder(
                "Host=db.example.test;Username=worker;Maximum Pool Size=4")
            .EnableMultiplexing(options =>
            {
                options.WorkerCount = 2;
                options.QueueCapacity = 17;
                options.MaxCommandsPerLease = 9;
                options.MaxPipelineCommands = 9;
            })
            .Build();

        Assert.True(connectionSettings.Multiplexing);
        Assert.True(fromConnectionString.IsMultiplexingEnabled);
        Assert.Equal(2, fromConnectionString.GetMultiplexingStatistics().Workers);
        Assert.True(fromBuilder.IsMultiplexingEnabled);
        Assert.Equal(2, fromBuilder.GetMultiplexingStatistics().Workers);
        Assert.False(
            BlueTuskDataSource.Create("Host=db.example.test;Username=worker")
                .GetMultiplexingStatistics()
                .Enabled);
    }

    [Fact]
    public void Multiplexing_configuration_is_fail_closed()
    {
        Assert.Throws<ArgumentException>(
            () => new BlueTuskConnectionStringBuilder(
                "Host=db.example.test;Pooling=false;Multiplexing=true").Validate());
        Assert.Throws<InvalidOperationException>(
            () => new BlueTuskDataSourceBuilder(
                    "Host=db.example.test;Username=worker;Maximum Pool Size=2")
                .EnableMultiplexing(options => options.WorkerCount = 3)
                .Build());
        Assert.Throws<InvalidOperationException>(
            () => new BlueTuskDataSourceBuilder(
                    "Host=db.example.test;Username=worker")
                .EnableMultiplexing(options => options.QueueCapacity = 0)
                .Build());
        Assert.Throws<InvalidOperationException>(
            () => new BlueTuskDataSourceBuilder(
                    "Host=db.example.test;Username=worker")
                .EnableMultiplexing(options =>
                {
                    options.MaxCommandsPerLease = 8;
                    options.MaxPipelineCommands = 9;
                })
                .Build());
    }

    [Theory]
    [InlineData("SELECT 42", true)]
    [InlineData("/* SET ignored = here */ SELECT 'set_config(false)'", true)]
    [InlineData("WITH value AS (SELECT 1) INSERT INTO app.items SELECT * FROM value", true)]
    [InlineData("SET application_name = 'worker'", false)]
    [InlineData("SELECT set_config('application_name', 'worker', false)", false)]
    [InlineData("SELECT pg_advisory_lock(42)", false)]
    [InlineData("CREATE TEMP TABLE state(value int)", false)]
    [InlineData("BEGIN; SELECT 1; COMMIT", false)]
    [InlineData("LISTEN changes", false)]
    [InlineData("COPY app.items TO STDOUT", false)]
    public void Classifier_conservatively_routes_session_state(
        string sql,
        bool expectedSessionNeutral)
    {
        Assert.Equal(
            expectedSessionNeutral,
            BlueTuskMultiplexingClassifier.IsSessionNeutral(sql));
    }
}
