using BlueTusk.EntityFrameworkCore.Graphs.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity.Internal;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes.Internal;
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
            case CreateBlueTuskEnumTypeOperation createEnum:
                builder
                    .Append("migrationBuilder.CreateBlueTuskEnumType(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(createEnum.Definition)))
                    .AppendLine(");");
                break;
            case AlterBlueTuskEnumTypeOperation alterEnum:
                builder
                    .Append("migrationBuilder.AlterBlueTuskEnumType(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(alterEnum.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(alterEnum.OldDefinition)))
                    .AppendLine(");");
                break;
            case DropBlueTuskEnumTypeOperation dropEnum:
                GenerateDropType("DropBlueTuskEnumType", dropEnum.Name, dropEnum.Schema, builder);
                break;
            case CreateBlueTuskDomainTypeOperation createDomain:
                builder
                    .Append("migrationBuilder.CreateBlueTuskDomainType(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(createDomain.Definition)))
                    .AppendLine(");");
                break;
            case AlterBlueTuskDomainTypeOperation alterDomain:
                builder
                    .Append("migrationBuilder.AlterBlueTuskDomainType(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(alterDomain.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(alterDomain.OldDefinition)))
                    .AppendLine(");");
                break;
            case DropBlueTuskDomainTypeOperation dropDomain:
                GenerateDropType("DropBlueTuskDomainType", dropDomain.Name, dropDomain.Schema, builder);
                break;
            case CreateBlueTuskCompositeTypeOperation createComposite:
                builder
                    .Append("migrationBuilder.CreateBlueTuskCompositeType(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(createComposite.Definition)))
                    .AppendLine(");");
                break;
            case AlterBlueTuskCompositeTypeOperation alterComposite:
                builder
                    .Append("migrationBuilder.AlterBlueTuskCompositeType(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(alterComposite.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(alterComposite.OldDefinition)))
                    .AppendLine(");");
                break;
            case DropBlueTuskCompositeTypeOperation dropComposite:
                GenerateDropType("DropBlueTuskCompositeType", dropComposite.Name, dropComposite.Schema, builder);
                break;
            case RenameBlueTuskUserDefinedTypeOperation renameType:
                builder
                    .Append("migrationBuilder.RenameBlueTuskUserDefinedType(")
                    .Append("global::BlueTusk.EntityFrameworkCore.UserDefinedTypes.BlueTuskUserDefinedTypeKind.")
                    .Append(renameType.Kind.ToString())
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameType.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameType.NewName))
                    .Append(", ")
                    .Append(Literal(renameType.Schema))
                    .Append(", ")
                    .Append(Literal(renameType.NewSchema))
                    .AppendLine(");");
                break;
            case AddBlueTuskTableInheritanceOperation addInheritance:
                builder
                    .Append("migrationBuilder.AddBlueTuskTableInheritance(")
                    .Append(Dependencies.CSharpHelper.Literal(addInheritance.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(addInheritance.ParentTable))
                    .Append(", ")
                    .Append(Literal(addInheritance.Schema))
                    .Append(", ")
                    .Append(Literal(addInheritance.ParentSchema))
                    .AppendLine(");");
                break;
            case RemoveBlueTuskTableInheritanceOperation removeInheritance:
                builder
                    .Append("migrationBuilder.RemoveBlueTuskTableInheritance(")
                    .Append(Dependencies.CSharpHelper.Literal(removeInheritance.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(removeInheritance.ParentTable))
                    .Append(", ")
                    .Append(Literal(removeInheritance.Schema))
                    .Append(", ")
                    .Append(Literal(removeInheritance.ParentSchema))
                    .AppendLine(");");
                break;
            case CreateBlueTuskRowSecurityPolicyOperation createPolicy:
                builder
                    .Append("migrationBuilder.CreateBlueTuskRowSecurityPolicy(")
                    .Append(Dependencies.CSharpHelper.Literal(createPolicy.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskRowLevelSecurityMetadata.Serialize(createPolicy.Definition)))
                    .Append(", ")
                    .Append(Literal(createPolicy.Schema))
                    .AppendLine(");");
                break;
            case AlterBlueTuskRowSecurityPolicyOperation alterPolicy:
                builder
                    .Append("migrationBuilder.AlterBlueTuskRowSecurityPolicy(")
                    .Append(Dependencies.CSharpHelper.Literal(alterPolicy.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskRowLevelSecurityMetadata.Serialize(alterPolicy.Definition)))
                    .Append(", ")
                    .Append(Literal(alterPolicy.Schema))
                    .AppendLine(");");
                break;
            case DropBlueTuskRowSecurityPolicyOperation dropPolicy:
                builder
                    .Append("migrationBuilder.DropBlueTuskRowSecurityPolicy(")
                    .Append(Dependencies.CSharpHelper.Literal(dropPolicy.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(dropPolicy.Name))
                    .Append(", ")
                    .Append(Literal(dropPolicy.Schema))
                    .AppendLine(");");
                break;
            case RenameBlueTuskRowSecurityPolicyOperation renamePolicy:
                builder
                    .Append("migrationBuilder.RenameBlueTuskRowSecurityPolicy(")
                    .Append(Dependencies.CSharpHelper.Literal(renamePolicy.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renamePolicy.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renamePolicy.NewName))
                    .Append(", ")
                    .Append(Literal(renamePolicy.Schema))
                    .AppendLine(");");
                break;
            case AlterBlueTuskRowLevelSecurityOperation alterRowLevelSecurity:
                builder
                    .Append("migrationBuilder.AlterBlueTuskRowLevelSecurity(")
                    .Append(Dependencies.CSharpHelper.Literal(alterRowLevelSecurity.Table))
                    .Append(", ")
                    .Append(Literal(alterRowLevelSecurity.Enabled))
                    .Append(", ")
                    .Append(Literal(alterRowLevelSecurity.Forced))
                    .Append(", ")
                    .Append(Literal(alterRowLevelSecurity.Schema))
                    .AppendLine(");");
                break;
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

    private void GenerateDropType(
        string method,
        string name,
        string? schema,
        IndentedStringBuilder builder)
    {
        builder.Append("migrationBuilder.").Append(method).Append("(")
            .Append(Dependencies.CSharpHelper.Literal(name))
            .Append(", ")
            .Append(Literal(schema))
            .AppendLine(");");
    }

    private static string Literal(bool? value) =>
        value switch
        {
            true => "true",
            false => "false",
            null => "null",
        };
}

#pragma warning restore EF1001
