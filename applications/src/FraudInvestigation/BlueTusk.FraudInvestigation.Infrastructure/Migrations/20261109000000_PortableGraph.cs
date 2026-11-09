using Microsoft.EntityFrameworkCore.Migrations;

namespace BlueTusk.FraudInvestigation.Infrastructure.Migrations;

/// <inheritdoc />
public partial class PortableGraph : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DO $portable_graph$
            BEGIN
                IF pg_catalog.to_regclass('information_schema.property_graphs') IS NOT NULL THEN
                    EXECUTE 'DROP PROPERTY GRAPH IF EXISTS "fraud"."fraud_graph"';
                END IF;
            END;
            $portable_graph$;
            """);
        migrationBuilder.CreateIndex("IX_transfers_SourceId", "transfers", "SourceId", "fraud");
        migrationBuilder.CreateIndex("IX_transfers_DestinationId", "transfers", "DestinationId", "fraud");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_transfers_DestinationId", "transfers", "fraud");
        migrationBuilder.DropIndex("IX_transfers_SourceId", "transfers", "fraud");
    }
}
