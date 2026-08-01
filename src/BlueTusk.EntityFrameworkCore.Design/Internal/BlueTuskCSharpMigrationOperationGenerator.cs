using BlueTusk.EntityFrameworkCore.Graphs.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

#pragma warning disable EF1001 // Provider implementation requires EF Core design-time infrastructure.

namespace BlueTusk.EntityFrameworkCore.Design.Internal;

internal sealed class BlueTuskCSharpMigrationOperationGenerator(
    CSharpMigrationOperationGeneratorDependencies dependencies)
    : CSharpMigrationOperationGenerator(dependencies)
{
    protected override void Generate(
        MigrationOperation operation,
        IndentedStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        switch (operation)
        {
            case CreateBlueTuskPartitionOperation createPartition:
                builder
                    .Append("migrationBuilder.CreateBlueTuskPartition(")
                    .Append(Dependencies.CSharpHelper.Literal(createPartition.ParentName))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskPartitionMetadata.Serialize(createPartition.Definition)))
                    .Append(", ")
                    .Append(Literal(createPartition.ParentSchema))
                    .AppendLine(");");
                break;
            case DropBlueTuskPartitionOperation dropPartition:
                builder
                    .Append("migrationBuilder.DropBlueTuskPartition(")
                    .Append(Dependencies.CSharpHelper.Literal(dropPartition.Name))
                    .Append(", ")
                    .Append(Literal(dropPartition.Schema))
                    .AppendLine(");");
                break;
            case AlterBlueTuskPartitionOperation alterPartition:
                builder
                    .Append("migrationBuilder.AlterBlueTuskPartition(")
                    .Append(Dependencies.CSharpHelper.Literal(alterPartition.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(alterPartition.NewName))
                    .Append(", ")
                    .Append(Literal(alterPartition.Schema))
                    .Append(", ")
                    .Append(Literal(alterPartition.NewSchema))
                    .AppendLine(");");
                break;
            case AttachBlueTuskPartitionOperation attachPartition:
                builder
                    .Append("migrationBuilder.AttachBlueTuskPartition(")
                    .Append(Dependencies.CSharpHelper.Literal(attachPartition.ParentName))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(attachPartition.PartitionName))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskPartitionMetadata.Serialize(attachPartition.Bound)))
                    .Append(", ")
                    .Append(Literal(attachPartition.ParentSchema))
                    .Append(", ")
                    .Append(Literal(attachPartition.PartitionSchema))
                    .AppendLine(");");
                break;
            case DetachBlueTuskPartitionOperation detachPartition:
                builder
                    .Append("migrationBuilder.DetachBlueTuskPartition(")
                    .Append(Dependencies.CSharpHelper.Literal(detachPartition.ParentName))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(detachPartition.PartitionName))
                    .Append(", global::BlueTusk.EntityFrameworkCore.Migrations.Operations.BlueTuskPartitionDetachMode.")
                    .Append(detachPartition.Mode.ToString())
                    .Append(", ")
                    .Append(Literal(detachPartition.ParentSchema))
                    .Append(", ")
                    .Append(Literal(detachPartition.PartitionSchema))
                    .AppendLine(");");
                break;
            case CreateBlueTuskPropertyGraphOperation create:
                builder
                    .Append("migrationBuilder.CreateBlueTuskPropertyGraph(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskPropertyGraphMetadata.Serialize(create.Definition)))
                    .AppendLine(");");
                break;
            case DropBlueTuskPropertyGraphOperation drop:
                builder
                    .Append("migrationBuilder.DropBlueTuskPropertyGraph(")
                    .Append(Dependencies.CSharpHelper.Literal(drop.Name))
                    .Append(", ")
                    .Append(Literal(drop.Schema))
                    .AppendLine(");");
                break;
            case AlterBlueTuskPropertyGraphOperation alter:
                builder
                    .Append("migrationBuilder.AlterBlueTuskPropertyGraph(")
                    .Append(Dependencies.CSharpHelper.Literal(alter.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(alter.NewName))
                    .Append(", ")
                    .Append(Literal(alter.Schema))
                    .Append(", ")
                    .Append(Literal(alter.NewSchema))
                    .AppendLine(");");
                break;
            default:
                base.Generate(operation, builder);
                break;
        }
    }

    private string Literal(string? value) =>
        value is null ? "null" : Dependencies.CSharpHelper.Literal(value);
}

#pragma warning restore EF1001
