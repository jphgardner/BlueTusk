using System.Data;

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
    [InlineData("/* outer /* SET nested */ comment */ SELECT 42", true)]
    [InlineData("SELECT E'set_config(\\'ignored\\')'", true)]
    [InlineData("SELECT $tag$SET application_name = 'ignored'$tag$", true)]
    [InlineData("SELECT settings FROM app.items", true)]
    [InlineData("-- comment only", false)]
    [InlineData("SELECT 'unterminated", false)]
    [InlineData("WITH value AS (SELECT 1) INSERT INTO app.items SELECT * FROM value", true)]
    [InlineData("SET application_name = 'worker'", false)]
    [InlineData("SELECT set_config('application_name', 'worker', false)", false)]
    [InlineData("SELECT pg_advisory_lock(42)", false)]
    [InlineData("SELECT pg_advisory_xact_lock(42)", false)]
    [InlineData("SELECT current_setting('application_name')", false)]
    [InlineData("SELECT currval('app.sequence')", false)]
    [InlineData("SELECT lo_open(42, 262144)", false)]
    [InlineData("SELECT pg_temp.session_value()", false)]
    [InlineData("CREATE TEMP TABLE state(value int)", false)]
    [InlineData("SELECT 1 INTO TEMPORARY state", false)]
    [InlineData("BEGIN; SELECT 1; COMMIT", false)]
    [InlineData("ABORT", false)]
    [InlineData("SELECT 1; END TRANSACTION", false)]
    [InlineData("SELECT CASE WHEN true THEN 1 ELSE 0 END", true)]
    [InlineData("PREPARE statement AS SELECT 1", false)]
    [InlineData("EXECUTE statement", false)]
    [InlineData("DECLARE values CURSOR FOR SELECT 1", false)]
    [InlineData("SHOW application_name", false)]
    [InlineData("LISTEN changes", false)]
    [InlineData("NOTIFY changes", false)]
    [InlineData("COPY app.items TO STDOUT", false)]
    [InlineData("CALL app.update_session()", false)]
    [InlineData("DO $$ BEGIN PERFORM set_config('application_name', 'x', false); END $$", false)]
    public void Classifier_conservatively_routes_session_state(
        string sql,
        bool expectedSessionNeutral)
    {
        Assert.Equal(
            expectedSessionNeutral,
            BlueTuskMultiplexingClassifier.IsSessionNeutral(sql));
    }

    [Fact]
    public async Task Require_fails_closed_for_explicit_connections()
    {
        await using var connection = new BlueTuskConnection(
            "Host=db.example.test;Username=worker");
        await using var command = new BlueTuskCommand("SELECT 1", connection)
        {
            MultiplexingMode = BlueTuskMultiplexingMode.Require,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => command.ExecuteScalarAsync<int>());

        Assert.Contains("explicit connection", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Require_fails_closed_for_sequential_readers()
    {
        await using var dataSource = new BlueTuskDataSourceBuilder(
                "Host=db.example.test;Username=worker;Maximum Pool Size=2")
            .EnableMultiplexing(options => options.WorkerCount = 1)
            .Build();
        await using var command = dataSource.CreateCommand("SELECT 1");
        command.MultiplexingMode = BlueTuskMultiplexingMode.Require;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess));

        Assert.Contains("sequential", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scheduler_rejects_pre_canceled_and_post_disposal_commands_before_connecting()
    {
        var dataSource = new BlueTuskDataSourceBuilder(
                "Host=db.example.test;Username=worker;Maximum Pool Size=2")
            .EnableMultiplexing(options => options.WorkerCount = 1)
            .Build();
        await using var canceled = dataSource.CreateCommand("SELECT 1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceled.ExecuteScalarAsync<int>(cancellation.Token));

        await using var disposed = dataSource.CreateCommand("SELECT 1");
        await dataSource.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => disposed.ExecuteScalarAsync<int>());
    }
}
