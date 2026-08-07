using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Generated EF migration uses inline index column arrays.

namespace BlueTusk.OrderOperations.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialOrders : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "orders");

        migrationBuilder.CreateTable(
            name: "fulfilment_orders",
            schema: "orders",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                CustomerReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                State = table.Column<int>(type: "integer", nullable: false),
                AllocationReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_fulfilment_orders", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "operational_audit",
            schema: "orders",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("BlueTusk:IdentityGeneration", 0),
                TenantId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                Operation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false),
                RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RelayedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operational_audit", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_fulfilment_orders_TenantId_CustomerReference",
            schema: "orders",
            table: "fulfilment_orders",
            columns: new[] { "TenantId", "CustomerReference" });

        migrationBuilder.CreateIndex(
            name: "IX_operational_audit_TenantId_IdempotencyKey",
            schema: "orders",
            table: "operational_audit",
            columns: new[] { "TenantId", "IdempotencyKey" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "fulfilment_orders",
            schema: "orders");

        migrationBuilder.DropTable(
            name: "operational_audit",
            schema: "orders");
    }
}
