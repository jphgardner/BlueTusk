using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Generated EF migration uses inline index column arrays.

namespace BlueTusk.ServiceTopology.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialTopology : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "topology");

        migrationBuilder.CreateTable(
            name: "dependencies",
            schema: "topology",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                DestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_dependencies", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "incidents",
            schema: "topology",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                ServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_incidents", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "services",
            schema: "topology",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Health = table.Column<int>(type: "integer", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_services", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_dependencies_TenantId_SourceId_DestinationId",
            schema: "topology",
            table: "dependencies",
            columns: new[] { "TenantId", "SourceId", "DestinationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_services_TenantId_Name",
            schema: "topology",
            table: "services",
            columns: new[] { "TenantId", "Name" },
            unique: true);

        migrationBuilder.CreatePropertyGraph(
            "service_topology_graph",
            graph => graph
                .Vertex(
                    "services",
                    "services",
                    "topology",
                    vertex => vertex
                        .HasKey("Id")
                        .HasLabel(
                            "service",
                            label => label
                                .Property("Id")
                                .Property("Name")
                                .Property("Health")))
                .Edge(
                    "dependencies",
                    "dependencies",
                    "topology",
                    edge => edge
                        .HasKey("Id")
                        .HasLabel("depends_on")
                        .HasSource("services", ["SourceId"], ["Id"])
                        .HasDestination("services", ["DestinationId"], ["Id"])),
            "topology");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropPropertyGraph("service_topology_graph", "topology");

        migrationBuilder.DropTable(
            name: "dependencies",
            schema: "topology");

        migrationBuilder.DropTable(
            name: "incidents",
            schema: "topology");

        migrationBuilder.DropTable(
            name: "services",
            schema: "topology");
    }
}
