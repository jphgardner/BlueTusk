using BlueTusk.EntityFrameworkCore.Collations.Internal;
using BlueTusk.EntityFrameworkCore.EventTriggers.Internal;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints.Internal;
using BlueTusk.EntityFrameworkCore.ExpressionIndexes.Internal;
using BlueTusk.EntityFrameworkCore.Extensions.Internal;
using BlueTusk.EntityFrameworkCore.ForeignData.Internal;
using BlueTusk.EntityFrameworkCore.Graphs;
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
            case CreateExpressionIndexOperation createExpressionIndex:
                builder
                    .Append(".CreateExpressionIndex(")
                    .Append(Dependencies.CSharpHelper.Literal(createExpressionIndex.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskExpressionIndexMetadata.Serialize(createExpressionIndex.Definition)))
                    .Append(", ")
                    .Append(Literal(createExpressionIndex.Schema))
                    .AppendLine(");");
                break;
            case DropExpressionIndexOperation dropExpressionIndex:
                builder
                    .Append(".DropExpressionIndex(")
                    .Append(Dependencies.CSharpHelper.Literal(dropExpressionIndex.Name))
                    .Append(", ")
                    .Append(Literal(dropExpressionIndex.Schema))
                    .Append(", ")
                    .Append(dropExpressionIndex.Concurrently ? "true" : "false")
                    .AppendLine(");");
                break;
            case RenameExpressionIndexOperation renameExpressionIndex:
                builder
                    .Append(".RenameExpressionIndex(")
                    .Append(Dependencies.CSharpHelper.Literal(renameExpressionIndex.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameExpressionIndex.NewName))
                    .Append(", ")
                    .Append(Literal(renameExpressionIndex.Schema))
                    .AppendLine(");");
                break;
            case ValidateCheckConstraintOperation validateCheckConstraint:
                builder
                    .Append(".ValidateCheckConstraint(")
                    .Append(Dependencies.CSharpHelper.Literal(validateCheckConstraint.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(validateCheckConstraint.Table))
                    .Append(", ")
                    .Append(Literal(validateCheckConstraint.Schema))
                    .AppendLine(");");
                break;
            case CreateTablespaceOperation createTablespace:
                builder
                    .Append(".CreateTablespace(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskTablespaceMetadata.Serialize(createTablespace.Definition)))
                    .AppendLine(");");
                break;
            case AlterTablespaceOperation alterTablespace:
                builder
                    .Append(".AlterTablespace(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskTablespaceMetadata.Serialize(alterTablespace.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskTablespaceMetadata.Serialize(alterTablespace.OldDefinition)))
                    .AppendLine(");");
                break;
            case RenameTablespaceOperation renameTablespace:
                builder
                    .Append(".RenameTablespace(")
                    .Append(Dependencies.CSharpHelper.Literal(renameTablespace.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameTablespace.NewName))
                    .AppendLine(");");
                break;
            case DropTablespaceOperation dropTablespace:
                builder
                    .Append(".DropTablespace(")
                    .Append(Dependencies.CSharpHelper.Literal(dropTablespace.Name))
                    .Append(", ")
                    .Append(dropTablespace.IfExists ? "true" : "false")
                    .AppendLine(");");
                break;
            case CreateCollationOperation createCollation:
                builder
                    .Append(".CreateCollation(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskCollationMetadata.Serialize(createCollation.Definition)))
                    .Append(", ")
                    .Append(createCollation.IfNotExists ? "true" : "false")
                    .AppendLine(");");
                break;
            case CreateCollationFromOperation createCollationFrom:
                builder
                    .Append(".CreateCollationFrom(")
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
            case RenameCollationOperation renameCollation:
                builder
                    .Append(".RenameCollation(")
                    .Append(Dependencies.CSharpHelper.Literal(renameCollation.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameCollation.NewName))
                    .Append(", ")
                    .Append(Literal(renameCollation.Schema))
                    .Append(", ")
                    .Append(Literal(renameCollation.NewSchema))
                    .AppendLine(");");
                break;
            case RefreshCollationVersionOperation refreshCollation:
                builder
                    .Append(".RefreshCollationVersion(")
                    .Append(Dependencies.CSharpHelper.Literal(refreshCollation.Name))
                    .Append(", ")
                    .Append(Literal(refreshCollation.Schema))
                    .AppendLine(");");
                break;
            case DropCollationOperation dropCollation:
                builder
                    .Append(".DropCollation(")
                    .Append(Dependencies.CSharpHelper.Literal(dropCollation.Name))
                    .Append(", ")
                    .Append(Literal(dropCollation.Schema))
                    .Append(", ")
                    .Append(dropCollation.IfExists ? "true" : "false")
                    .Append(", ")
                    .Append(dropCollation.Cascade ? "true" : "false")
                    .AppendLine(");");
                break;
            case CreateExtensionOperation createExtension:
                builder
                    .Append(".CreateExtension(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskExtensionMetadata.Serialize(createExtension.Definition)))
                    .Append(", ")
                    .Append(createExtension.IfNotExists ? "true" : "false")
                    .AppendLine(");");
                break;
            case AlterExtensionOperation alterExtension:
                builder
                    .Append(".AlterExtension(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskExtensionMetadata.Serialize(alterExtension.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskExtensionMetadata.Serialize(alterExtension.OldDefinition)))
                    .AppendLine(");");
                break;
            case DropExtensionOperation dropExtension:
                builder
                    .Append(".DropExtension(")
                    .Append(Dependencies.CSharpHelper.Literal(dropExtension.Name))
                    .Append(", ")
                    .Append(dropExtension.IfExists ? "true" : "false")
                    .Append(", ")
                    .Append(dropExtension.Cascade ? "true" : "false")
                    .AppendLine(");");
                break;
            case CreateViewOperation createView:
                builder
                    .Append(".CreateView(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskViewMetadata.Serialize(createView.Definition)))
                    .AppendLine(");");
                break;
            case ReplaceViewOperation replaceView:
                builder
                    .Append(".ReplaceView(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskViewMetadata.Serialize(replaceView.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskViewMetadata.Serialize(replaceView.OldDefinition)))
                    .AppendLine(");");
                break;
            case CreateMaterializedViewOperation createMaterializedView:
                builder
                    .Append(".CreateMaterializedView(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskViewMetadata.Serialize(createMaterializedView.Definition)))
                    .AppendLine(");");
                break;
            case AlterMaterializedViewOperation alterMaterializedView:
                builder
                    .Append(".AlterMaterializedView(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskViewMetadata.Serialize(alterMaterializedView.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskViewMetadata.Serialize(alterMaterializedView.OldDefinition)))
                    .AppendLine(");");
                break;
            case DropViewOperation dropView:
                builder
                    .Append(".DropView(")
                    .Append("global::BlueTusk.EntityFrameworkCore.Views.BlueTuskViewKind.")
                    .Append(dropView.Kind.ToString())
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(dropView.Name))
                    .Append(", ")
                    .Append(Literal(dropView.Schema))
                    .AppendLine(");");
                break;
            case RenameViewOperation renameView:
                builder
                    .Append(".RenameView(")
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
            case RefreshMaterializedViewOperation refreshMaterializedView:
                builder
                    .Append(".RefreshMaterializedView(")
                    .Append(Dependencies.CSharpHelper.Literal(refreshMaterializedView.Name))
                    .Append(", ")
                    .Append(Literal(refreshMaterializedView.Schema))
                    .Append(", ")
                    .Append(refreshMaterializedView.Concurrently ? "true" : "false")
                    .Append(", ")
                    .Append(refreshMaterializedView.WithData ? "true" : "false")
                    .AppendLine(");");
                break;
            case CreateRoutineOperation createRoutine:
                builder
                    .Append(".CreateRoutine(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskRoutineMetadata.Serialize(createRoutine.Definition)))
                    .AppendLine(");");
                break;
            case ReplaceRoutineOperation replaceRoutine:
                builder
                    .Append(".ReplaceRoutine(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskRoutineMetadata.Serialize(replaceRoutine.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskRoutineMetadata.Serialize(replaceRoutine.OldDefinition)))
                    .AppendLine(");");
                break;
            case DropRoutineOperation dropRoutine:
                builder
                    .Append(".DropRoutine(")
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
            case RenameRoutineOperation renameRoutine:
                builder
                    .Append(".RenameRoutine(")
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
            case CreateEnumTypeOperation createEnum:
                builder
                    .Append(".CreateEnumType(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(createEnum.Definition)))
                    .AppendLine(");");
                break;
            case AlterEnumTypeOperation alterEnum:
                builder
                    .Append(".AlterEnumType(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(alterEnum.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(alterEnum.OldDefinition)))
                    .AppendLine(");");
                break;
            case DropEnumTypeOperation dropEnum:
                GenerateDropType("DropEnumType", dropEnum.Name, dropEnum.Schema, builder);
                break;
            case CreateDomainTypeOperation createDomain:
                builder
                    .Append(".CreateDomainType(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(createDomain.Definition)))
                    .AppendLine(");");
                break;
            case AlterDomainTypeOperation alterDomain:
                builder
                    .Append(".AlterDomainType(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(alterDomain.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(alterDomain.OldDefinition)))
                    .AppendLine(");");
                break;
            case DropDomainTypeOperation dropDomain:
                GenerateDropType("DropDomainType", dropDomain.Name, dropDomain.Schema, builder);
                break;
            case CreateCompositeTypeOperation createComposite:
                builder
                    .Append(".CreateCompositeType(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(createComposite.Definition)))
                    .AppendLine(");");
                break;
            case AlterCompositeTypeOperation alterComposite:
                builder
                    .Append(".AlterCompositeType(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(alterComposite.Definition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(alterComposite.OldDefinition)))
                    .AppendLine(");");
                break;
            case DropCompositeTypeOperation dropComposite:
                GenerateDropType("DropCompositeType", dropComposite.Name, dropComposite.Schema, builder);
                break;
            case CreateRangeTypeOperation createRange:
                builder
                    .Append(".CreateRangeType(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskUserDefinedTypeMetadata.Serialize(createRange.Definition)))
                    .AppendLine(");");
                break;
            case DropRangeTypeOperation dropRange:
                GenerateDropType("DropRangeType", dropRange.Name, dropRange.Schema, builder);
                break;
            case RenameRangeTypeOperation renameRange:
                builder
                    .Append(".RenameRangeType(")
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
            case RenameUserDefinedTypeOperation renameType:
                builder
                    .Append(".RenameUserDefinedType(")
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
            case AddTableInheritanceOperation addInheritance:
                builder
                    .Append(".AddTableInheritance(")
                    .Append(Dependencies.CSharpHelper.Literal(addInheritance.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(addInheritance.ParentTable))
                    .Append(", ")
                    .Append(Literal(addInheritance.Schema))
                    .Append(", ")
                    .Append(Literal(addInheritance.ParentSchema))
                    .AppendLine(");");
                break;
            case RemoveTableInheritanceOperation removeInheritance:
                builder
                    .Append(".RemoveTableInheritance(")
                    .Append(Dependencies.CSharpHelper.Literal(removeInheritance.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(removeInheritance.ParentTable))
                    .Append(", ")
                    .Append(Literal(removeInheritance.Schema))
                    .Append(", ")
                    .Append(Literal(removeInheritance.ParentSchema))
                    .AppendLine(");");
                break;
            case AddExclusionConstraintOperation addExclusionConstraint:
                builder
                    .Append(".AddExclusionConstraint(")
                    .Append(Dependencies.CSharpHelper.Literal(addExclusionConstraint.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskExclusionConstraintMetadata.Serialize(addExclusionConstraint.Definition)))
                    .Append(", ")
                    .Append(Literal(addExclusionConstraint.Schema))
                    .AppendLine(");");
                break;
            case DropExclusionConstraintOperation dropExclusionConstraint:
                builder
                    .Append(".DropExclusionConstraint(")
                    .Append(Dependencies.CSharpHelper.Literal(dropExclusionConstraint.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(dropExclusionConstraint.Name))
                    .Append(", ")
                    .Append(Literal(dropExclusionConstraint.Schema))
                    .AppendLine(");");
                break;
            case RenameExclusionConstraintOperation renameExclusionConstraint:
                builder
                    .Append(".RenameExclusionConstraint(")
                    .Append(Dependencies.CSharpHelper.Literal(renameExclusionConstraint.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameExclusionConstraint.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameExclusionConstraint.NewName))
                    .Append(", ")
                    .Append(Literal(renameExclusionConstraint.Schema))
                    .AppendLine(");");
                break;
            case CreateTriggerOperation createTrigger:
                builder
                    .Append(".CreateTrigger(")
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
            case DropTriggerOperation dropTrigger:
                builder
                    .Append(".DropTrigger(")
                    .Append(Dependencies.CSharpHelper.Literal(dropTrigger.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(dropTrigger.Name))
                    .Append(", ")
                    .Append(Literal(dropTrigger.Schema))
                    .AppendLine(");");
                break;
            case RenameTriggerOperation renameTrigger:
                builder
                    .Append(".RenameTrigger(")
                    .Append(Dependencies.CSharpHelper.Literal(renameTrigger.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameTrigger.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameTrigger.NewName))
                    .Append(", ")
                    .Append(Literal(renameTrigger.Schema))
                    .AppendLine(");");
                break;
            case AlterTriggerEnabledModeOperation alterTriggerMode:
                builder
                    .Append(".AlterTriggerEnabledMode(")
                    .Append(Dependencies.CSharpHelper.Literal(alterTriggerMode.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(alterTriggerMode.Name))
                    .Append(", BlueTusk.EntityFrameworkCore.Triggers.BlueTuskTriggerEnabledMode.")
                    .Append(alterTriggerMode.EnabledMode.ToString())
                    .Append(", ")
                    .Append(Literal(alterTriggerMode.Schema))
                    .AppendLine(");");
                break;
            case CreateEventTriggerOperation createEventTrigger:
                builder.Append(".CreateEventTrigger(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskEventTriggerMetadata.Serialize(createEventTrigger.Definition)))
                    .AppendLine(");");
                break;
            case DropEventTriggerOperation dropEventTrigger:
                builder.Append(".DropEventTrigger(")
                    .Append(Dependencies.CSharpHelper.Literal(dropEventTrigger.Name))
                    .AppendLine(");");
                break;
            case RenameEventTriggerOperation renameEventTrigger:
                builder.Append(".RenameEventTrigger(")
                    .Append(Dependencies.CSharpHelper.Literal(renameEventTrigger.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameEventTrigger.NewName))
                    .AppendLine(");");
                break;
            case AlterEventTriggerEnabledModeOperation alterEventTriggerMode:
                builder.Append(".AlterEventTriggerEnabledMode(")
                    .Append(Dependencies.CSharpHelper.Literal(alterEventTriggerMode.Name))
                    .Append(", global::BlueTusk.EntityFrameworkCore.EventTriggers.BlueTuskEventTriggerEnabledMode.")
                    .Append(alterEventTriggerMode.EnabledMode.ToString())
                    .AppendLine(");");
                break;
            case CreateRuleOperation createRule:
                builder.Append(".CreateRule(")
                    .Append(Dependencies.CSharpHelper.Literal(createRule.Table)).Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(BlueTuskRuleMetadata.Serialize(createRule.Definition)))
                    .Append(", ").Append(Literal(createRule.Schema)).Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(createRule.OrReplace)).AppendLine(");");
                break;
            case DropRuleOperation dropRule:
                builder.Append(".DropRule(")
                    .Append(Dependencies.CSharpHelper.Literal(dropRule.Table)).Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(dropRule.Name)).Append(", ")
                    .Append(Literal(dropRule.Schema)).AppendLine(");");
                break;
            case RenameRuleOperation renameRule:
                builder.Append(".RenameRule(")
                    .Append(Dependencies.CSharpHelper.Literal(renameRule.Table)).Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameRule.Name)).Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameRule.NewName)).Append(", ")
                    .Append(Literal(renameRule.Schema)).AppendLine(");");
                break;
            case AlterRuleEnabledModeOperation alterRuleMode:
                builder.Append(".AlterRuleEnabledMode(")
                    .Append(Dependencies.CSharpHelper.Literal(alterRuleMode.Table)).Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(alterRuleMode.Name))
                    .Append(", BlueTusk.EntityFrameworkCore.Rules.BlueTuskRuleEnabledMode.")
                    .Append(alterRuleMode.EnabledMode.ToString()).Append(", ")
                    .Append(Literal(alterRuleMode.Schema)).AppendLine(");");
                break;
            case CreatePublicationOperation createPublication:
                builder.Append(".CreatePublication(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskPublicationMetadata.Serialize(createPublication.Definition)))
                    .AppendLine(");");
                break;
            case CreateForeignDataWrapperOperation createWrapper:
                builder.Append(".CreateForeignDataWrapper(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(createWrapper.Definition)))
                    .AppendLine(");");
                break;
            case AlterForeignDataWrapperOperation alterWrapper:
                builder.Append(".AlterForeignDataWrapper(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(alterWrapper.OldDefinition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(alterWrapper.Definition)))
                    .AppendLine(");");
                break;
            case DropForeignDataWrapperOperation dropWrapper:
                builder.Append(".DropForeignDataWrapper(")
                    .Append(Dependencies.CSharpHelper.Literal(dropWrapper.Name))
                    .AppendLine(");");
                break;
            case RenameForeignDataWrapperOperation renameWrapper:
                builder.Append(".RenameForeignDataWrapper(")
                    .Append(Dependencies.CSharpHelper.Literal(renameWrapper.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameWrapper.NewName))
                    .AppendLine(");");
                break;
            case CreateForeignServerOperation createServer:
                builder.Append(".CreateForeignServer(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(createServer.Definition)))
                    .AppendLine(");");
                break;
            case AlterForeignServerOperation alterServer:
                builder.Append(".AlterForeignServer(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(alterServer.OldDefinition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(alterServer.Definition)))
                    .AppendLine(");");
                break;
            case DropForeignServerOperation dropServer:
                builder.Append(".DropForeignServer(")
                    .Append(Dependencies.CSharpHelper.Literal(dropServer.Name))
                    .AppendLine(");");
                break;
            case RenameForeignServerOperation renameServer:
                builder.Append(".RenameForeignServer(")
                    .Append(Dependencies.CSharpHelper.Literal(renameServer.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameServer.NewName))
                    .AppendLine(");");
                break;
            case CreateUserMappingOperation createMapping:
                builder.Append(".CreateUserMapping(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(createMapping.Definition)))
                    .AppendLine(");");
                break;
            case AlterUserMappingOperation alterMapping:
                builder.Append(".AlterUserMapping(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(alterMapping.OldDefinition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskForeignDataMetadata.Serialize(alterMapping.Definition)))
                    .AppendLine(");");
                break;
            case DropUserMappingOperation dropMapping:
                builder.Append(".DropUserMapping(")
                    .Append(Dependencies.CSharpHelper.Literal(dropMapping.ServerName))
                    .Append(", ")
                    .Append(Literal(dropMapping.UserName))
                    .AppendLine(");");
                break;
            case CreateOperatorOperation createOperator:
                GenerateSchemaProgram("CreateOperator",
                    BlueTuskSchemaProgramMetadata.Serialize(createOperator.Definition), builder);
                break;
            case ReplaceOperatorOperation replaceOperator:
                GenerateSchemaProgram("ReplaceOperator",
                    BlueTuskSchemaProgramMetadata.Serialize(replaceOperator.OldDefinition),
                    BlueTuskSchemaProgramMetadata.Serialize(replaceOperator.Definition), builder);
                break;
            case DropOperatorOperation dropOperator:
                GenerateSchemaProgram("DropOperator",
                    BlueTuskSchemaProgramMetadata.Serialize(dropOperator.Definition), builder);
                break;
            case CreateOperatorFamilyOperation createFamily:
                GenerateSchemaProgram("CreateOperatorFamily",
                    BlueTuskSchemaProgramMetadata.Serialize(createFamily.Definition), builder);
                break;
            case AlterOperatorFamilyOperation alterFamily:
                GenerateSchemaProgram("AlterOperatorFamily",
                    BlueTuskSchemaProgramMetadata.Serialize(alterFamily.OldDefinition),
                    BlueTuskSchemaProgramMetadata.Serialize(alterFamily.Definition), builder);
                break;
            case DropOperatorFamilyOperation dropFamily:
                GenerateSchemaProgram("DropOperatorFamily",
                    BlueTuskSchemaProgramMetadata.Serialize(dropFamily.Definition), builder);
                break;
            case CreateOperatorClassOperation createClass:
                GenerateSchemaProgram("CreateOperatorClass",
                    BlueTuskSchemaProgramMetadata.Serialize(createClass.Definition), builder);
                break;
            case ReplaceOperatorClassOperation replaceClass:
                GenerateSchemaProgram("ReplaceOperatorClass",
                    BlueTuskSchemaProgramMetadata.Serialize(replaceClass.OldDefinition),
                    BlueTuskSchemaProgramMetadata.Serialize(replaceClass.Definition), builder);
                break;
            case DropOperatorClassOperation dropClass:
                GenerateSchemaProgram("DropOperatorClass",
                    BlueTuskSchemaProgramMetadata.Serialize(dropClass.Definition), builder);
                break;
            case CreateCastOperation createCast:
                GenerateSchemaProgram("CreateCast",
                    BlueTuskSchemaProgramMetadata.Serialize(createCast.Definition), builder);
                break;
            case ReplaceCastOperation replaceCast:
                GenerateSchemaProgram("ReplaceCast",
                    BlueTuskSchemaProgramMetadata.Serialize(replaceCast.OldDefinition),
                    BlueTuskSchemaProgramMetadata.Serialize(replaceCast.Definition), builder);
                break;
            case DropCastOperation dropCast:
                GenerateSchemaProgram("DropCast",
                    BlueTuskSchemaProgramMetadata.Serialize(dropCast.Definition), builder);
                break;
            case CreateAggregateOperation createAggregate:
                GenerateSchemaProgram("CreateAggregate",
                    BlueTuskSchemaProgramMetadata.Serialize(createAggregate.Definition), builder);
                break;
            case ReplaceAggregateOperation replaceAggregate:
                GenerateSchemaProgram("ReplaceAggregate",
                    BlueTuskSchemaProgramMetadata.Serialize(replaceAggregate.OldDefinition),
                    BlueTuskSchemaProgramMetadata.Serialize(replaceAggregate.Definition), builder);
                break;
            case DropAggregateOperation dropAggregate:
                GenerateSchemaProgram("DropAggregate",
                    BlueTuskSchemaProgramMetadata.Serialize(dropAggregate.Definition), builder);
                break;
            case AlterPublicationOperation alterPublication:
                builder.Append(".AlterPublication(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskPublicationMetadata.Serialize(alterPublication.OldDefinition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskPublicationMetadata.Serialize(alterPublication.Definition)))
                    .AppendLine(");");
                break;
            case DropPublicationOperation dropPublication:
                builder.Append(".DropPublication(")
                    .Append(Dependencies.CSharpHelper.Literal(dropPublication.Name))
                    .AppendLine(");");
                break;
            case RenamePublicationOperation renamePublication:
                builder.Append(".RenamePublication(")
                    .Append(Dependencies.CSharpHelper.Literal(renamePublication.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renamePublication.NewName))
                    .AppendLine(");");
                break;
            case CreateSubscriptionOperation createSubscription:
                builder.Append(".CreateSubscription(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskSubscriptionMetadata.Serialize(createSubscription.Definition)))
                    .AppendLine(");");
                break;
            case AlterSubscriptionOperation alterSubscription:
                builder.Append(".AlterSubscription(")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskSubscriptionMetadata.Serialize(alterSubscription.OldDefinition)))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskSubscriptionMetadata.Serialize(alterSubscription.Definition)))
                    .AppendLine(");");
                break;
            case DropSubscriptionOperation dropSubscription:
                builder.Append(".DropSubscription(")
                    .Append(Dependencies.CSharpHelper.Literal(dropSubscription.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(dropSubscription.HasSlot))
                    .AppendLine(");");
                break;
            case RenameSubscriptionOperation renameSubscription:
                builder.Append(".RenameSubscription(")
                    .Append(Dependencies.CSharpHelper.Literal(renameSubscription.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renameSubscription.NewName))
                    .AppendLine(");");
                break;
            case RefreshSubscriptionOperation refreshSubscription:
                builder.Append(".RefreshSubscription(")
                    .Append(Dependencies.CSharpHelper.Literal(refreshSubscription.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(refreshSubscription.CopyData))
                    .AppendLine(");");
                break;
            case RefreshSubscriptionSequencesOperation refreshSubscriptionSequences:
                builder.Append(".RefreshSubscriptionSequences(")
                    .Append(Dependencies.CSharpHelper.Literal(refreshSubscriptionSequences.Name))
                    .AppendLine(");");
                break;
            case SkipSubscriptionTransactionOperation skipSubscriptionTransaction:
                builder.Append(".SkipSubscriptionTransaction(")
                    .Append(Dependencies.CSharpHelper.Literal(skipSubscriptionTransaction.Name))
                    .Append(", ")
                    .Append(Literal(skipSubscriptionTransaction.FinishLsn))
                    .AppendLine(");");
                break;
            case CreateRowSecurityPolicyOperation createPolicy:
                builder
                    .Append(".CreateRowSecurityPolicy(")
                    .Append(Dependencies.CSharpHelper.Literal(createPolicy.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskRowLevelSecurityMetadata.Serialize(createPolicy.Definition)))
                    .Append(", ")
                    .Append(Literal(createPolicy.Schema))
                    .AppendLine(");");
                break;
            case AlterRowSecurityPolicyOperation alterPolicy:
                builder
                    .Append(".AlterRowSecurityPolicy(")
                    .Append(Dependencies.CSharpHelper.Literal(alterPolicy.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskRowLevelSecurityMetadata.Serialize(alterPolicy.Definition)))
                    .Append(", ")
                    .Append(Literal(alterPolicy.Schema))
                    .AppendLine(");");
                break;
            case DropRowSecurityPolicyOperation dropPolicy:
                builder
                    .Append(".DropRowSecurityPolicy(")
                    .Append(Dependencies.CSharpHelper.Literal(dropPolicy.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(dropPolicy.Name))
                    .Append(", ")
                    .Append(Literal(dropPolicy.Schema))
                    .AppendLine(");");
                break;
            case RenameRowSecurityPolicyOperation renamePolicy:
                builder
                    .Append(".RenameRowSecurityPolicy(")
                    .Append(Dependencies.CSharpHelper.Literal(renamePolicy.Table))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renamePolicy.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(renamePolicy.NewName))
                    .Append(", ")
                    .Append(Literal(renamePolicy.Schema))
                    .AppendLine(");");
                break;
            case AlterRowLevelSecurityOperation alterRowLevelSecurity:
                builder
                    .Append(".AlterRowLevelSecurity(")
                    .Append(Dependencies.CSharpHelper.Literal(alterRowLevelSecurity.Table))
                    .Append(", ")
                    .Append(Literal(alterRowLevelSecurity.Enabled))
                    .Append(", ")
                    .Append(Literal(alterRowLevelSecurity.Forced))
                    .Append(", ")
                    .Append(Literal(alterRowLevelSecurity.Schema))
                    .AppendLine(");");
                break;
            case CreatePartitionOperation createPartition:
                builder
                    .Append(".CreatePartition(")
                    .Append(Dependencies.CSharpHelper.Literal(createPartition.ParentName))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(
                        BlueTuskPartitionMetadata.Serialize(createPartition.Definition)))
                    .Append(", ")
                    .Append(Literal(createPartition.ParentSchema))
                    .AppendLine(");");
                break;
            case DropPartitionOperation dropPartition:
                builder
                    .Append(".DropPartition(")
                    .Append(Dependencies.CSharpHelper.Literal(dropPartition.Name))
                    .Append(", ")
                    .Append(Literal(dropPartition.Schema))
                    .AppendLine(");");
                break;
            case AlterPartitionOperation alterPartition:
                builder
                    .Append(".AlterPartition(")
                    .Append(Dependencies.CSharpHelper.Literal(alterPartition.Name))
                    .Append(", ")
                    .Append(Dependencies.CSharpHelper.Literal(alterPartition.NewName))
                    .Append(", ")
                    .Append(Literal(alterPartition.Schema))
                    .Append(", ")
                    .Append(Literal(alterPartition.NewSchema))
                    .AppendLine(");");
                break;
            case AttachPartitionOperation attachPartition:
                builder
                    .Append(".AttachPartition(")
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
            case DetachPartitionOperation detachPartition:
                builder
                    .Append(".DetachPartition(")
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
            case CreatePropertyGraphOperation create:
                GeneratePropertyGraph(create.Definition, builder);
                break;
            case DropPropertyGraphOperation drop:
                builder
                    .Append(".DropPropertyGraph(")
                    .Append(Dependencies.CSharpHelper.Literal(drop.Name))
                    .Append(", ")
                    .Append(Literal(drop.Schema))
                    .AppendLine(");");
                break;
            case AlterPropertyGraphOperation alter:
                builder
                    .Append(".AlterPropertyGraph(")
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

    private void GeneratePropertyGraph(
        BlueTuskPropertyGraphDefinition definition,
        IndentedStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(definition);
        builder
            .Append(".CreatePropertyGraph(")
            .Append(Dependencies.CSharpHelper.Literal(definition.Name))
            .AppendLine(",");
        using (builder.Indent())
        {
            builder.AppendLine("graph => graph");
            using (builder.Indent())
            {
                for (var index = 0; index < definition.ElementTables.Count; index++)
                {
                    GeneratePropertyGraphElement(
                        definition.ElementTables[index],
                        index == definition.ElementTables.Count - 1,
                        builder);
                }
            }
            builder
                .Append(Literal(definition.Schema))
                .AppendLine(");");
        }
    }

    private void GeneratePropertyGraphElement(
        BlueTuskGraphElementTableDefinition element,
        bool isLast,
        IndentedStringBuilder builder)
    {
        var method = element.Kind == BlueTuskGraphElementKind.Vertex ? ".Vertex(" : ".Edge(";
        var variable = element.Kind == BlueTuskGraphElementKind.Vertex ? "vertex" : "edge";
        builder.AppendLine(method);
        using (builder.Indent())
        {
            builder
                .Append(Dependencies.CSharpHelper.Literal(element.Alias))
                .AppendLine(",")
                .Append(Dependencies.CSharpHelper.Literal(element.Table))
                .AppendLine(",")
                .Append(Literal(element.Schema))
                .AppendLine(",")
                .Append(variable)
                .Append(" => ")
                .AppendLine(variable);
            using (builder.Indent())
            {
                if (element.KeyColumns.Count > 0)
                {
                    builder.Append(".HasKey(");
                    AppendStringArguments(element.KeyColumns, builder);
                    builder.AppendLine(")");
                }
                foreach (var label in element.Labels)
                {
                    GeneratePropertyGraphLabel(label, builder);
                }
                if (element.Kind == BlueTuskGraphElementKind.Edge)
                {
                    GeneratePropertyGraphEndpoint("HasSource", element.Source, builder);
                    GeneratePropertyGraphEndpoint("HasDestination", element.Destination, builder);
                }
            }
            builder.AppendLine(isLast ? ")," : ")");
        }
    }

    private void GeneratePropertyGraphLabel(
        BlueTuskGraphLabelDefinition label,
        IndentedStringBuilder builder)
    {
        if (label.Properties.Count == 0)
        {
            builder
                .Append(".HasLabel(")
                .Append(Dependencies.CSharpHelper.Literal(label.Name))
                .AppendLine(")");
            return;
        }

        builder.AppendLine(".HasLabel(");
        using (builder.Indent())
        {
            builder
                .Append(Dependencies.CSharpHelper.Literal(label.Name))
                .AppendLine(",")
                .AppendLine("label => label");
            using (builder.Indent())
            {
                foreach (var property in label.Properties)
                {
                    builder.Append(property.IsColumn ? ".Property(" : ".Expression(")
                        .Append(Dependencies.CSharpHelper.Literal(property.Expression));
                    if (!property.IsColumn ||
                        !string.Equals(property.Expression, property.Name, StringComparison.Ordinal))
                    {
                        builder
                            .Append(", ")
                            .Append(Dependencies.CSharpHelper.Literal(property.Name));
                    }
                    builder.AppendLine(")");
                }
            }
            builder.AppendLine(")");
        }
    }

    private void GeneratePropertyGraphEndpoint(
        string method,
        BlueTuskGraphEndpointDefinition? endpoint,
        IndentedStringBuilder builder)
    {
        if (endpoint is null)
        {
            throw new InvalidOperationException(
                $"Property-graph edge must configure {method} before it can be scaffolded.");
        }
        builder
            .Append('.')
            .Append(method)
            .Append('(')
            .Append(Dependencies.CSharpHelper.Literal(endpoint.VertexTableAlias))
            .Append(", ");
        AppendStringCollection(endpoint.EdgeKeyColumns, builder);
        builder.Append(", ");
        AppendStringCollection(endpoint.VertexKeyColumns, builder);
        builder.AppendLine(")");
    }

    private void AppendStringArguments(
        IReadOnlyList<string> values,
        IndentedStringBuilder builder)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0) { builder.Append(", "); }
            builder.Append(Dependencies.CSharpHelper.Literal(values[index]));
        }
    }

    private void AppendStringCollection(
        IReadOnlyList<string> values,
        IndentedStringBuilder builder)
    {
        builder.Append('[');
        AppendStringArguments(values, builder);
        builder.Append(']');
    }

    private void GenerateSchemaProgram(string method, string definition, IndentedStringBuilder builder)
    {
        builder.Append(".").Append(method).Append('(')
            .Append(Dependencies.CSharpHelper.Literal(definition))
            .AppendLine(");");
    }

    private void GenerateSchemaProgram(
        string method,
        string oldDefinition,
        string definition,
        IndentedStringBuilder builder)
    {
        builder.Append(".").Append(method).Append('(')
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
        builder.Append(".").Append(method).Append("(")
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
