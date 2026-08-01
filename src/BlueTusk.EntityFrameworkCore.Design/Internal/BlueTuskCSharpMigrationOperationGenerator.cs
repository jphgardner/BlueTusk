using BlueTusk.EntityFrameworkCore.Collations.Internal;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints.Internal;
using BlueTusk.EntityFrameworkCore.Extensions.Internal;
using BlueTusk.EntityFrameworkCore.Graphs.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
using BlueTusk.EntityFrameworkCore.Routines.Internal;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity.Internal;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes.Internal;
using BlueTusk.EntityFrameworkCore.Views.Internal;
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
            case CreateBlueTuskCollationOperation createCollation:
                builder
                    .Append("migrationBuilder.CreateBlueTuskCollation(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskCollationMetadata.Serialize(createCollation.Definition)))
                    .Append(", ")
                    .Append(createCollation.IfNotExists ? "true" : "false")
                    .AppendLine(");");
                break;
            case CreateBlueTuskCollationFromOperation createCollationFrom:
                builder
                    .Append("migrationBuilder.CreateBlueTuskCollationFrom(")
                    .Append(Dependencies.CSharpHelper.Literal(createCollationFrom.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(createCollationFrom.SourceName))
                    .Append(", ")
                    .Append(Literal(createCollationFrom.Schema))
                    .Append(", ")
                    .Append(Literal(createCollationFrom.SourceSchema))
                    .Append(", ")
                    .Append(createCollationFrom.IfNotExists ? "true" : "false")
                    .AppendLine(");");
                break;
            case RenameBlueTuskCollationOperation renameCollation:
                builder
                    .Append("migrationBuilder.RenameBlueTuskCollation(")
                    .Append(Dependencies.CSharpHelper.Literal(renameCollation.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameCollation.NewName))
                    .Append(", ")
                    .Append(Literal(renameCollation.Schema))
                    .Append(", ")
                    .Append(Literal(renameCollation.NewSchema))
                    .AppendLine(");");
                break;
            case RefreshBlueTuskCollationVersionOperation refreshCollation:
                builder
                    .Append("migrationBuilder.RefreshBlueTuskCollationVersion(")
                    .Append(Dependencies.CSharpHelper.Literal(refreshCollation.Name))
                    .Append(", ")
                    .Append(Literal(refreshCollation.Schema))
                    .AppendLine(");");
                break;
            case DropBlueTuskCollationOperation dropCollation:
                builder
                    .Append("migrationBuilder.DropBlueTuskCollation(")
                    .Append(Dependencies.CSharpHelper.Literal(dropCollation.Name))
                    .Append(", ")
                    .Append(Literal(dropCollation.Schema))
                    .Append(", ")
                    .Append(dropCollation.IfExists ? "true" : "false")
                    .Append(", ")
                    .Append(dropCollation.Cascade ? "true" : "false")
                    .AppendLine(");");
                break;
            case CreateBlueTuskExtensionOperation createExtension:
                builder
                    .Append("migrationBuilder.CreateBlueTuskExtension(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskExtensionMetadata.Serialize(createExtension.Definition)))
                    .Append(", ")
                    .Append(createExtension.IfNotExists ? "true" : "false")
                    .AppendLine(");");
                break;
            case AlterBlueTuskExtensionOperation alterExtension:
                builder
                    .Append("migrationBuilder.AlterBlueTuskExtension(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskExtensionMetadata.Serialize(alterExtension.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskExtensionMetadata.Serialize(alterExtension.OldDefinition)))
                    .AppendLine(");");
                break;
            case DropBlueTuskExtensionOperation dropExtension:
                builder
                    .Append("migrationBuilder.DropBlueTuskExtension(")
                    .Append(Dependencies.CSharpHelper.Literal(dropExtension.Name))
                    .Append(", ")
                    .Append(dropExtension.IfExists ? "true" : "false")
                    .Append(", ")
                    .Append(dropExtension.Cascade ? "true" : "false")
                    .AppendLine(");");
                break;
            case CreateBlueTuskViewOperation createView:
                builder
                    .Append("migrationBuilder.CreateBlueTuskView(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskViewMetadata.Serialize(createView.Definition)))
                    .AppendLine(");");
                break;
            case ReplaceBlueTuskViewOperation replaceView:
                builder
                    .Append("migrationBuilder.ReplaceBlueTuskView(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskViewMetadata.Serialize(replaceView.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskViewMetadata.Serialize(replaceView.OldDefinition)))
                    .AppendLine(");");
                break;
            case CreateBlueTuskMaterializedViewOperation createMaterializedView:
                builder
                    .Append("migrationBuilder.CreateBlueTuskMaterializedView(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskViewMetadata.Serialize(createMaterializedView.Definition)))
                    .AppendLine(");");
                break;
            case AlterBlueTuskMaterializedViewOperation alterMaterializedView:
                builder
                    .Append("migrationBuilder.AlterBlueTuskMaterializedView(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskViewMetadata.Serialize(alterMaterializedView.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskViewMetadata.Serialize(alterMaterializedView.OldDefinition)))
                    .AppendLine(");");
                break;
            case DropBlueTuskViewOperation dropView:
                builder
                    .Append("migrationBuilder.DropBlueTuskView(")
                    .Append("global::BlueTusk.EntityFrameworkCore.Views.BlueTuskViewKind.")
                    .Append(dropView.Kind.ToString())
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(dropView.Name))
                    .Append(", ")
                    .Append(Literal(dropView.Schema))
                    .AppendLine(");");
                break;
            case RenameBlueTuskViewOperation renameView:
                builder
                    .Append("migrationBuilder.RenameBlueTuskView(")
                    .Append("global::BlueTusk.EntityFrameworkCore.Views.BlueTuskViewKind.")
                    .Append(renameView.Kind.ToString())
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameView.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameView.NewName))
                    .Append(", ")
                    .Append(Literal(renameView.Schema))
                    .Append(", ")
                    .Append(Literal(renameView.NewSchema))
                    .AppendLine(");");
                break;
            case RefreshBlueTuskMaterializedViewOperation refreshMaterializedView:
                builder
                    .Append("migrationBuilder.RefreshBlueTuskMaterializedView(")
                    .Append(Dependencies.CSharpHelper.Literal(refreshMaterializedView.Name))
                    .Append(", ")
                    .Append(Literal(refreshMaterializedView.Schema))
                    .Append(", ")
                    .Append(refreshMaterializedView.Concurrently ? "true" : "false")
                    .Append(", ")
                    .Append(refreshMaterializedView.WithData ? "true" : "false")
                    .AppendLine(");");
                break;
            case CreateBlueTuskRoutineOperation createRoutine:
                builder
                    .Append("migrationBuilder.CreateBlueTuskRoutine(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskRoutineMetadata.Serialize(createRoutine.Definition)))
                    .AppendLine(");");
                break;
            case ReplaceBlueTuskRoutineOperation replaceRoutine:
                builder
                    .Append("migrationBuilder.ReplaceBlueTuskRoutine(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskRoutineMetadata.Serialize(replaceRoutine.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskRoutineMetadata.Serialize(replaceRoutine.OldDefinition)))
                    .AppendLine(");");
                break;
            case DropBlueTuskRoutineOperation dropRoutine:
                builder
                    .Append("migrationBuilder.DropBlueTuskRoutine(")
                    .Append("global::BlueTusk.EntityFrameworkCore.Routines.BlueTuskRoutineKind.")
                    .Append(dropRoutine.Kind.ToString())
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(dropRoutine.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(dropRoutine.IdentityArgumentsSql))
                    .Append(", ")
                    .Append(Literal(dropRoutine.Schema))
                    .AppendLine(");");
                break;
            case RenameBlueTuskRoutineOperation renameRoutine:
                builder
                    .Append("migrationBuilder.RenameBlueTuskRoutine(")
                    .Append("global::BlueTusk.EntityFrameworkCore.Routines.BlueTuskRoutineKind.")
                    .Append(renameRoutine.Kind.ToString())
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameRoutine.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameRoutine.IdentityArgumentsSql))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameRoutine.NewName))
                    .Append(", ")
                    .Append(Literal(renameRoutine.Schema))
                    .Append(", ")
                    .Append(Literal(renameRoutine.NewSchema))
                    .AppendLine(");");
                break;
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
            case CreateBlueTuskRangeTypeOperation createRange:
                builder
                    .Append("migrationBuilder.CreateBlueTuskRangeType(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(createRange.Definition)))
                    .AppendLine(");");
                break;
            case DropBlueTuskRangeTypeOperation dropRange:
                GenerateDropType("DropBlueTuskRangeType", dropRange.Name, dropRange.Schema, builder);
                break;
            case RenameBlueTuskRangeTypeOperation renameRange:
                builder
                    .Append("migrationBuilder.RenameBlueTuskRangeType(")
                    .Append(Dependencies.CSharpHelper.Literal(renameRange.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameRange.NewName))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameRange.MultirangeName))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameRange.NewMultirangeName))
                    .Append(", ")
                    .Append(Literal(renameRange.Schema))
                    .Append(", ")
                    .Append(Literal(renameRange.NewSchema))
                    .Append(", ")
                    .Append(Literal(renameRange.MultirangeSchema))
                    .Append(", ")
                    .Append(Literal(renameRange.NewMultirangeSchema))
                    .AppendLine(");");
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
            case AddBlueTuskExclusionConstraintOperation addExclusionConstraint:
                builder
                    .Append("migrationBuilder.AddBlueTuskExclusionConstraint(")
                    .Append(Dependencies.CSharpHelper.Literal(addExclusionConstraint.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskExclusionConstraintMetadata.Serialize(addExclusionConstraint.Definition)))
                    .Append(", ")
                    .Append(Literal(addExclusionConstraint.Schema))
                    .AppendLine(");");
                break;
            case DropBlueTuskExclusionConstraintOperation dropExclusionConstraint:
                builder
                    .Append("migrationBuilder.DropBlueTuskExclusionConstraint(")
                    .Append(Dependencies.CSharpHelper.Literal(dropExclusionConstraint.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(dropExclusionConstraint.Name))
                    .Append(", ")
                    .Append(Literal(dropExclusionConstraint.Schema))
                    .AppendLine(");");
                break;
            case RenameBlueTuskExclusionConstraintOperation renameExclusionConstraint:
                builder
                    .Append("migrationBuilder.RenameBlueTuskExclusionConstraint(")
                    .Append(Dependencies.CSharpHelper.Literal(renameExclusionConstraint.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameExclusionConstraint.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameExclusionConstraint.NewName))
                    .Append(", ")
                    .Append(Literal(renameExclusionConstraint.Schema))
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
