using BlueTusk.EntityFrameworkCore.Collations.Internal;
using BlueTusk.EntityFrameworkCore.EventTriggers.Internal;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints.Internal;
using BlueTusk.EntityFrameworkCore.Extensions.Internal;
using BlueTusk.EntityFrameworkCore.ForeignData.Internal;
using BlueTusk.EntityFrameworkCore.Graphs.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
using BlueTusk.EntityFrameworkCore.Publications.Internal;
using BlueTusk.EntityFrameworkCore.Routines.Internal;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity.Internal;
using BlueTusk.EntityFrameworkCore.Rules.Internal;
using BlueTusk.EntityFrameworkCore.SchemaPrograms.Internal;
using BlueTusk.EntityFrameworkCore.Subscriptions.Internal;
using BlueTusk.EntityFrameworkCore.Tablespaces.Internal;
using BlueTusk.EntityFrameworkCore.Triggers.Internal;
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
            case CreateBlueTuskTablespaceOperation createTablespace:
                builder
                    .Append("migrationBuilder.CreateBlueTuskTablespace(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskTablespaceMetadata.Serialize(createTablespace.Definition)))
                    .AppendLine(");");
                break;
            case AlterBlueTuskTablespaceOperation alterTablespace:
                builder
                    .Append("migrationBuilder.AlterBlueTuskTablespace(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskTablespaceMetadata.Serialize(alterTablespace.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskTablespaceMetadata.Serialize(alterTablespace.OldDefinition)))
                    .AppendLine(");");
                break;
            case RenameBlueTuskTablespaceOperation renameTablespace:
                builder
                    .Append("migrationBuilder.RenameBlueTuskTablespace(")
                    .Append(Dependencies.CSharpHelper.Literal(renameTablespace.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameTablespace.NewName))
                    .AppendLine(");");
                break;
            case DropBlueTuskTablespaceOperation dropTablespace:
                builder
                    .Append("migrationBuilder.DropBlueTuskTablespace(")
                    .Append(Dependencies.CSharpHelper.Literal(dropTablespace.Name))
                    .Append(", ")
                    .Append(dropTablespace.IfExists ? "true" : "false")
                    .AppendLine(");");
                break;
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
            case CreateBlueTuskTriggerOperation createTrigger:
                builder
                    .Append("migrationBuilder.CreateBlueTuskTrigger(")
                    .Append(Dependencies.CSharpHelper.Literal(createTrigger.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskTriggerMetadata.Serialize(createTrigger.Definition)))
                    .Append(", ")
                    .Append(Literal(createTrigger.Schema))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(createTrigger.OrReplace))
                    .AppendLine(");");
                break;
            case DropBlueTuskTriggerOperation dropTrigger:
                builder
                    .Append("migrationBuilder.DropBlueTuskTrigger(")
                    .Append(Dependencies.CSharpHelper.Literal(dropTrigger.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(dropTrigger.Name))
                    .Append(", ")
                    .Append(Literal(dropTrigger.Schema))
                    .AppendLine(");");
                break;
            case RenameBlueTuskTriggerOperation renameTrigger:
                builder
                    .Append("migrationBuilder.RenameBlueTuskTrigger(")
                    .Append(Dependencies.CSharpHelper.Literal(renameTrigger.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameTrigger.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameTrigger.NewName))
                    .Append(", ")
                    .Append(Literal(renameTrigger.Schema))
                    .AppendLine(");");
                break;
            case AlterBlueTuskTriggerEnabledModeOperation alterTriggerMode:
                builder
                    .Append("migrationBuilder.AlterBlueTuskTriggerEnabledMode(")
                    .Append(Dependencies.CSharpHelper.Literal(alterTriggerMode.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(alterTriggerMode.Name))
                    .Append(", BlueTusk.EntityFrameworkCore.Triggers.BlueTuskTriggerEnabledMode.")
                    .Append(alterTriggerMode.EnabledMode.ToString())
                    .Append(", ")
                    .Append(Literal(alterTriggerMode.Schema))
                    .AppendLine(");");
                break;
            case CreateBlueTuskEventTriggerOperation createEventTrigger:
                builder.Append("migrationBuilder.CreateBlueTuskEventTrigger(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskEventTriggerMetadata.Serialize(createEventTrigger.Definition)))
                    .AppendLine(");");
                break;
            case DropBlueTuskEventTriggerOperation dropEventTrigger:
                builder.Append("migrationBuilder.DropBlueTuskEventTrigger(")
                    .Append(Dependencies.CSharpHelper.Literal(dropEventTrigger.Name))
                    .AppendLine(");");
                break;
            case RenameBlueTuskEventTriggerOperation renameEventTrigger:
                builder.Append("migrationBuilder.RenameBlueTuskEventTrigger(")
                    .Append(Dependencies.CSharpHelper.Literal(renameEventTrigger.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameEventTrigger.NewName))
                    .AppendLine(");");
                break;
            case AlterBlueTuskEventTriggerEnabledModeOperation alterEventTriggerMode:
                builder.Append("migrationBuilder.AlterBlueTuskEventTriggerEnabledMode(")
                    .Append(Dependencies.CSharpHelper.Literal(alterEventTriggerMode.Name))
                    .Append(", global::BlueTusk.EntityFrameworkCore.EventTriggers.BlueTuskEventTriggerEnabledMode.")
                    .Append(alterEventTriggerMode.EnabledMode.ToString())
                    .AppendLine(");");
                break;
            case CreateBlueTuskRuleOperation createRule:
                builder.Append("migrationBuilder.CreateBlueTuskRule(")
                    .Append(Dependencies.CSharpHelper.Literal(createRule.Table)).Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(BlueTuskRuleMetadata.Serialize(createRule.Definition)))
                    .Append(", ").Append(Literal(createRule.Schema)).Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(createRule.OrReplace)).AppendLine(");");
                break;
            case DropBlueTuskRuleOperation dropRule:
                builder.Append("migrationBuilder.DropBlueTuskRule(")
                    .Append(Dependencies.CSharpHelper.Literal(dropRule.Table)).Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(dropRule.Name)).Append(", ")
                    .Append(Literal(dropRule.Schema)).AppendLine(");");
                break;
            case RenameBlueTuskRuleOperation renameRule:
                builder.Append("migrationBuilder.RenameBlueTuskRule(")
                    .Append(Dependencies.CSharpHelper.Literal(renameRule.Table)).Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameRule.Name)).Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameRule.NewName)).Append(", ")
                    .Append(Literal(renameRule.Schema)).AppendLine(");");
                break;
            case AlterBlueTuskRuleEnabledModeOperation alterRuleMode:
                builder.Append("migrationBuilder.AlterBlueTuskRuleEnabledMode(")
                    .Append(Dependencies.CSharpHelper.Literal(alterRuleMode.Table)).Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(alterRuleMode.Name))
                    .Append(", BlueTusk.EntityFrameworkCore.Rules.BlueTuskRuleEnabledMode.")
                    .Append(alterRuleMode.EnabledMode.ToString()).Append(", ")
                    .Append(Literal(alterRuleMode.Schema)).AppendLine(");");
                break;
            case CreateBlueTuskPublicationOperation createPublication:
                builder.Append("migrationBuilder.CreateBlueTuskPublication(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskPublicationMetadata.Serialize(createPublication.Definition)))
                    .AppendLine(");");
                break;
            case CreateBlueTuskForeignDataWrapperOperation createWrapper:
                builder.Append("migrationBuilder.CreateBlueTuskForeignDataWrapper(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(createWrapper.Definition)))
                    .AppendLine(");");
                break;
            case AlterBlueTuskForeignDataWrapperOperation alterWrapper:
                builder.Append("migrationBuilder.AlterBlueTuskForeignDataWrapper(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(alterWrapper.OldDefinition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(alterWrapper.Definition)))
                    .AppendLine(");");
                break;
            case DropBlueTuskForeignDataWrapperOperation dropWrapper:
                builder.Append("migrationBuilder.DropBlueTuskForeignDataWrapper(")
                    .Append(Dependencies.CSharpHelper.Literal(dropWrapper.Name))
                    .AppendLine(");");
                break;
            case RenameBlueTuskForeignDataWrapperOperation renameWrapper:
                builder.Append("migrationBuilder.RenameBlueTuskForeignDataWrapper(")
                    .Append(Dependencies.CSharpHelper.Literal(renameWrapper.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameWrapper.NewName))
                    .AppendLine(");");
                break;
            case CreateBlueTuskForeignServerOperation createServer:
                builder.Append("migrationBuilder.CreateBlueTuskForeignServer(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(createServer.Definition)))
                    .AppendLine(");");
                break;
            case AlterBlueTuskForeignServerOperation alterServer:
                builder.Append("migrationBuilder.AlterBlueTuskForeignServer(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(alterServer.OldDefinition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(alterServer.Definition)))
                    .AppendLine(");");
                break;
            case DropBlueTuskForeignServerOperation dropServer:
                builder.Append("migrationBuilder.DropBlueTuskForeignServer(")
                    .Append(Dependencies.CSharpHelper.Literal(dropServer.Name))
                    .AppendLine(");");
                break;
            case RenameBlueTuskForeignServerOperation renameServer:
                builder.Append("migrationBuilder.RenameBlueTuskForeignServer(")
                    .Append(Dependencies.CSharpHelper.Literal(renameServer.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameServer.NewName))
                    .AppendLine(");");
                break;
            case CreateBlueTuskUserMappingOperation createMapping:
                builder.Append("migrationBuilder.CreateBlueTuskUserMapping(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(createMapping.Definition)))
                    .AppendLine(");");
                break;
            case AlterBlueTuskUserMappingOperation alterMapping:
                builder.Append("migrationBuilder.AlterBlueTuskUserMapping(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(alterMapping.OldDefinition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(alterMapping.Definition)))
                    .AppendLine(");");
                break;
            case DropBlueTuskUserMappingOperation dropMapping:
                builder.Append("migrationBuilder.DropBlueTuskUserMapping(")
                    .Append(Dependencies.CSharpHelper.Literal(dropMapping.ServerName))
                    .Append(", ")
                    .Append(Literal(dropMapping.UserName))
                    .AppendLine(");");
                break;
            case CreateBlueTuskOperatorOperation createOperator:
                GenerateSchemaProgram("CreateBlueTuskOperator",
                    BlueTuskSchemaProgramMetadata.Serialize(createOperator.Definition), builder);
                break;
            case ReplaceBlueTuskOperatorOperation replaceOperator:
                GenerateSchemaProgram("ReplaceBlueTuskOperator",
                    BlueTuskSchemaProgramMetadata.Serialize(replaceOperator.OldDefinition),
                    BlueTuskSchemaProgramMetadata.Serialize(replaceOperator.Definition), builder);
                break;
            case DropBlueTuskOperatorOperation dropOperator:
                GenerateSchemaProgram("DropBlueTuskOperator",
                    BlueTuskSchemaProgramMetadata.Serialize(dropOperator.Definition), builder);
                break;
            case CreateBlueTuskOperatorFamilyOperation createFamily:
                GenerateSchemaProgram("CreateBlueTuskOperatorFamily",
                    BlueTuskSchemaProgramMetadata.Serialize(createFamily.Definition), builder);
                break;
            case AlterBlueTuskOperatorFamilyOperation alterFamily:
                GenerateSchemaProgram("AlterBlueTuskOperatorFamily",
                    BlueTuskSchemaProgramMetadata.Serialize(alterFamily.OldDefinition),
                    BlueTuskSchemaProgramMetadata.Serialize(alterFamily.Definition), builder);
                break;
            case DropBlueTuskOperatorFamilyOperation dropFamily:
                GenerateSchemaProgram("DropBlueTuskOperatorFamily",
                    BlueTuskSchemaProgramMetadata.Serialize(dropFamily.Definition), builder);
                break;
            case CreateBlueTuskOperatorClassOperation createClass:
                GenerateSchemaProgram("CreateBlueTuskOperatorClass",
                    BlueTuskSchemaProgramMetadata.Serialize(createClass.Definition), builder);
                break;
            case ReplaceBlueTuskOperatorClassOperation replaceClass:
                GenerateSchemaProgram("ReplaceBlueTuskOperatorClass",
                    BlueTuskSchemaProgramMetadata.Serialize(replaceClass.OldDefinition),
                    BlueTuskSchemaProgramMetadata.Serialize(replaceClass.Definition), builder);
                break;
            case DropBlueTuskOperatorClassOperation dropClass:
                GenerateSchemaProgram("DropBlueTuskOperatorClass",
                    BlueTuskSchemaProgramMetadata.Serialize(dropClass.Definition), builder);
                break;
            case CreateBlueTuskCastOperation createCast:
                GenerateSchemaProgram("CreateBlueTuskCast",
                    BlueTuskSchemaProgramMetadata.Serialize(createCast.Definition), builder);
                break;
            case ReplaceBlueTuskCastOperation replaceCast:
                GenerateSchemaProgram("ReplaceBlueTuskCast",
                    BlueTuskSchemaProgramMetadata.Serialize(replaceCast.OldDefinition),
                    BlueTuskSchemaProgramMetadata.Serialize(replaceCast.Definition), builder);
                break;
            case DropBlueTuskCastOperation dropCast:
                GenerateSchemaProgram("DropBlueTuskCast",
                    BlueTuskSchemaProgramMetadata.Serialize(dropCast.Definition), builder);
                break;
            case CreateBlueTuskAggregateOperation createAggregate:
                GenerateSchemaProgram("CreateBlueTuskAggregate",
                    BlueTuskSchemaProgramMetadata.Serialize(createAggregate.Definition), builder);
                break;
            case ReplaceBlueTuskAggregateOperation replaceAggregate:
                GenerateSchemaProgram("ReplaceBlueTuskAggregate",
                    BlueTuskSchemaProgramMetadata.Serialize(replaceAggregate.OldDefinition),
                    BlueTuskSchemaProgramMetadata.Serialize(replaceAggregate.Definition), builder);
                break;
            case DropBlueTuskAggregateOperation dropAggregate:
                GenerateSchemaProgram("DropBlueTuskAggregate",
                    BlueTuskSchemaProgramMetadata.Serialize(dropAggregate.Definition), builder);
                break;
            case AlterBlueTuskPublicationOperation alterPublication:
                builder.Append("migrationBuilder.AlterBlueTuskPublication(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskPublicationMetadata.Serialize(alterPublication.OldDefinition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskPublicationMetadata.Serialize(alterPublication.Definition)))
                    .AppendLine(");");
                break;
            case DropBlueTuskPublicationOperation dropPublication:
                builder.Append("migrationBuilder.DropBlueTuskPublication(")
                    .Append(Dependencies.CSharpHelper.Literal(dropPublication.Name))
                    .AppendLine(");");
                break;
            case RenameBlueTuskPublicationOperation renamePublication:
                builder.Append("migrationBuilder.RenameBlueTuskPublication(")
                    .Append(Dependencies.CSharpHelper.Literal(renamePublication.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renamePublication.NewName))
                    .AppendLine(");");
                break;
            case CreateBlueTuskSubscriptionOperation createSubscription:
                builder.Append("migrationBuilder.CreateBlueTuskSubscription(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskSubscriptionMetadata.Serialize(createSubscription.Definition)))
                    .AppendLine(");");
                break;
            case AlterBlueTuskSubscriptionOperation alterSubscription:
                builder.Append("migrationBuilder.AlterBlueTuskSubscription(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskSubscriptionMetadata.Serialize(alterSubscription.OldDefinition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskSubscriptionMetadata.Serialize(alterSubscription.Definition)))
                    .AppendLine(");");
                break;
            case DropBlueTuskSubscriptionOperation dropSubscription:
                builder.Append("migrationBuilder.DropBlueTuskSubscription(")
                    .Append(Dependencies.CSharpHelper.Literal(dropSubscription.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(dropSubscription.HasSlot))
                    .AppendLine(");");
                break;
            case RenameBlueTuskSubscriptionOperation renameSubscription:
                builder.Append("migrationBuilder.RenameBlueTuskSubscription(")
                    .Append(Dependencies.CSharpHelper.Literal(renameSubscription.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameSubscription.NewName))
                    .AppendLine(");");
                break;
            case RefreshBlueTuskSubscriptionOperation refreshSubscription:
                builder.Append("migrationBuilder.RefreshBlueTuskSubscription(")
                    .Append(Dependencies.CSharpHelper.Literal(refreshSubscription.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(refreshSubscription.CopyData))
                    .AppendLine(");");
                break;
            case RefreshBlueTuskSubscriptionSequencesOperation refreshSubscriptionSequences:
                builder.Append("migrationBuilder.RefreshBlueTuskSubscriptionSequences(")
                    .Append(Dependencies.CSharpHelper.Literal(refreshSubscriptionSequences.Name))
                    .AppendLine(");");
                break;
            case SkipBlueTuskSubscriptionTransactionOperation skipSubscriptionTransaction:
                builder.Append("migrationBuilder.SkipBlueTuskSubscriptionTransaction(")
                    .Append(Dependencies.CSharpHelper.Literal(skipSubscriptionTransaction.Name))
                    .Append(", ")
                    .Append(Literal(skipSubscriptionTransaction.FinishLsn))
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

    private void GenerateSchemaProgram(string method, string definition, IndentedStringBuilder builder)
    {
        builder.Append("migrationBuilder.").Append(method).Append('(')
            .Append(Dependencies.CSharpHelper.Literal(definition))
            .AppendLine(");");
    }

    private void GenerateSchemaProgram(
        string method,
        string oldDefinition,
        string definition,
        IndentedStringBuilder builder)
    {
        builder.Append("migrationBuilder.").Append(method).Append('(')
            .Append(Dependencies.CSharpHelper.Literal(oldDefinition))
            .Append(", ")
            .Append(Dependencies.CSharpHelper.Literal(definition))
            .AppendLine(");");
    }

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
