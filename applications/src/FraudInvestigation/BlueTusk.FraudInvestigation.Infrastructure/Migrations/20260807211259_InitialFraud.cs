using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Generated EF migration uses inline index column arrays.

namespace BlueTusk.FraudInvestigation.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialFraud : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "fraud");

        migrationBuilder.CreateTable(
            name: "accounts",
            schema: "fraud",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_accounts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "investigation_cases",
            schema: "fraud",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Assignee = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                Decision = table.Column<int>(type: "integer", nullable: false),
                DecisionNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false),
                OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_investigation_cases", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "transfers",
            schema: "fraud",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                DestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                Amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_transfers", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_transfers_TenantId_RecordedAt",
            schema: "fraud",
            table: "transfers",
            columns: new[] { "TenantId", "RecordedAt" });

        migrationBuilder.CreatePropertyGraph(
            "fraud_graph",
            graph => graph
                .Vertex(
                    "accounts",
                    "accounts",
                    "fraud",
                    vertex => vertex
                        .HasKey("Id")
                        .HasLabel(
                            "account",
                            label => label.Property("Id").Property("DisplayName")))
                .Edge(
                    "transfers",
                    "transfers",
                    "fraud",
                    edge => edge
                        .HasKey("Id")
                        .HasLabel(
                            "transfer",
                            label => label
                                .Property("Id")
                                .Property("Amount")
                                .Property("Currency"))
                        .HasSource("accounts", ["SourceId"], ["Id"])
                        .HasDestination("accounts", ["DestinationId"], ["Id"])),
            "fraud");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropPropertyGraph("fraud_graph", "fraud");

        migrationBuilder.DropTable(
            name: "accounts",
            schema: "fraud");

        migrationBuilder.DropTable(
            name: "investigation_cases",
            schema: "fraud");

        migrationBuilder.DropTable(
            name: "transfers",
            schema: "fraud");
    }
}
