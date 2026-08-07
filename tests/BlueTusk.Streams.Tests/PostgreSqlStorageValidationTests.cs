using System.Data.Common;
using BlueTusk.Streams.Storage.PostgreSql;

namespace BlueTusk.Streams.Tests;

public sealed class PostgreSqlStorageValidationTests
{
    [Fact]
    public void Relay_rejects_a_publication_containing_its_control_schema()
    {
        var options = new PostgreSqlStreamsStorageOptions
        {
            ControlDataSource = new UnusableDataSource(),
        };

        var exception = Assert.Throws<PostgreSqlRelayPublicationException>(
            () => PostgreSqlRelayPublicationValidator.Validate(
                options,
                [new PostgreSqlPublishedTable("bluetusk_streams", "stream_state")]));

        Assert.Contains("separate control data source", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Relay_accepts_publications_outside_its_control_schema()
    {
        var options = new PostgreSqlStreamsStorageOptions
        {
            ControlDataSource = new UnusableDataSource(),
        };

        PostgreSqlRelayPublicationValidator.Validate(
            options,
            [new PostgreSqlPublishedTable("public", "orders")]);
    }

    private sealed class UnusableDataSource : DbDataSource
    {
        public override string ConnectionString => string.Empty;

        protected override DbConnection CreateDbConnection() =>
            throw new NotSupportedException();
    }
}
