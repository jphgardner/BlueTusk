using Microsoft.EntityFrameworkCore.Migrations;

namespace BlueTusk.ServiceTopology.Infrastructure.Migrations;

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
                    EXECUTE 'DROP PROPERTY GRAPH IF EXISTS "topology"."service_topology_graph"';
                END IF;
            END;
            $portable_graph$;
            """);
        migrationBuilder.CreateIndex("IX_dependencies_SourceId", "dependencies", "SourceId", "topology");
        migrationBuilder.CreateIndex("IX_dependencies_DestinationId", "dependencies", "DestinationId", "topology");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_dependencies_DestinationId", "dependencies", "topology");
        migrationBuilder.DropIndex("IX_dependencies_SourceId", "dependencies", "topology");
    }
}
