using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlueTusk.FraudInvestigation.Infrastructure.Migrations;

/// <inheritdoc />
public partial class FraudRulesAndEvidence : Migration
{
    private static readonly string[] AlertRuleTenantNameColumns = ["TenantId", "Name"];
    private static readonly string[] EvidenceAuditColumns = ["TenantId", "CaseId", "RecordedAt"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "alert_rules",
            schema: "fraud",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                MinimumAmount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_alert_rules", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "evidence_audit",
            schema: "fraud",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("BlueTusk:IdentityGeneration", 0),
                TenantId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                Operation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Detail = table.Column<string>(type: "text", nullable: false),
                RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_evidence_audit", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_alert_rules_TenantId_Name",
            schema: "fraud",
            table: "alert_rules",
            columns: AlertRuleTenantNameColumns,
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_evidence_audit_TenantId_CaseId_RecordedAt",
            schema: "fraud",
            table: "evidence_audit",
            columns: EvidenceAuditColumns);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "alert_rules",
            schema: "fraud");

        migrationBuilder.DropTable(
            name: "evidence_audit",
            schema: "fraud");
    }
}
