using System.Text;
using BlueTusk.EntityFrameworkCore.Collations;
using BlueTusk.EntityFrameworkCore.Collations.Internal;
using BlueTusk.EntityFrameworkCore.EventTriggers;
using BlueTusk.EntityFrameworkCore.EventTriggers.Internal;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints.Internal;
using BlueTusk.EntityFrameworkCore.Extensions.Internal;
using BlueTusk.EntityFrameworkCore.ForeignData;
using BlueTusk.EntityFrameworkCore.ForeignData.Internal;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Partitioning;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
using BlueTusk.EntityFrameworkCore.Publications;
using BlueTusk.EntityFrameworkCore.Publications.Internal;
using BlueTusk.EntityFrameworkCore.Routines;
using BlueTusk.EntityFrameworkCore.Routines.Internal;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity;
using BlueTusk.EntityFrameworkCore.Rules;
using BlueTusk.EntityFrameworkCore.Rules.Internal;
using BlueTusk.EntityFrameworkCore.SchemaPrograms;
using BlueTusk.EntityFrameworkCore.SchemaPrograms.Internal;
using BlueTusk.EntityFrameworkCore.Subscriptions;
using BlueTusk.EntityFrameworkCore.Subscriptions.Internal;
using BlueTusk.EntityFrameworkCore.Tablespaces;
using BlueTusk.EntityFrameworkCore.Tablespaces.Internal;
using BlueTusk.EntityFrameworkCore.Triggers;
using BlueTusk.EntityFrameworkCore.Triggers.Internal;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes.Internal;
using BlueTusk.EntityFrameworkCore.Views;
using BlueTusk.EntityFrameworkCore.Views.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal sealed class BlueTuskMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies)
    : MigrationsSqlGenerator(dependencies)
{
    protected override void Generate(
        MigrationOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        switch (operation)
        {
            case CreateBlueTuskTablespaceOperation createTablespace:
                Generate(createTablespace, builder);
                break;
            case AlterBlueTuskTablespaceOperation alterTablespace:
                Generate(alterTablespace, builder);
                break;
            case RenameBlueTuskTablespaceOperation renameTablespace:
                Generate(renameTablespace, builder);
                break;
            case DropBlueTuskTablespaceOperation dropTablespace:
                Generate(dropTablespace, builder);
                break;
            case CreateBlueTuskExtensionOperation createExtension:
                Generate(createExtension, builder);
                break;
            case AlterBlueTuskExtensionOperation alterExtension:
                Generate(alterExtension, builder);
                break;
            case DropBlueTuskExtensionOperation dropExtension:
                Generate(dropExtension, builder);
                break;
            case CreateBlueTuskCollationOperation createCollation:
                Generate(createCollation, builder);
                break;
            case CreateBlueTuskCollationFromOperation createCollationFrom:
                Generate(createCollationFrom, builder);
                break;
            case RenameBlueTuskCollationOperation renameCollation:
                Generate(renameCollation, builder);
                break;
            case RefreshBlueTuskCollationVersionOperation refreshCollation:
                Generate(refreshCollation, builder);
                break;
            case DropBlueTuskCollationOperation dropCollation:
                Generate(dropCollation, builder);
                break;
            case CreateBlueTuskViewOperation createView:
                Generate(createView, builder);
                break;
            case ReplaceBlueTuskViewOperation replaceView:
                Generate(replaceView, builder);
                break;
            case CreateBlueTuskMaterializedViewOperation createMaterializedView:
                Generate(createMaterializedView, builder);
                break;
            case AlterBlueTuskMaterializedViewOperation alterMaterializedView:
                Generate(alterMaterializedView, builder);
                break;
            case DropBlueTuskViewOperation dropView:
                Generate(dropView, builder);
                break;
            case RenameBlueTuskViewOperation renameView:
                Generate(renameView, builder);
                break;
            case RefreshBlueTuskMaterializedViewOperation refreshMaterializedView:
                Generate(refreshMaterializedView, builder);
                break;
            case CreateBlueTuskRoutineOperation createRoutine:
                Generate(createRoutine, builder);
                break;
            case ReplaceBlueTuskRoutineOperation replaceRoutine:
                Generate(replaceRoutine, builder);
                break;
            case DropBlueTuskRoutineOperation dropRoutine:
                Generate(dropRoutine, builder);
                break;
            case RenameBlueTuskRoutineOperation renameRoutine:
                Generate(renameRoutine, builder);
                break;
            case CreateBlueTuskEnumTypeOperation createEnum:
                Generate(createEnum, builder);
                break;
            case AlterBlueTuskEnumTypeOperation alterEnum:
                Generate(alterEnum, builder);
                break;
            case DropBlueTuskEnumTypeOperation dropEnum:
                Generate(dropEnum, builder);
                break;
            case CreateBlueTuskDomainTypeOperation createDomain:
                Generate(createDomain, builder);
                break;
            case AlterBlueTuskDomainTypeOperation alterDomain:
                Generate(alterDomain, builder);
                break;
            case DropBlueTuskDomainTypeOperation dropDomain:
                Generate(dropDomain, builder);
                break;
            case CreateBlueTuskCompositeTypeOperation createComposite:
                Generate(createComposite, builder);
                break;
            case AlterBlueTuskCompositeTypeOperation alterComposite:
                Generate(alterComposite, builder);
                break;
            case DropBlueTuskCompositeTypeOperation dropComposite:
                Generate(dropComposite, builder);
                break;
            case CreateBlueTuskRangeTypeOperation createRange:
                Generate(createRange, builder);
                break;
            case DropBlueTuskRangeTypeOperation dropRange:
                Generate(dropRange, builder);
                break;
            case RenameBlueTuskRangeTypeOperation renameRange:
                Generate(renameRange, builder);
                break;
            case RenameBlueTuskUserDefinedTypeOperation renameType:
                Generate(renameType, builder);
                break;
            case AddBlueTuskTableInheritanceOperation addInheritance:
                Generate(addInheritance, builder);
                break;
            case RemoveBlueTuskTableInheritanceOperation removeInheritance:
                Generate(removeInheritance, builder);
                break;
            case CreateBlueTuskRowSecurityPolicyOperation createPolicy:
                Generate(createPolicy, builder);
                break;
            case AlterBlueTuskRowSecurityPolicyOperation alterPolicy:
                Generate(alterPolicy, builder);
                break;
            case DropBlueTuskRowSecurityPolicyOperation dropPolicy:
                Generate(dropPolicy, builder);
                break;
            case RenameBlueTuskRowSecurityPolicyOperation renamePolicy:
                Generate(renamePolicy, builder);
                break;
            case AlterBlueTuskRowLevelSecurityOperation alterRowLevelSecurity:
                Generate(alterRowLevelSecurity, builder);
                break;
            case AddBlueTuskExclusionConstraintOperation addExclusionConstraint:
                Generate(addExclusionConstraint, builder);
                break;
            case DropBlueTuskExclusionConstraintOperation dropExclusionConstraint:
                Generate(dropExclusionConstraint, builder);
                break;
            case RenameBlueTuskExclusionConstraintOperation renameExclusionConstraint:
                Generate(renameExclusionConstraint, builder);
                break;
            case CreateBlueTuskTriggerOperation createTrigger:
                Generate(createTrigger, builder);
                break;
            case DropBlueTuskTriggerOperation dropTrigger:
                Generate(dropTrigger, builder);
                break;
            case RenameBlueTuskTriggerOperation renameTrigger:
                Generate(renameTrigger, builder);
                break;
            case AlterBlueTuskTriggerEnabledModeOperation alterTriggerMode:
                Generate(alterTriggerMode, builder);
                break;
            case CreateBlueTuskEventTriggerOperation createEventTrigger:
                Generate(createEventTrigger, builder);
                break;
            case DropBlueTuskEventTriggerOperation dropEventTrigger:
                Generate(dropEventTrigger, builder);
                break;
            case RenameBlueTuskEventTriggerOperation renameEventTrigger:
                Generate(renameEventTrigger, builder);
                break;
            case AlterBlueTuskEventTriggerEnabledModeOperation alterEventTriggerMode:
                Generate(alterEventTriggerMode, builder);
                break;
            case CreateBlueTuskRuleOperation createRule:
                Generate(createRule, builder);
                break;
            case DropBlueTuskRuleOperation dropRule:
                Generate(dropRule, builder);
                break;
            case RenameBlueTuskRuleOperation renameRule:
                Generate(renameRule, builder);
                break;
            case AlterBlueTuskRuleEnabledModeOperation alterRuleMode:
                Generate(alterRuleMode, builder);
                break;
            case CreateBlueTuskPublicationOperation createPublication:
                Generate(createPublication, builder);
                break;
            case AlterBlueTuskPublicationOperation alterPublication:
                Generate(alterPublication, builder);
                break;
            case DropBlueTuskPublicationOperation dropPublication:
                Generate(dropPublication, builder);
                break;
            case RenameBlueTuskPublicationOperation renamePublication:
                Generate(renamePublication, builder);
                break;
            case CreateBlueTuskSubscriptionOperation createSubscription:
                Generate(createSubscription, builder);
                break;
            case AlterBlueTuskSubscriptionOperation alterSubscription:
                Generate(alterSubscription, builder);
                break;
            case DropBlueTuskSubscriptionOperation dropSubscription:
                Generate(dropSubscription, builder);
                break;
            case RenameBlueTuskSubscriptionOperation renameSubscription:
                Generate(renameSubscription, builder);
                break;
            case RefreshBlueTuskSubscriptionOperation refreshSubscription:
                Generate(refreshSubscription, builder);
                break;
            case RefreshBlueTuskSubscriptionSequencesOperation refreshSubscriptionSequences:
                Generate(refreshSubscriptionSequences, builder);
                break;
            case SkipBlueTuskSubscriptionTransactionOperation skipSubscriptionTransaction:
                Generate(skipSubscriptionTransaction, builder);
                break;
            case CreateBlueTuskForeignDataWrapperOperation createWrapper:
                Generate(createWrapper, builder);
                break;
            case AlterBlueTuskForeignDataWrapperOperation alterWrapper:
                Generate(alterWrapper, builder);
                break;
            case DropBlueTuskForeignDataWrapperOperation dropWrapper:
                Generate(dropWrapper, builder);
                break;
            case RenameBlueTuskForeignDataWrapperOperation renameWrapper:
                Generate(renameWrapper, builder);
                break;
            case CreateBlueTuskForeignServerOperation createServer:
                Generate(createServer, builder);
                break;
            case AlterBlueTuskForeignServerOperation alterServer:
                Generate(alterServer, builder);
                break;
            case DropBlueTuskForeignServerOperation dropServer:
                Generate(dropServer, builder);
                break;
            case RenameBlueTuskForeignServerOperation renameServer:
                Generate(renameServer, builder);
                break;
            case CreateBlueTuskUserMappingOperation createMapping:
                Generate(createMapping, builder);
                break;
            case AlterBlueTuskUserMappingOperation alterMapping:
                Generate(alterMapping, builder);
                break;
            case DropBlueTuskUserMappingOperation dropMapping:
                Generate(dropMapping, builder);
                break;
            case CreateBlueTuskOperatorOperation createOperator:
                Generate(createOperator, builder);
                break;
            case ReplaceBlueTuskOperatorOperation replaceOperator:
                Generate(replaceOperator, builder);
                break;
            case DropBlueTuskOperatorOperation dropOperator:
                Generate(dropOperator, builder);
                break;
            case CreateBlueTuskOperatorFamilyOperation createOperatorFamily:
                Generate(createOperatorFamily, builder);
                break;
            case AlterBlueTuskOperatorFamilyOperation alterOperatorFamily:
                Generate(alterOperatorFamily, builder);
                break;
            case DropBlueTuskOperatorFamilyOperation dropOperatorFamily:
                Generate(dropOperatorFamily, builder);
                break;
            case CreateBlueTuskOperatorClassOperation createOperatorClass:
                Generate(createOperatorClass, builder);
                break;
            case ReplaceBlueTuskOperatorClassOperation replaceOperatorClass:
                Generate(replaceOperatorClass, builder);
                break;
            case DropBlueTuskOperatorClassOperation dropOperatorClass:
                Generate(dropOperatorClass, builder);
                break;
            case CreateBlueTuskCastOperation createCast:
                Generate(createCast, builder);
                break;
            case ReplaceBlueTuskCastOperation replaceCast:
                Generate(replaceCast, builder);
                break;
            case DropBlueTuskCastOperation dropCast:
                Generate(dropCast, builder);
                break;
            case CreateBlueTuskAggregateOperation createAggregate:
                Generate(createAggregate, builder);
                break;
            case ReplaceBlueTuskAggregateOperation replaceAggregate:
                Generate(replaceAggregate, builder);
                break;
            case DropBlueTuskAggregateOperation dropAggregate:
                Generate(dropAggregate, builder);
                break;
            case CreateBlueTuskPartitionOperation createPartition:
                Generate(createPartition, builder);
                break;
            case DropBlueTuskPartitionOperation dropPartition:
                Generate(dropPartition, builder);
                break;
            case AlterBlueTuskPartitionOperation alterPartition:
                Generate(alterPartition, builder);
                break;
            case AttachBlueTuskPartitionOperation attachPartition:
                Generate(attachPartition, builder);
                break;
            case DetachBlueTuskPartitionOperation detachPartition:
                Generate(detachPartition, builder);
                break;
            case CreateBlueTuskPropertyGraphOperation create:
                Generate(create, builder);
                break;
            case DropBlueTuskPropertyGraphOperation drop:
                Generate(drop, builder);
                break;
            case AlterBlueTuskPropertyGraphOperation alter:
                Generate(alter, builder);
                break;
            default:
                base.Generate(operation, model, builder);
                break;
        }
    }

    private void Generate(
        CreateBlueTuskExtensionOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskExtensionMetadata.Normalize(operation.Definition);
        BlueTuskExtensionMetadata.Validate(definition);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("CREATE EXTENSION ");
        if (operation.IfNotExists)
        {
            builder.Append("IF NOT EXISTS ");
        }

        builder.Append(helper.DelimitIdentifier(definition.Name));
        if (definition.Schema is not null)
        {
            builder.Append(" WITH SCHEMA ").Append(helper.DelimitIdentifier(definition.Schema));
        }

        if (definition.Version is not null)
        {
            builder.Append(" VERSION ");
            AppendStringLiteral(builder, definition.Version);
        }

        if (definition.InstallDependencies)
        {
            builder.Append(" CASCADE");
        }

        EndStatement(builder);
    }

    private void Generate(
        AlterBlueTuskExtensionOperation operation,
        MigrationCommandListBuilder builder)
    {
        var oldDefinition = BlueTuskExtensionMetadata.Normalize(operation.OldDefinition);
        var definition = BlueTuskExtensionMetadata.Normalize(operation.Definition);
        BlueTuskExtensionMetadata.Validate(oldDefinition);
        BlueTuskExtensionMetadata.Validate(definition);
        if (!string.Equals(oldDefinition.Name, definition.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "PostgreSQL cannot rename an extension. Use an explicit create/drop migration.");
        }

        var name = Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Name);
        if (!string.Equals(oldDefinition.Version, definition.Version, StringComparison.Ordinal))
        {
            builder.Append("ALTER EXTENSION ").Append(name).Append(" UPDATE");
            if (definition.Version is not null)
            {
                builder.Append(" TO ");
                AppendStringLiteral(builder, definition.Version);
            }

            EndStatement(builder);
        }

        if (!string.Equals(oldDefinition.Schema, definition.Schema, StringComparison.Ordinal))
        {
            if (definition.Schema is null)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL extension '{definition.Name}' cannot be moved to an unspecified schema.");
            }

            builder.Append("ALTER EXTENSION ").Append(name)
                .Append(" SET SCHEMA ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Schema));
            EndStatement(builder);
        }
    }

    private void Generate(
        DropBlueTuskExtensionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        builder.Append("DROP EXTENSION ");
        if (operation.IfExists)
        {
            builder.Append("IF EXISTS ");
        }

        builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(operation.Cascade ? " CASCADE" : " RESTRICT");
        EndStatement(builder);
    }

    private void Generate(
        CreateBlueTuskCollationOperation operation,
        MigrationCommandListBuilder builder)
    {
        BlueTuskCollationMetadata.Validate(operation.Definition);
        var sql = BuildCreateCollationSql(operation.Definition, operation.IfNotExists);
        var minimumVersion = operation.Definition.Provider == BlueTuskCollationProvider.Builtin
            ? 170000
            : operation.Definition.Rules is null
                ? 0
                : 160000;
        if (minimumVersion == 0)
        {
            builder.Append(sql);
            EndStatement(builder);
            return;
        }

        const string delimiter = "$BlueTuskCollation$";
        var message = minimumVersion == 170000
            ? "BlueTusk built-in collations require PostgreSQL 17 or later."
            : "BlueTusk ICU collation rules require PostgreSQL 16 or later.";
        builder.Append("DO ").AppendLine(delimiter)
            .AppendLine("BEGIN")
            .Append("    IF current_setting('server_version_num')::integer < ")
            .Append(minimumVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .AppendLine(" THEN")
            .AppendLine("        RAISE EXCEPTION USING")
            .AppendLine("            ERRCODE = '0A000',")
            .Append("            MESSAGE = '").Append(EscapeLiteral(message)).AppendLine("';")
            .AppendLine("    END IF;")
            .Append("    EXECUTE '").Append(EscapeLiteral(sql)).AppendLine("';")
            .AppendLine("END;")
            .Append(delimiter);
        EndStatement(builder);
    }

    private string BuildCreateCollationSql(
        BlueTuskCollationDefinition definition,
        bool ifNotExists)
    {
        var helper = Dependencies.SqlGenerationHelper;
        var sql = new StringBuilder("CREATE COLLATION ");
        if (ifNotExists)
        {
            sql.Append("IF NOT EXISTS ");
        }

        sql.Append(helper.DelimitIdentifier(definition.Name, definition.Schema)).Append(" (");
        var options = new List<string>();
        if (definition.Locale is not null)
        {
            options.Add($"LOCALE = '{EscapeLiteral(definition.Locale)}'");
        }

        if (definition.LcCollate is not null)
        {
            options.Add($"LC_COLLATE = '{EscapeLiteral(definition.LcCollate)}'");
        }

        if (definition.LcCtype is not null)
        {
            options.Add($"LC_CTYPE = '{EscapeLiteral(definition.LcCtype)}'");
        }

        if (definition.Provider is { } provider)
        {
            options.Add($"PROVIDER = {ProviderSql(provider)}");
        }

        if (definition.IsDeterministic is { } deterministic)
        {
            options.Add($"DETERMINISTIC = {(deterministic ? "true" : "false")}");
        }

        if (definition.Rules is not null)
        {
            options.Add($"RULES = '{EscapeLiteral(definition.Rules)}'");
        }

        if (definition.Version is not null)
        {
            options.Add($"VERSION = '{EscapeLiteral(definition.Version)}'");
        }

        return sql.AppendJoin(", ", options).Append(')').ToString();
    }

    private static string ProviderSql(BlueTuskCollationProvider provider) => provider switch
    {
        BlueTuskCollationProvider.Libc => "libc",
        BlueTuskCollationProvider.Icu => "icu",
        BlueTuskCollationProvider.Builtin => "builtin",
        _ => throw new InvalidOperationException($"Unknown PostgreSQL collation provider '{provider}'."),
    };

    private void Generate(
        CreateBlueTuskCollationFromOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.SourceName);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("CREATE COLLATION ");
        if (operation.IfNotExists)
        {
            builder.Append("IF NOT EXISTS ");
        }

        builder.Append(helper.DelimitIdentifier(operation.Name, operation.Schema))
            .Append(" FROM ")
            .Append(helper.DelimitIdentifier(operation.SourceName, operation.SourceSchema));
        EndStatement(builder);
    }

    private void Generate(
        RenameBlueTuskCollationOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        var helper = Dependencies.SqlGenerationHelper;
        var currentSchema = operation.Schema;
        if (!string.Equals(operation.Schema, operation.NewSchema, StringComparison.Ordinal))
        {
            if (operation.NewSchema is null)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL collation '{operation.Schema}.{operation.Name}' cannot be moved to an unspecified schema.");
            }

            builder.Append("ALTER COLLATION ")
                .Append(helper.DelimitIdentifier(operation.Name, currentSchema))
                .Append(" SET SCHEMA ")
                .Append(helper.DelimitIdentifier(operation.NewSchema));
            EndStatement(builder);
            currentSchema = operation.NewSchema;
        }

        if (!string.Equals(operation.Name, operation.NewName, StringComparison.Ordinal))
        {
            builder.Append("ALTER COLLATION ")
                .Append(helper.DelimitIdentifier(operation.Name, currentSchema))
                .Append(" RENAME TO ")
                .Append(helper.DelimitIdentifier(operation.NewName));
            EndStatement(builder);
        }
    }

    private void Generate(
        RefreshBlueTuskCollationVersionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        builder.Append("ALTER COLLATION ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema))
            .Append(" REFRESH VERSION");
        EndStatement(builder);
    }

    private void Generate(
        DropBlueTuskCollationOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        builder.Append("DROP COLLATION ");
        if (operation.IfExists)
        {
            builder.Append("IF EXISTS ");
        }

        builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema))
            .Append(operation.Cascade ? " CASCADE" : " RESTRICT");
        EndStatement(builder);
    }

    private void Generate(
        CreateBlueTuskTablespaceOperation operation,
        MigrationCommandListBuilder builder)
    {
        BlueTuskTablespaceMetadata.Validate(operation.Definition);
        var definition = BlueTuskTablespaceMetadata.Normalize(operation.Definition);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("CREATE TABLESPACE ")
            .Append(helper.DelimitIdentifier(definition.Name));
        if (definition.Owner is not null)
        {
            builder.Append(" OWNER ").Append(helper.DelimitIdentifier(definition.Owner));
        }

        builder.Append(" LOCATION '").Append(EscapeLiteral(definition.Location)).Append("'");
        AppendTablespaceOptions(builder, " WITH", definition.Options);
        EndSuppressedStatement(builder);
        if (definition.Comment is not null)
        {
            AppendTablespaceComment(builder, definition.Name, definition.Comment);
        }
    }

    private void Generate(
        AlterBlueTuskTablespaceOperation operation,
        MigrationCommandListBuilder builder)
    {
        BlueTuskTablespaceMetadata.Validate(operation.Definition);
        BlueTuskTablespaceMetadata.Validate(operation.OldDefinition);
        var definition = BlueTuskTablespaceMetadata.Normalize(operation.Definition);
        var oldDefinition = BlueTuskTablespaceMetadata.Normalize(operation.OldDefinition);
        if (!string.Equals(definition.Name, oldDefinition.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Rename a PostgreSQL tablespace with a rename operation.");
        }

        if (!BlueTuskTablespaceMetadata.LocationEquals(definition, oldDefinition))
        {
            throw new InvalidOperationException(
                $"Tablespace '{definition.Name}' cannot change its filesystem location in place.");
        }

        var helper = Dependencies.SqlGenerationHelper;
        var identifier = helper.DelimitIdentifier(definition.Name);
        if (!string.Equals(definition.Owner, oldDefinition.Owner, StringComparison.Ordinal))
        {
            builder.Append("ALTER TABLESPACE ").Append(identifier).Append(" OWNER TO ")
                .Append(definition.Owner is null
                    ? "CURRENT_USER"
                    : helper.DelimitIdentifier(definition.Owner));
            EndStatement(builder);
        }

        var oldOptions = oldDefinition.Options.ToDictionary(option => option.Name, StringComparer.Ordinal);
        var changedOptions = definition.Options.Where(option =>
                !oldOptions.TryGetValue(option.Name, out var oldOption) ||
                !string.Equals(option.Value, oldOption.Value, StringComparison.Ordinal))
            .ToArray();
        if (changedOptions.Length > 0)
        {
            builder.Append("ALTER TABLESPACE ").Append(identifier);
            AppendTablespaceOptions(builder, " SET", changedOptions);
            EndStatement(builder);
        }

        var currentOptions = definition.Options.Select(option => option.Name)
            .ToHashSet(StringComparer.Ordinal);
        var resetOptions = oldDefinition.Options.Where(option => !currentOptions.Contains(option.Name)).ToArray();
        if (resetOptions.Length > 0)
        {
            builder.Append("ALTER TABLESPACE ").Append(identifier).Append(" RESET (")
                .Append(string.Join(", ", resetOptions.Select(option => option.Name)))
                .Append(")");
            EndStatement(builder);
        }

        if (!string.Equals(definition.Comment, oldDefinition.Comment, StringComparison.Ordinal))
        {
            AppendTablespaceComment(builder, definition.Name, definition.Comment);
        }
    }

    private void Generate(
        RenameBlueTuskTablespaceOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("ALTER TABLESPACE ").Append(helper.DelimitIdentifier(operation.Name))
            .Append(" RENAME TO ").Append(helper.DelimitIdentifier(operation.NewName));
        EndStatement(builder);
    }

    private void Generate(
        DropBlueTuskTablespaceOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        builder.Append("DROP TABLESPACE ");
        if (operation.IfExists)
        {
            builder.Append("IF EXISTS ");
        }

        builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));
        EndSuppressedStatement(builder);
    }

    private static void AppendTablespaceOptions(
        MigrationCommandListBuilder builder,
        string keyword,
        IReadOnlyList<BlueTuskTablespaceOptionDefinition> options)
    {
        if (options.Count == 0)
        {
            return;
        }

        builder.Append(keyword).Append(" (");
        for (var index = 0; index < options.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(options[index].Name)
                .Append(" = '")
                .Append(EscapeLiteral(options[index].Value))
                .Append("'");
        }

        builder.Append(")");
    }

    private void AppendTablespaceComment(
        MigrationCommandListBuilder builder,
        string name,
        string? comment)
    {
        builder.Append("COMMENT ON TABLESPACE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(name))
            .Append(" IS ")
            .Append(comment is null ? "NULL" : $"'{EscapeLiteral(comment)}'");
        EndStatement(builder);
    }

    private void Generate(
        CreateBlueTuskViewOperation operation,
        MigrationCommandListBuilder builder)
    {
        BlueTuskViewMetadata.Validate(operation.Definition);
        AppendViewDefinition(builder, operation.Definition, replace: false);
        EndStatement(builder);
    }

    private void Generate(
        ReplaceBlueTuskViewOperation operation,
        MigrationCommandListBuilder builder)
    {
        BlueTuskViewAlterationPlanner.ValidateReplacement(operation.OldDefinition, operation.Definition);
        AppendViewDefinition(builder, operation.Definition, replace: true);
        EndStatement(builder);

        var name = Dependencies.SqlGenerationHelper.DelimitIdentifier(
            operation.Definition.Name,
            operation.Definition.Schema);
        if (operation.OldDefinition.SecurityBarrier && !operation.Definition.SecurityBarrier)
        {
            builder.Append("ALTER VIEW ").Append(name).Append(" SET (security_barrier=false)");
            EndStatement(builder);
        }

        if (operation.OldDefinition.SecurityInvoker && !operation.Definition.SecurityInvoker)
        {
            builder.Append("ALTER VIEW ").Append(name).Append(" SET (security_invoker=false)");
            EndStatement(builder);
        }

        if (operation.OldDefinition.CheckOption is not null && operation.Definition.CheckOption is null)
        {
            builder.Append("ALTER VIEW ").Append(name).Append(" RESET (check_option)");
            EndStatement(builder);
        }
    }

    private void Generate(
        CreateBlueTuskMaterializedViewOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskViewMetadata.Normalize(operation.Definition);
        BlueTuskViewMetadata.Validate(definition);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("CREATE MATERIALIZED VIEW ")
            .Append(helper.DelimitIdentifier(definition.Name, definition.Schema));
        AppendIdentifierList(builder, definition.Columns);
        builder.Append(" USING ").Append(helper.DelimitIdentifier(definition.AccessMethod));
        AppendStorageParameters(builder, definition.StorageParameters);
        if (definition.Tablespace is not null)
        {
            builder.Append(" TABLESPACE ").Append(helper.DelimitIdentifier(definition.Tablespace));
        }

        builder.Append(" AS ").Append(definition.QuerySql)
            .Append(definition.IsPopulated ? " WITH DATA" : " WITH NO DATA");
        EndStatement(builder);
    }

    private void Generate(
        AlterBlueTuskMaterializedViewOperation operation,
        MigrationCommandListBuilder builder)
    {
        BlueTuskViewAlterationPlanner.ValidateMaterializedAlteration(
            operation.OldDefinition,
            operation.Definition);
        var oldDefinition = BlueTuskViewMetadata.Normalize(operation.OldDefinition);
        var definition = BlueTuskViewMetadata.Normalize(operation.Definition);
        var helper = Dependencies.SqlGenerationHelper;
        var name = helper.DelimitIdentifier(definition.Name, definition.Schema);
        if (!string.Equals(oldDefinition.AccessMethod, definition.AccessMethod, StringComparison.Ordinal))
        {
            builder.Append("ALTER MATERIALIZED VIEW ").Append(name)
                .Append(" SET ACCESS METHOD ").Append(helper.DelimitIdentifier(definition.AccessMethod));
            EndStatement(builder);
        }

        if (!string.Equals(oldDefinition.Tablespace, definition.Tablespace, StringComparison.Ordinal))
        {
            builder.Append("ALTER MATERIALIZED VIEW ").Append(name)
                .Append(" SET TABLESPACE ")
                .Append(helper.DelimitIdentifier(definition.Tablespace ?? "pg_default"));
            EndStatement(builder);
        }

        var oldParameters = oldDefinition.StorageParameters.ToDictionary(
            parameter => parameter.Name,
            StringComparer.Ordinal);
        var parametersToSet = definition.StorageParameters
            .Where(parameter => !oldParameters.TryGetValue(parameter.Name, out var oldParameter) ||
                                !string.Equals(oldParameter.ValueSql, parameter.ValueSql, StringComparison.Ordinal))
            .ToArray();
        if (parametersToSet.Length > 0)
        {
            builder.Append("ALTER MATERIALIZED VIEW ").Append(name).Append(" SET ");
            AppendStorageParameterBody(builder, parametersToSet);
            EndStatement(builder);
        }

        var parameterNames = definition.StorageParameters.Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        var parametersToReset = oldDefinition.StorageParameters
            .Where(parameter => !parameterNames.Contains(parameter.Name))
            .Select(parameter => parameter.Name)
            .ToArray();
        if (parametersToReset.Length > 0)
        {
            builder.Append("ALTER MATERIALIZED VIEW ").Append(name).Append(" RESET (");
            for (var index = 0; index < parametersToReset.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(helper.DelimitIdentifier(parametersToReset[index]));
            }

            builder.Append(")");
            EndStatement(builder);
        }

        if (oldDefinition.IsPopulated != definition.IsPopulated)
        {
            AppendRefreshMaterializedView(builder, definition.Name, definition.Schema, false, definition.IsPopulated);
            EndStatement(builder);
        }
    }

    private void Generate(
        DropBlueTuskViewOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        builder.Append(operation.Kind == BlueTuskViewKind.View ? "DROP VIEW " : "DROP MATERIALIZED VIEW ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema));
        EndStatement(builder);
    }

    private void Generate(
        RenameBlueTuskViewOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        var helper = Dependencies.SqlGenerationHelper;
        var keyword = operation.Kind == BlueTuskViewKind.View ? "VIEW" : "MATERIALIZED VIEW";
        var currentSchema = operation.Schema;
        if (!string.Equals(operation.Schema, operation.NewSchema, StringComparison.Ordinal))
        {
            if (operation.NewSchema is null)
            {
                throw new InvalidOperationException("A PostgreSQL view cannot be moved to an unspecified schema.");
            }

            builder.Append("ALTER ").Append(keyword).Append(" ")
                .Append(helper.DelimitIdentifier(operation.Name, currentSchema))
                .Append(" SET SCHEMA ").Append(helper.DelimitIdentifier(operation.NewSchema));
            EndStatement(builder);
            currentSchema = operation.NewSchema;
        }

        if (!string.Equals(operation.Name, operation.NewName, StringComparison.Ordinal))
        {
            builder.Append("ALTER ").Append(keyword).Append(" ")
                .Append(helper.DelimitIdentifier(operation.Name, currentSchema))
                .Append(" RENAME TO ").Append(helper.DelimitIdentifier(operation.NewName));
            EndStatement(builder);
        }
    }

    private void Generate(
        RefreshBlueTuskMaterializedViewOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        if (operation.Concurrently && !operation.WithData)
        {
            throw new InvalidOperationException(
                "PostgreSQL cannot refresh a materialized view CONCURRENTLY WITH NO DATA.");
        }

        AppendRefreshMaterializedView(
            builder,
            operation.Name,
            operation.Schema,
            operation.Concurrently,
            operation.WithData);
        EndStatement(builder);
    }

    private void AppendViewDefinition(
        MigrationCommandListBuilder builder,
        BlueTuskViewDefinition definition,
        bool replace)
    {
        definition = BlueTuskViewMetadata.Normalize(definition);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append(replace ? "CREATE OR REPLACE " : "CREATE ");
        if (definition.IsRecursive)
        {
            builder.Append("RECURSIVE ");
        }

        builder.Append("VIEW ").Append(helper.DelimitIdentifier(definition.Name, definition.Schema));
        AppendIdentifierList(builder, definition.Columns);
        var optionCount = (definition.SecurityBarrier ? 1 : 0) +
                          (definition.SecurityInvoker ? 1 : 0) +
                          (definition.CheckOption is null ? 0 : 1);
        if (optionCount > 0)
        {
            builder.Append(" WITH (");
            var needsSeparator = false;
            if (definition.SecurityBarrier)
            {
                builder.Append("security_barrier=true");
                needsSeparator = true;
            }

            if (definition.SecurityInvoker)
            {
                if (needsSeparator)
                {
                    builder.Append(", ");
                }

                builder.Append("security_invoker=true");
                needsSeparator = true;
            }

            if (definition.CheckOption is not null)
            {
                if (needsSeparator)
                {
                    builder.Append(", ");
                }

                builder.Append("check_option=")
                    .Append(definition.CheckOption == BlueTuskViewCheckOption.Local ? "local" : "cascaded");
            }

            builder.Append(")");
        }

        builder.Append(" AS ").Append(definition.QuerySql);
    }

    private void AppendIdentifierList(MigrationCommandListBuilder builder, IReadOnlyList<string> identifiers)
    {
        if (identifiers.Count == 0)
        {
            return;
        }

        builder.Append(" (");
        for (var index = 0; index < identifiers.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(identifiers[index]));
        }

        builder.Append(")");
    }

    private void AppendStorageParameters(
        MigrationCommandListBuilder builder,
        IReadOnlyList<BlueTuskMaterializedViewStorageParameterDefinition> parameters)
    {
        if (parameters.Count == 0)
        {
            return;
        }

        builder.Append(" WITH ");
        AppendStorageParameterBody(builder, parameters);
    }

    private void AppendStorageParameterBody(
        MigrationCommandListBuilder builder,
        IReadOnlyList<BlueTuskMaterializedViewStorageParameterDefinition> parameters)
    {
        builder.Append("(");
        for (var index = 0; index < parameters.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(parameters[index].Name))
                .Append("=").Append(parameters[index].ValueSql);
        }

        builder.Append(")");
    }

    private void AppendRefreshMaterializedView(
        MigrationCommandListBuilder builder,
        string name,
        string? schema,
        bool concurrently,
        bool withData)
    {
        builder.Append("REFRESH MATERIALIZED VIEW ");
        if (concurrently)
        {
            builder.Append("CONCURRENTLY ");
        }

        builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(name, schema))
            .Append(withData ? " WITH DATA" : " WITH NO DATA");
    }

    private void Generate(
        CreateBlueTuskRoutineOperation operation,
        MigrationCommandListBuilder builder)
    {
        BlueTuskRoutineMetadata.Validate(operation.Definition);
        AppendRoutineDefinition(builder, operation.Definition, replace: false);
        EndStatement(builder);
    }

    private void Generate(
        ReplaceBlueTuskRoutineOperation operation,
        MigrationCommandListBuilder builder)
    {
        BlueTuskRoutineAlterationPlanner.ValidateReplacement(operation.OldDefinition, operation.Definition);
        AppendRoutineDefinition(builder, operation.Definition, replace: true);
        EndStatement(builder);
    }

    private void Generate(
        DropBlueTuskRoutineOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentNullException.ThrowIfNull(operation.IdentityArgumentsSql);
        builder.Append(operation.Kind == BlueTuskRoutineKind.Function ? "DROP FUNCTION " : "DROP PROCEDURE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema))
            .Append("(").Append(operation.IdentityArgumentsSql).Append(")");
        EndStatement(builder);
    }

    private void Generate(
        RenameBlueTuskRoutineOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        ArgumentNullException.ThrowIfNull(operation.IdentityArgumentsSql);
        var helper = Dependencies.SqlGenerationHelper;
        var keyword = operation.Kind == BlueTuskRoutineKind.Function ? "FUNCTION" : "PROCEDURE";
        var currentSchema = operation.Schema;
        if (!string.Equals(operation.Schema, operation.NewSchema, StringComparison.Ordinal))
        {
            if (operation.NewSchema is null)
            {
                throw new InvalidOperationException("A PostgreSQL routine cannot be moved to an unspecified schema.");
            }

            builder.Append("ALTER ").Append(keyword).Append(" ")
                .Append(helper.DelimitIdentifier(operation.Name, currentSchema))
                .Append("(").Append(operation.IdentityArgumentsSql).Append(")")
                .Append(" SET SCHEMA ").Append(helper.DelimitIdentifier(operation.NewSchema));
            EndStatement(builder);
            currentSchema = operation.NewSchema;
        }

        if (!string.Equals(operation.Name, operation.NewName, StringComparison.Ordinal))
        {
            builder.Append("ALTER ").Append(keyword).Append(" ")
                .Append(helper.DelimitIdentifier(operation.Name, currentSchema))
                .Append("(").Append(operation.IdentityArgumentsSql).Append(")")
                .Append(" RENAME TO ").Append(helper.DelimitIdentifier(operation.NewName));
            EndStatement(builder);
        }
    }

    private static void AppendRoutineDefinition(
        MigrationCommandListBuilder builder,
        BlueTuskRoutineDefinition definition,
        bool replace)
    {
        var sql = BlueTuskRoutineMetadata.Normalize(definition).CreateOrReplaceSql;
        if (!replace)
        {
            const string prefix = "CREATE OR REPLACE ";
            sql = $"CREATE {sql[prefix.Length..]}";
        }

        builder.Append(sql);
    }

    private void Generate(
        CreateBlueTuskEnumTypeOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = operation.Definition;
        BlueTuskUserDefinedTypeMetadata.Validate(definition);
        builder
            .Append("CREATE TYPE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Name, definition.Schema))
            .Append(" AS ENUM (");
        for (var index = 0; index < definition.Labels.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            AppendStringLiteral(builder, definition.Labels[index]);
        }

        builder.Append(")");
        EndStatement(builder);
    }

    private void Generate(
        AlterBlueTuskEnumTypeOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = operation.Definition;
        var typeName = Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Name, definition.Schema);
        foreach (var change in BlueTuskUserDefinedTypeAlterationPlanner.PlanEnum(
                     operation.OldDefinition,
                     definition))
        {
            builder.Append("ALTER TYPE ").Append(typeName);
            if (change.Kind == EnumValueChangeKind.Rename)
            {
                builder.Append(" RENAME VALUE ");
                AppendStringLiteral(builder, change.Value);
                builder.Append(" TO ");
                AppendStringLiteral(builder, change.NewValue!);
                EndStatement(builder);
            }
            else
            {
                builder.Append(" ADD VALUE ");
                AppendStringLiteral(builder, change.Value);
                if (change.Neighbor is not null)
                {
                    builder.Append(change.Before ? " BEFORE " : " AFTER ");
                    AppendStringLiteral(builder, change.Neighbor);
                }

                EndIndexStatement(builder, suppressTransaction: true);
            }
        }
    }

    private void Generate(
        DropBlueTuskEnumTypeOperation operation,
        MigrationCommandListBuilder builder)
    {
        builder.Append("DROP TYPE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema));
        EndStatement(builder);
    }

    private void Generate(
        CreateBlueTuskDomainTypeOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = operation.Definition;
        BlueTuskUserDefinedTypeMetadata.Validate(definition);
        builder
            .Append("CREATE DOMAIN ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Name, definition.Schema))
            .Append(" AS ")
            .Append(definition.BaseStoreType);
        if (definition.Collation is not null)
        {
            builder.Append(" COLLATE ");
            AppendQualifiedIdentifier(builder, definition.Collation);
        }

        if (definition.DefaultSql is not null)
        {
            builder.Append(" DEFAULT ").Append(definition.DefaultSql);
        }

        if (definition.IsNotNull)
        {
            builder.Append(" NOT NULL");
        }

        foreach (var constraint in definition.Constraints.Where(constraint => constraint.IsValidated))
        {
            builder.Append(" CONSTRAINT ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(constraint.Name))
                .Append(" CHECK (")
                .Append(constraint.CheckSql)
                .Append(")");
        }

        EndStatement(builder);
        foreach (var constraint in definition.Constraints.Where(constraint => !constraint.IsValidated))
        {
            GenerateAddDomainConstraint(definition, constraint, builder);
        }
    }

    private void Generate(
        AlterBlueTuskDomainTypeOperation operation,
        MigrationCommandListBuilder builder)
    {
        var oldDefinition = operation.OldDefinition;
        var definition = operation.Definition;
        BlueTuskUserDefinedTypeAlterationPlanner.ValidateDomain(oldDefinition, definition);
        var helper = Dependencies.SqlGenerationHelper;
        var typeName = helper.DelimitIdentifier(definition.Name, definition.Schema);
        if (!string.Equals(oldDefinition.DefaultSql, definition.DefaultSql, StringComparison.Ordinal))
        {
            builder.Append("ALTER DOMAIN ").Append(typeName)
                .Append(definition.DefaultSql is null ? " DROP DEFAULT" : " SET DEFAULT ");
            if (definition.DefaultSql is not null)
            {
                builder.Append(definition.DefaultSql);
            }

            EndStatement(builder);
        }

        if (oldDefinition.IsNotNull != definition.IsNotNull)
        {
            builder.Append("ALTER DOMAIN ").Append(typeName)
                .Append(definition.IsNotNull ? " SET NOT NULL" : " DROP NOT NULL");
            EndStatement(builder);
        }

        var oldByName = oldDefinition.Constraints.ToDictionary(constraint => constraint.Name, StringComparer.Ordinal);
        var newByName = definition.Constraints.ToDictionary(constraint => constraint.Name, StringComparer.Ordinal);
        var mappedNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var constraint in oldDefinition.Constraints)
        {
            if (newByName.ContainsKey(constraint.Name))
            {
                mappedNames[constraint.Name] = constraint.Name;
                usedTargets.Add(constraint.Name);
                continue;
            }

            var candidates = definition.Constraints.Where(candidate =>
                    !oldByName.ContainsKey(candidate.Name) &&
                    !usedTargets.Contains(candidate.Name) &&
                    DomainConstraintBodyEquals(constraint, candidate))
                .ToArray();
            if (candidates.Length == 1)
            {
                var renamed = candidates[0];
                builder.Append("ALTER DOMAIN ").Append(typeName)
                    .Append(" RENAME CONSTRAINT ").Append(helper.DelimitIdentifier(constraint.Name))
                    .Append(" TO ").Append(helper.DelimitIdentifier(renamed.Name));
                EndStatement(builder);
                mappedNames[constraint.Name] = renamed.Name;
                usedTargets.Add(renamed.Name);
            }
        }

        foreach (var constraint in oldDefinition.Constraints)
        {
            if (!mappedNames.TryGetValue(constraint.Name, out var targetName))
            {
                GenerateDropDomainConstraint(typeName, constraint.Name, builder);
                continue;
            }

            var target = newByName[targetName];
            if (!string.Equals(constraint.CheckSql, target.CheckSql, StringComparison.Ordinal) ||
                constraint.IsValidated && !target.IsValidated)
            {
                GenerateDropDomainConstraint(typeName, targetName, builder);
                GenerateAddDomainConstraint(definition, target, builder);
            }
            else if (!constraint.IsValidated && target.IsValidated)
            {
                builder.Append("ALTER DOMAIN ").Append(typeName)
                    .Append(" VALIDATE CONSTRAINT ").Append(helper.DelimitIdentifier(targetName));
                EndStatement(builder);
            }
        }

        var mappedTargetNames = mappedNames.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var constraint in definition.Constraints.Where(constraint => !mappedTargetNames.Contains(constraint.Name)))
        {
            GenerateAddDomainConstraint(definition, constraint, builder);
        }
    }

    private void Generate(
        DropBlueTuskDomainTypeOperation operation,
        MigrationCommandListBuilder builder)
    {
        builder.Append("DROP DOMAIN ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema));
        EndStatement(builder);
    }

    private void Generate(
        CreateBlueTuskCompositeTypeOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = operation.Definition;
        BlueTuskUserDefinedTypeMetadata.Validate(definition);
        builder.Append("CREATE TYPE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Name, definition.Schema))
            .Append(" AS (");
        for (var index = 0; index < definition.Attributes.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            AppendCompositeAttribute(builder, definition.Attributes[index]);
        }

        builder.Append(")");
        EndStatement(builder);
    }

    private void Generate(
        AlterBlueTuskCompositeTypeOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = operation.Definition;
        var helper = Dependencies.SqlGenerationHelper;
        var typeName = helper.DelimitIdentifier(definition.Name, definition.Schema);
        foreach (var change in BlueTuskUserDefinedTypeAlterationPlanner.PlanComposite(
                     operation.OldDefinition,
                     definition))
        {
            builder.Append("ALTER TYPE ").Append(typeName);
            switch (change.Kind)
            {
                case CompositeAttributeChangeKind.Rename:
                    builder.Append(" RENAME ATTRIBUTE ").Append(helper.DelimitIdentifier(change.Name))
                        .Append(" TO ").Append(helper.DelimitIdentifier(change.Attribute!.Name));
                    break;
                case CompositeAttributeChangeKind.Drop:
                    builder.Append(" DROP ATTRIBUTE ").Append(helper.DelimitIdentifier(change.Name));
                    break;
                case CompositeAttributeChangeKind.Add:
                    builder.Append(" ADD ATTRIBUTE ");
                    AppendCompositeAttribute(builder, change.Attribute!);
                    break;
                case CompositeAttributeChangeKind.Alter:
                    builder.Append(" ALTER ATTRIBUTE ").Append(helper.DelimitIdentifier(change.Name))
                        .Append(" TYPE ").Append(change.Attribute!.StoreType);
                    if (change.Attribute.Collation is not null)
                    {
                        builder.Append(" COLLATE ");
                        AppendQualifiedIdentifier(builder, change.Attribute.Collation);
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Unknown composite attribute change '{change.Kind}'.");
            }

            EndStatement(builder);
        }
    }

    private void Generate(
        DropBlueTuskCompositeTypeOperation operation,
        MigrationCommandListBuilder builder)
    {
        builder.Append("DROP TYPE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema));
        EndStatement(builder);
    }

    private void Generate(
        CreateBlueTuskRangeTypeOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = operation.Definition;
        BlueTuskUserDefinedTypeMetadata.Validate(definition);
        builder.Append("CREATE TYPE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Name, definition.Schema))
            .Append(" AS RANGE (SUBTYPE = ");
        AppendQualifiedIdentifier(builder, definition.Subtype);
        AppendRangeOption(builder, "SUBTYPE_OPCLASS", definition.SubtypeOperatorClass);
        AppendRangeOption(builder, "COLLATION", definition.Collation);
        AppendRangeOption(builder, "CANONICAL", definition.CanonicalFunction);
        AppendRangeOption(builder, "SUBTYPE_DIFF", definition.SubtypeDifferenceFunction);
        AppendRangeOption(builder, "MULTIRANGE_TYPE_NAME", definition.MultirangeType);
        builder.Append(")");
        EndStatement(builder);
    }

    private void Generate(
        DropBlueTuskRangeTypeOperation operation,
        MigrationCommandListBuilder builder)
    {
        builder.Append("DROP TYPE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema))
            .Append(" RESTRICT");
        EndStatement(builder);
    }

    private void Generate(
        RenameBlueTuskRangeTypeOperation operation,
        MigrationCommandListBuilder builder)
    {
        GenerateTypeRename(
            operation.MultirangeName,
            operation.MultirangeSchema,
            operation.NewMultirangeName,
            operation.NewMultirangeSchema,
            builder);
        GenerateTypeRename(
            operation.Name,
            operation.Schema,
            operation.NewName,
            operation.NewSchema,
            builder);
    }

    private void Generate(
        RenameBlueTuskUserDefinedTypeOperation operation,
        MigrationCommandListBuilder builder)
    {
        var helper = Dependencies.SqlGenerationHelper;
        var keyword = operation.Kind == BlueTuskUserDefinedTypeKind.Domain ? "DOMAIN" : "TYPE";
        var currentName = operation.Name;
        var currentSchema = operation.Schema;
        if (!string.Equals(operation.Schema, operation.NewSchema, StringComparison.Ordinal))
        {
            if (operation.NewSchema is null)
            {
                throw new InvalidOperationException("A PostgreSQL user-defined type cannot be moved to an unspecified schema.");
            }

            builder.Append("ALTER ").Append(keyword).Append(" ")
                .Append(helper.DelimitIdentifier(currentName, currentSchema))
                .Append(" SET SCHEMA ").Append(helper.DelimitIdentifier(operation.NewSchema));
            EndStatement(builder);
            currentSchema = operation.NewSchema;
        }

        if (!string.Equals(operation.Name, operation.NewName, StringComparison.Ordinal))
        {
            builder.Append("ALTER ").Append(keyword).Append(" ")
                .Append(helper.DelimitIdentifier(currentName, currentSchema))
                .Append(" RENAME TO ").Append(helper.DelimitIdentifier(operation.NewName));
            EndStatement(builder);
        }
    }

    private void GenerateTypeRename(
        string name,
        string? schema,
        string newName,
        string? newSchema,
        MigrationCommandListBuilder builder)
    {
        var helper = Dependencies.SqlGenerationHelper;
        var currentSchema = schema;
        if (!string.Equals(schema, newSchema, StringComparison.Ordinal))
        {
            if (newSchema is null)
            {
                throw new InvalidOperationException("A PostgreSQL type cannot be moved to an unspecified schema.");
            }

            builder.Append("ALTER TYPE ")
                .Append(helper.DelimitIdentifier(name, currentSchema))
                .Append(" SET SCHEMA ").Append(helper.DelimitIdentifier(newSchema));
            EndStatement(builder);
            currentSchema = newSchema;
        }

        if (!string.Equals(name, newName, StringComparison.Ordinal))
        {
            builder.Append("ALTER TYPE ")
                .Append(helper.DelimitIdentifier(name, currentSchema))
                .Append(" RENAME TO ").Append(helper.DelimitIdentifier(newName));
            EndStatement(builder);
        }
    }

    private void AppendRangeOption(
        MigrationCommandListBuilder builder,
        string keyword,
        BlueTuskQualifiedName? value)
    {
        if (value is null)
        {
            return;
        }

        builder.Append(", ").Append(keyword).Append(" = ");
        AppendQualifiedIdentifier(builder, value);
    }

    private void AppendQualifiedIdentifier(
        MigrationCommandListBuilder builder,
        BlueTuskQualifiedName value) =>
        builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(value.Name, value.Schema));

    private void GenerateAddDomainConstraint(
        BlueTuskDomainTypeDefinition domain,
        BlueTuskDomainConstraintDefinition constraint,
        MigrationCommandListBuilder builder)
    {
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("ALTER DOMAIN ")
            .Append(helper.DelimitIdentifier(domain.Name, domain.Schema))
            .Append(" ADD CONSTRAINT ").Append(helper.DelimitIdentifier(constraint.Name))
            .Append(" CHECK (").Append(constraint.CheckSql).Append(")");
        if (!constraint.IsValidated)
        {
            builder.Append(" NOT VALID");
        }

        EndStatement(builder);
    }

    private void GenerateDropDomainConstraint(
        string typeName,
        string constraintName,
        MigrationCommandListBuilder builder)
    {
        builder.Append("ALTER DOMAIN ").Append(typeName)
            .Append(" DROP CONSTRAINT ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(constraintName));
        EndStatement(builder);
    }

    private void AppendCompositeAttribute(
        MigrationCommandListBuilder builder,
        BlueTuskCompositeAttributeDefinition attribute)
    {
        builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(attribute.Name))
            .Append(" ").Append(attribute.StoreType);
        if (attribute.Collation is not null)
        {
            builder.Append(" COLLATE ");
            AppendQualifiedIdentifier(builder, attribute.Collation);
        }
    }

    private static bool DomainConstraintBodyEquals(
        BlueTuskDomainConstraintDefinition left,
        BlueTuskDomainConstraintDefinition right) =>
        string.Equals(left.CheckSql, right.CheckSql, StringComparison.Ordinal) &&
        left.IsValidated == right.IsValidated;

    private static void AppendStringLiteral(MigrationCommandListBuilder builder, string value) =>
        builder.Append("'").Append(EscapeLiteral(value)).Append("'");

    private void Generate(
        AddBlueTuskTableInheritanceOperation operation,
        MigrationCommandListBuilder builder)
    {
        var helper = Dependencies.SqlGenerationHelper;
        builder
            .Append("ALTER TABLE ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" INHERIT ")
            .Append(helper.DelimitIdentifier(operation.ParentTable, operation.ParentSchema));
        EndStatement(builder);
    }

    private void Generate(
        RemoveBlueTuskTableInheritanceOperation operation,
        MigrationCommandListBuilder builder)
    {
        var helper = Dependencies.SqlGenerationHelper;
        builder
            .Append("ALTER TABLE ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" NO INHERIT ")
            .Append(helper.DelimitIdentifier(operation.ParentTable, operation.ParentSchema));
        EndStatement(builder);
    }

    protected override void Generate(
        CreateTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        var foreignTable = operation[BlueTuskForeignDataMetadata.ForeignTableAnnotationName] is string foreignJson
            ? BlueTuskForeignDataMetadata.DeserializeForeignTable(foreignJson)
            : null;
        if (foreignTable is not null &&
            (operation.PrimaryKey is not null || operation.UniqueConstraints.Count > 0 ||
             operation.ForeignKeys.Count > 0))
        {
            throw new NotSupportedException(
                $"PostgreSQL foreign table '{operation.Schema}.{operation.Name}' can contain only CHECK and " +
                "NOT NULL constraints. Configure the EF entity as keyless and model remote uniqueness separately.");
        }

        builder
            .Append(foreignTable is null ? "CREATE TABLE " : "CREATE FOREIGN TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema))
            .AppendLine(" (");
        using (builder.Indent())
        {
            CreateTableColumns(operation, model, builder);
            CreateTableConstraints(operation, model, builder);
            builder.AppendLine();
        }

        builder.Append(")");
        if (foreignTable is not null)
        {
            if (operation[BlueTuskPartitionMetadata.AnnotationName] is not null)
            {
                throw new NotSupportedException(
                    "Use explicit partition migration operations when a foreign table is a partition.");
            }

            builder.Append(" SERVER ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(foreignTable.ServerName));
            AppendCreateForeignOptions(builder, foreignTable.Options);
        }
        else if (operation[BlueTuskPartitionMetadata.AnnotationName] is string serializedDefinition)
        {
            AppendPartitioningClause(builder, BlueTuskPartitionMetadata.Deserialize(serializedDefinition));
        }

        if (terminate)
        {
            EndStatement(builder);
        }
    }

    protected override void Generate(
        AlterTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        var currentForeign = operation[BlueTuskForeignDataMetadata.ForeignTableAnnotationName] as string;
        var previousForeign = operation.OldTable[BlueTuskForeignDataMetadata.ForeignTableAnnotationName] as string;
        if (currentForeign is not null || previousForeign is not null)
        {
            if (currentForeign is null || previousForeign is null)
            {
                throw new NotSupportedException(
                    $"PostgreSQL cannot convert table '{operation.Schema}.{operation.Name}' between local and " +
                    "foreign storage in place. Create an explicit replacement migration.");
            }

            GenerateForeignTableAlteration(
                operation,
                BlueTuskForeignDataMetadata.DeserializeForeignTable(previousForeign),
                BlueTuskForeignDataMetadata.DeserializeForeignTable(currentForeign),
                builder);
            return;
        }

        var current = operation[BlueTuskPartitionMetadata.AnnotationName] as string;
        var previous = operation.OldTable[BlueTuskPartitionMetadata.AnnotationName] as string;
        if (!string.Equals(current, previous, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"PostgreSQL cannot change partition strategy or keys for table '{operation.Schema}.{operation.Name}' in place. " +
                "Create an explicit data-preserving replacement migration.");
        }

        base.Generate(operation, model, builder);
    }

    protected override void Generate(
        DropTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        if (operation[BlueTuskForeignDataMetadata.ForeignTableAnnotationName] is null)
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        builder.Append("DROP FOREIGN TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema));
        if (terminate)
        {
            EndStatement(builder);
        }
    }

    private void GenerateForeignTableAlteration(
        AlterTableOperation operation,
        BlueTuskForeignTableDefinition oldDefinition,
        BlueTuskForeignTableDefinition definition,
        MigrationCommandListBuilder builder)
    {
        oldDefinition = BlueTuskForeignDataMetadata.Normalize(oldDefinition);
        definition = BlueTuskForeignDataMetadata.Normalize(definition);
        BlueTuskForeignDataMetadata.Validate(oldDefinition);
        BlueTuskForeignDataMetadata.Validate(definition);
        if (oldDefinition.ServerName != definition.ServerName)
        {
            throw new NotSupportedException(
                $"PostgreSQL cannot change the server of foreign table '{operation.Schema}.{operation.Name}' in " +
                "place. Create an explicit replacement migration.");
        }

        var helper = Dependencies.SqlGenerationHelper;
        var tableName = helper.DelimitIdentifier(operation.Name, operation.Schema);
        if (BlueTuskForeignDataMetadata.SerializeOptions(oldDefinition.Options) !=
            BlueTuskForeignDataMetadata.SerializeOptions(definition.Options))
        {
            builder.Append("ALTER FOREIGN TABLE ")
                .Append(tableName);
            AppendAlterForeignOptions(builder, oldDefinition.Options, definition.Options);
            EndStatement(builder);
        }

        var oldColumns = oldDefinition.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        foreach (var column in definition.Columns)
        {
            if (!oldColumns.TryGetValue(column.Name, out var oldColumn) ||
                BlueTuskForeignDataMetadata.SerializeOptions(oldColumn.Options) ==
                BlueTuskForeignDataMetadata.SerializeOptions(column.Options))
            {
                continue;
            }

            builder.Append("ALTER FOREIGN TABLE ")
                .Append(tableName)
                .Append(" ALTER COLUMN ")
                .Append(helper.DelimitIdentifier(column.Name));
            AppendAlterForeignOptions(builder, oldColumn.Options, column.Options);
            EndStatement(builder);
        }

        if (operation.OldTable.Comment != operation.Comment)
        {
            builder.Append("COMMENT ON FOREIGN TABLE ")
                .Append(tableName)
                .Append(" IS ");
            if (operation.Comment is null)
            {
                builder.Append("NULL");
            }
            else
            {
                AppendStringLiteral(builder, operation.Comment);
            }

            EndStatement(builder);
        }
    }

    protected override void Generate(
        CreateIndexOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        var helper = Dependencies.SqlGenerationHelper;
        var concurrently = operation[BlueTuskIndexAnnotations.IsConcurrent] as bool? == true;
        builder.Append("CREATE ");
        if (operation.IsUnique)
        {
            builder.Append("UNIQUE ");
        }

        builder.Append("INDEX ");
        if (concurrently)
        {
            builder.Append("CONCURRENTLY ");
        }

        builder
            .Append(helper.DelimitIdentifier(operation.Name))
            .Append(" ON ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema));

        if (operation[BlueTuskIndexAnnotations.Method] is string method)
        {
            builder.Append(" USING ");
            AppendQualifiedIdentifier(builder, method);
        }

        var operatorClasses = operation[BlueTuskIndexAnnotations.OperatorClasses] as string[];
        var collations = operation[BlueTuskIndexAnnotations.Collations] as string[];
        var nullSortOrders = operation[BlueTuskIndexAnnotations.NullSortOrders] as int[];
        var expressions = operation[BlueTuskIndexAnnotations.Expressions] as string[];
        builder.Append(" (");
        for (var index = 0; index < operation.Columns.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            if (expressions is { Length: > 0 } &&
                index < expressions.Length &&
                !string.IsNullOrEmpty(expressions[index]))
            {
                builder.Append("(").Append(expressions[index]).Append(")");
            }
            else
            {
                builder.Append(helper.DelimitIdentifier(operation.Columns[index]));
            }

            if (collations is { Length: > 0 } &&
                index < collations.Length &&
                !string.IsNullOrWhiteSpace(collations[index]))
            {
                builder.Append(" COLLATE ");
                AppendQualifiedIdentifier(builder, collations[index]);
            }

            if (operatorClasses is { Length: > 0 } &&
                index < operatorClasses.Length &&
                !string.IsNullOrWhiteSpace(operatorClasses[index]))
            {
                builder.Append(" ");
                AppendQualifiedIdentifier(builder, operatorClasses[index]);
            }

            if (IsDescending(operation, index))
            {
                builder.Append(" DESC");
            }

            if (nullSortOrders is { Length: > 0 } && index < nullSortOrders.Length)
            {
                builder.Append(nullSortOrders[index] switch
                {
                    (int)BlueTuskIndexNullSortOrder.Default => string.Empty,
                    (int)BlueTuskIndexNullSortOrder.NullsFirst => " NULLS FIRST",
                    (int)BlueTuskIndexNullSortOrder.NullsLast => " NULLS LAST",
                    _ => throw new InvalidOperationException(
                        $"Index '{operation.Name}' has an invalid null sort order at key {index}."),
                });
            }
        }

        builder.Append(")");

        if (operation[BlueTuskIndexAnnotations.IncludeProperties] is string[] includeProperties &&
            includeProperties.Length > 0)
        {
            builder.Append(" INCLUDE (");
            for (var index = 0; index < includeProperties.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(helper.DelimitIdentifier(includeProperties[index]));
            }

            builder.Append(")");
        }

        if (operation[BlueTuskIndexAnnotations.NullsDistinct] is bool nullsDistinct)
        {
            if (!operation.IsUnique)
            {
                throw new InvalidOperationException(
                    $"Index '{operation.Name}' configures null distinctness but is not unique.");
            }

            builder.Append(nullsDistinct ? " NULLS DISTINCT" : " NULLS NOT DISTINCT");
        }

        if (operation[BlueTuskIndexAnnotations.StorageParameters] is string serializedParameters)
        {
            var parameters = BlueTuskIndexAnnotations.DeserializeStorageParameters(serializedParameters);
            if (parameters.Count > 0)
            {
                builder.Append(" WITH (");
                var index = 0;
                foreach (var (name, value) in parameters)
                {
                    if (index++ > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(name).Append(" = ").Append(value);
                }

                builder.Append(")");
            }
        }

        if (!string.IsNullOrWhiteSpace(operation.Filter))
        {
            builder.Append(" WHERE ").Append(operation.Filter);
        }

        if (terminate)
        {
            EndIndexStatement(builder, concurrently);
        }
    }

    protected override void Generate(
        AlterColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        if (operation.ComputedColumnSql is not null)
        {
            throw new NotSupportedException("PostgreSQL generated-column alteration requires dropping and recreating the column.");
        }

        var helper = Dependencies.SqlGenerationHelper;
        var table = helper.DelimitIdentifier(operation.Table, operation.Schema);
        var column = helper.DelimitIdentifier(operation.Name);
        var columnType = GetColumnType(
            operation.Schema,
            operation.Table,
            operation.Name,
            operation,
            model);

        if (!string.Equals(columnType, operation.OldColumn.ColumnType, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(operation.Collation, operation.OldColumn.Collation, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER TABLE ").Append(table)
                .Append(" ALTER COLUMN ").Append(column)
                .Append(" TYPE ").Append(columnType);
            if (operation.Collation is not null)
            {
                builder.Append(" COLLATE ").Append(helper.DelimitIdentifier(operation.Collation));
            }

            EndStatement(builder);
        }

        if (operation.IsNullable != operation.OldColumn.IsNullable)
        {
            builder
                .Append("ALTER TABLE ").Append(table)
                .Append(" ALTER COLUMN ").Append(column)
                .Append(operation.IsNullable ? " DROP NOT NULL" : " SET NOT NULL");
            EndStatement(builder);
        }

        if (!Equals(operation.DefaultValue, operation.OldColumn.DefaultValue)
            || !string.Equals(operation.DefaultValueSql, operation.OldColumn.DefaultValueSql, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER TABLE ").Append(table)
                .Append(" ALTER COLUMN ").Append(column);
            if (operation.DefaultValueSql is not null)
            {
                builder.Append(" SET DEFAULT (").Append(operation.DefaultValueSql).Append(")");
            }
            else if (operation.DefaultValue is not null)
            {
                builder.Append(" SET DEFAULT ");
                DefaultValue(operation.DefaultValue, columnType, operation.Name, builder);
            }
            else
            {
                builder.Append(" DROP DEFAULT");
            }

            EndStatement(builder);
        }

        if (!string.Equals(operation.Comment, operation.OldColumn.Comment, StringComparison.Ordinal))
        {
            builder
                .Append("COMMENT ON COLUMN ").Append(table).Append(".").Append(column)
                .Append(" IS ")
                .Append(operation.Comment is null ? "NULL" : $"'{EscapeLiteral(operation.Comment)}'");
            EndStatement(builder);
        }
    }

    protected override void Generate(
        RenameColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" RENAME COLUMN ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" TO ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName));
        EndStatement(builder);
    }

    protected override void Generate(
        RenameIndexOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .Append("ALTER INDEX ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema))
            .Append(" RENAME TO ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName));
        EndStatement(builder);
    }

    protected override void Generate(
        DropIndexOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .Append("DROP INDEX ")
            .Append(operation[BlueTuskIndexAnnotations.IsConcurrent] as bool? == true ? "CONCURRENTLY " : string.Empty)
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema));
        if (terminate)
        {
            EndIndexStatement(
                builder,
                operation[BlueTuskIndexAnnotations.IsConcurrent] as bool? == true);
        }
    }

    protected override void Generate(
        RenameTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        var helper = Dependencies.SqlGenerationHelper;
        var name = operation.Name;
        if (operation.NewName is not null && !string.Equals(operation.Name, operation.NewName, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER TABLE ").Append(helper.DelimitIdentifier(operation.Name, operation.Schema))
                .Append(" RENAME TO ").Append(helper.DelimitIdentifier(operation.NewName));
            EndStatement(builder);
            name = operation.NewName;
        }

        if (operation.NewSchema is not null
            && !string.Equals(operation.Schema, operation.NewSchema, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER TABLE ").Append(helper.DelimitIdentifier(name, operation.Schema))
                .Append(" SET SCHEMA ").Append(helper.DelimitIdentifier(operation.NewSchema));
            EndStatement(builder);
        }
    }

    protected override void Generate(
        RenameSequenceOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);

        var helper = Dependencies.SqlGenerationHelper;
        var name = operation.Name;
        if (operation.NewName is not null && !string.Equals(operation.Name, operation.NewName, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER SEQUENCE ").Append(helper.DelimitIdentifier(operation.Name, operation.Schema))
                .Append(" RENAME TO ").Append(helper.DelimitIdentifier(operation.NewName));
            EndStatement(builder);
            name = operation.NewName;
        }

        if (operation.NewSchema is not null
            && !string.Equals(operation.Schema, operation.NewSchema, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER SEQUENCE ").Append(helper.DelimitIdentifier(name, operation.Schema))
                .Append(" SET SCHEMA ").Append(helper.DelimitIdentifier(operation.NewSchema));
            EndStatement(builder);
        }
    }

    protected override void Generate(
        EnsureSchemaOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        builder
            .Append("CREATE SCHEMA IF NOT EXISTS ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));
        EndStatement(builder);
    }

    protected override void Generate(
        DropSchemaOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        builder
            .Append("DROP SCHEMA ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));
        EndStatement(builder);
    }

    protected override void ColumnDefinition(
        string? schema,
        string table,
        string name,
        ColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        if (operation[BlueTuskForeignDataMetadata.ForeignColumnOptionsAnnotationName] is string serializedOptions)
        {
            var columnType = operation.ColumnType ?? GetColumnType(schema, table, name, operation, model);
            builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(name))
                .Append(" ")
                .Append(columnType);
            AppendCreateForeignOptions(builder, BlueTuskForeignDataMetadata.DeserializeOptions(serializedOptions));
            if (operation.Collation is not null)
            {
                builder.Append(" COLLATE ");
                AppendQualifiedIdentifier(builder, operation.Collation);
            }

            if (operation.ComputedColumnSql is not null)
            {
                builder.Append(" GENERATED ALWAYS AS (")
                    .Append(operation.ComputedColumnSql)
                    .Append(operation.IsStored == false ? ") VIRTUAL" : ") STORED");
                return;
            }

            builder.Append(operation.IsNullable ? " NULL" : " NOT NULL");
            DefaultValue(operation.DefaultValue, operation.DefaultValueSql, columnType, builder);
            return;
        }

        base.ColumnDefinition(schema, table, name, operation, model, builder);

        if (IsIdentityColumn(schema, table, name, operation, model))
        {
            builder.Append(" GENERATED BY DEFAULT AS IDENTITY");
        }
    }

    private static bool IsIdentityColumn(
        string? schema,
        string table,
        string name,
        ColumnOperation operation,
        IModel? model)
    {
        if (operation.DefaultValue is not null
            || operation.DefaultValueSql is not null
            || operation.ComputedColumnSql is not null)
        {
            return false;
        }

        var clrType = Nullable.GetUnderlyingType(operation.ClrType) ?? operation.ClrType;
        if (clrType != typeof(short) && clrType != typeof(int) && clrType != typeof(long))
        {
            return false;
        }

        return model?.GetRelationalModel()
            .FindTable(table, schema)
            ?.FindColumn(name)
            ?.PropertyMappings
            .Any(mapping =>
                mapping.Property.ValueGenerated == ValueGenerated.OnAdd
                && mapping.Property.IsPrimaryKey()) == true;
    }

    private void EndStatement(MigrationCommandListBuilder builder)
        => builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator).EndCommand();

    private void EndSuppressedStatement(MigrationCommandListBuilder builder)
        => builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator)
            .EndCommand(suppressTransaction: true);

    private void EndIndexStatement(MigrationCommandListBuilder builder, bool suppressTransaction)
        => builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator)
            .EndCommand(suppressTransaction: suppressTransaction);

    private static bool IsDescending(CreateIndexOperation operation, int index) =>
        operation.IsDescending is { Length: 0 } ||
        operation.IsDescending is { Length: > 0 } values && index < values.Length && values[index];

    private void AppendQualifiedIdentifier(MigrationCommandListBuilder builder, string identifier)
    {
        var parts = identifier.Split('.');
        for (var index = 0; index < parts.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(".");
            }

            builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(parts[index]));
        }
    }

    private void Generate(
        CreateBlueTuskPartitionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.ParentName);
        ArgumentNullException.ThrowIfNull(operation.Definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Definition.Name);
        builder
            .Append("CREATE TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(
                operation.Definition.Name,
                operation.Definition.Schema))
            .Append(" PARTITION OF ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(
                operation.ParentName,
                operation.ParentSchema))
            .Append(" ");
        AppendPartitionBound(builder, operation.Definition.Bound);
        if (operation.Definition.Partitioning is { } subpartitioning)
        {
            AppendPartitioningClause(builder, subpartitioning);
        }

        EndStatement(builder);
    }

    private void Generate(
        DropBlueTuskPartitionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        builder
            .Append("DROP TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema));
        EndStatement(builder);
    }

    private void Generate(
        AlterBlueTuskPartitionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        var helper = Dependencies.SqlGenerationHelper;
        var currentName = operation.Name;
        var currentSchema = operation.Schema;
        if (!string.Equals(operation.Name, operation.NewName, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER TABLE ")
                .Append(helper.DelimitIdentifier(currentName, currentSchema))
                .Append(" RENAME TO ")
                .Append(helper.DelimitIdentifier(operation.NewName));
            EndStatement(builder);
            currentName = operation.NewName;
        }

        if (operation.NewSchema is not null &&
            !string.Equals(operation.Schema, operation.NewSchema, StringComparison.Ordinal))
        {
            builder
                .Append("ALTER TABLE ")
                .Append(helper.DelimitIdentifier(currentName, currentSchema))
                .Append(" SET SCHEMA ")
                .Append(helper.DelimitIdentifier(operation.NewSchema));
            EndStatement(builder);
        }
    }

    private void Generate(
        AttachBlueTuskPartitionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.ParentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.PartitionName);
        ArgumentNullException.ThrowIfNull(operation.Bound);
        builder
            .Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(
                operation.ParentName,
                operation.ParentSchema))
            .Append(" ATTACH PARTITION ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(
                operation.PartitionName,
                operation.PartitionSchema))
            .Append(" ");
        AppendPartitionBound(builder, operation.Bound);
        EndStatement(builder);
    }

    private void Generate(
        DetachBlueTuskPartitionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.ParentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.PartitionName);
        builder
            .Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(
                operation.ParentName,
                operation.ParentSchema))
            .Append(" DETACH PARTITION ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(
                operation.PartitionName,
                operation.PartitionSchema));
        builder.Append(operation.Mode switch
        {
            BlueTuskPartitionDetachMode.Normal => string.Empty,
            BlueTuskPartitionDetachMode.Concurrently => " CONCURRENTLY",
            BlueTuskPartitionDetachMode.Finalize => " FINALIZE",
            _ => throw new InvalidOperationException($"Unknown partition detach mode '{operation.Mode}'."),
        });
        if (operation.Mode == BlueTuskPartitionDetachMode.Concurrently)
        {
            EndIndexStatement(builder, suppressTransaction: true);
        }
        else
        {
            EndStatement(builder);
        }
    }

    private void AppendPartitioningClause(
        MigrationCommandListBuilder builder,
        BlueTuskPartitioningDefinition definition)
    {
        BlueTuskPartitioningBuilder.ValidateDefinition(definition);
        builder.Append(" PARTITION BY ").Append(definition.Strategy switch
        {
            BlueTuskPartitionStrategy.Range => "RANGE",
            BlueTuskPartitionStrategy.List => "LIST",
            BlueTuskPartitionStrategy.Hash => "HASH",
            _ => throw new InvalidOperationException($"Unknown partition strategy '{definition.Strategy}'."),
        }).Append(" (");
        if (!string.IsNullOrWhiteSpace(definition.KeySql))
        {
            builder.Append(definition.KeySql);
        }
        else
        {
            for (var index = 0; index < definition.Keys.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                var key = definition.Keys[index];
                if (key.IsColumn)
                {
                    builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(key.Expression));
                }
                else
                {
                    builder.Append("(").Append(key.Expression).Append(")");
                }

                if (key.Collation is not null)
                {
                    builder.Append(" COLLATE ");
                    AppendQualifiedIdentifier(builder, key.Collation);
                }

                if (key.OperatorClass is not null)
                {
                    builder.Append(" ");
                    AppendQualifiedIdentifier(builder, key.OperatorClass);
                }
            }
        }

        builder.Append(")");
    }

    private static void AppendPartitionBound(
        MigrationCommandListBuilder builder,
        BlueTuskPartitionBound bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        switch (bound.Kind)
        {
            case BlueTuskPartitionBoundKind.Range:
                builder.Append("FOR VALUES FROM (")
                    .Append(string.Join(", ", bound.From))
                    .Append(") TO (")
                    .Append(string.Join(", ", bound.To))
                    .Append(")");
                break;
            case BlueTuskPartitionBoundKind.List:
                builder.Append("FOR VALUES IN (");
                for (var index = 0; index < bound.Values.Length; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(", ");
                    }

                    var tuple = bound.Values[index];
                    builder.Append(tuple.Length == 1 ? tuple[0] : $"({string.Join(", ", tuple)})");
                }

                builder.Append(")");
                break;
            case BlueTuskPartitionBoundKind.Hash:
                builder.Append("FOR VALUES WITH (MODULUS ")
                    .Append(bound.Modulus.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(", REMAINDER ")
                    .Append(bound.Remainder.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(")");
                break;
            case BlueTuskPartitionBoundKind.Default:
                builder.Append("DEFAULT");
                break;
            case BlueTuskPartitionBoundKind.Sql:
                builder.Append(bound.Sql!);
                break;
            default:
                throw new InvalidOperationException($"Unknown partition bound kind '{bound.Kind}'.");
        }
    }

    private void Generate(
        CreateBlueTuskRowSecurityPolicyOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        ArgumentNullException.ThrowIfNull(operation.Definition);
        BlueTuskRowLevelSecurityBuilder.ValidatePolicy(operation.Definition);
        var helper = Dependencies.SqlGenerationHelper;
        var policy = operation.Definition;
        builder
            .Append("CREATE POLICY ")
            .Append(helper.DelimitIdentifier(policy.Name))
            .Append(" ON ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" AS ")
            .Append(policy.Behavior switch
            {
                BlueTuskRowSecurityPolicyBehavior.Permissive => "PERMISSIVE",
                BlueTuskRowSecurityPolicyBehavior.Restrictive => "RESTRICTIVE",
                _ => throw new InvalidOperationException($"Unknown policy behavior '{policy.Behavior}'."),
            })
            .Append(" FOR ")
            .Append(policy.Command switch
            {
                BlueTuskRowSecurityPolicyCommand.All => "ALL",
                BlueTuskRowSecurityPolicyCommand.Select => "SELECT",
                BlueTuskRowSecurityPolicyCommand.Insert => "INSERT",
                BlueTuskRowSecurityPolicyCommand.Update => "UPDATE",
                BlueTuskRowSecurityPolicyCommand.Delete => "DELETE",
                _ => throw new InvalidOperationException($"Unknown policy command '{policy.Command}'."),
            })
            .Append(" TO ");
        for (var index = 0; index < policy.Roles.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            AppendPolicyRole(builder, policy.Roles[index]);
        }

        if (policy.UsingSql is not null)
        {
            builder.Append(" USING (").Append(policy.UsingSql).Append(")");
        }

        if (policy.WithCheckSql is not null)
        {
            builder.Append(" WITH CHECK (").Append(policy.WithCheckSql).Append(")");
        }

        EndStatement(builder);
    }

    private void Generate(
        DropBlueTuskRowSecurityPolicyOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        var helper = Dependencies.SqlGenerationHelper;
        builder
            .Append("DROP POLICY ")
            .Append(helper.DelimitIdentifier(operation.Name))
            .Append(" ON ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema));
        EndStatement(builder);
    }

    private void Generate(
        AlterBlueTuskRowSecurityPolicyOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        ArgumentNullException.ThrowIfNull(operation.Definition);
        BlueTuskRowLevelSecurityBuilder.ValidatePolicy(operation.Definition);
        var helper = Dependencies.SqlGenerationHelper;
        var policy = operation.Definition;
        builder
            .Append("ALTER POLICY ")
            .Append(helper.DelimitIdentifier(policy.Name))
            .Append(" ON ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" TO ");
        for (var index = 0; index < policy.Roles.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            AppendPolicyRole(builder, policy.Roles[index]);
        }

        if (policy.UsingSql is not null)
        {
            builder.Append(" USING (").Append(policy.UsingSql).Append(")");
        }

        if (policy.WithCheckSql is not null)
        {
            builder.Append(" WITH CHECK (").Append(policy.WithCheckSql).Append(")");
        }

        EndStatement(builder);
    }

    private void Generate(
        RenameBlueTuskRowSecurityPolicyOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        var helper = Dependencies.SqlGenerationHelper;
        builder
            .Append("ALTER POLICY ")
            .Append(helper.DelimitIdentifier(operation.Name))
            .Append(" ON ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" RENAME TO ")
            .Append(helper.DelimitIdentifier(operation.NewName));
        EndStatement(builder);
    }

    private void Generate(
        AlterBlueTuskRowLevelSecurityOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        if (operation.Enabled is null && operation.Forced is null)
        {
            throw new InvalidOperationException("A row-level security operation has no setting changes.");
        }

        var table = Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema);
        if (operation.Enabled is { } enabled)
        {
            builder
                .Append("ALTER TABLE ")
                .Append(table)
                .Append(enabled ? " ENABLE" : " DISABLE")
                .Append(" ROW LEVEL SECURITY");
            EndStatement(builder);
        }

        if (operation.Forced is { } forced)
        {
            builder
                .Append("ALTER TABLE ")
                .Append(table)
                .Append(forced ? " FORCE" : " NO FORCE")
                .Append(" ROW LEVEL SECURITY");
            EndStatement(builder);
        }
    }

    private void Generate(
        AddBlueTuskExclusionConstraintOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        var definition = operation.Definition;
        BlueTuskExclusionConstraintMetadata.Validate(definition);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("ALTER TABLE ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" ADD CONSTRAINT ")
            .Append(helper.DelimitIdentifier(definition.Name))
            .Append(" EXCLUDE USING ")
            .Append(helper.DelimitIdentifier(definition.IndexMethod))
            .Append(" (");
        for (var index = 0; index < definition.Elements.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            AppendExclusionElement(builder, definition.Elements[index]);
        }

        builder.Append(")");
        if (definition.IncludedColumns.Count > 0)
        {
            builder.Append(" INCLUDE (");
            for (var index = 0; index < definition.IncludedColumns.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(helper.DelimitIdentifier(definition.IncludedColumns[index]));
            }

            builder.Append(")");
        }

        if (definition.StorageParameters.Count > 0)
        {
            builder.Append(" WITH (");
            AppendExclusionParameters(builder, definition.StorageParameters);
            builder.Append(")");
        }

        if (definition.Tablespace is not null)
        {
            builder.Append(" USING INDEX TABLESPACE ")
                .Append(helper.DelimitIdentifier(definition.Tablespace));
        }

        if (definition.PredicateSql is not null)
        {
            builder.Append(" WHERE (").Append(definition.PredicateSql).Append(")");
        }

        if (definition.IsDeferrable)
        {
            builder.Append(definition.IsInitiallyDeferred
                ? " DEFERRABLE INITIALLY DEFERRED"
                : " DEFERRABLE INITIALLY IMMEDIATE");
        }

        EndStatement(builder);
    }

    private void Generate(
        DropBlueTuskExclusionConstraintOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("ALTER TABLE ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" DROP CONSTRAINT ")
            .Append(helper.DelimitIdentifier(operation.Name))
            .Append(" RESTRICT");
        EndStatement(builder);
    }

    private void Generate(
        RenameBlueTuskExclusionConstraintOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("ALTER TABLE ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" RENAME CONSTRAINT ")
            .Append(helper.DelimitIdentifier(operation.Name))
            .Append(" TO ")
            .Append(helper.DelimitIdentifier(operation.NewName));
        EndStatement(builder);
    }

    private void AppendExclusionElement(
        MigrationCommandListBuilder builder,
        BlueTuskExclusionElementDefinition element)
    {
        var helper = Dependencies.SqlGenerationHelper;
        if (element.IsPreformatted)
        {
            builder.Append(element.Expression);
        }
        else
        {
            builder.Append(element.IsColumn
                ? helper.DelimitIdentifier(element.Expression)
                : $"({element.Expression})");
            if (element.Collation is not null)
            {
                builder.Append(" COLLATE ")
                    .Append(helper.DelimitIdentifier(element.Collation, element.CollationSchema));
            }

            if (element.OperatorClass is not null)
            {
                builder.Append(" ")
                    .Append(helper.DelimitIdentifier(element.OperatorClass, element.OperatorClassSchema));
                if (element.OperatorClassParameters.Count > 0)
                {
                    builder.Append(" (");
                    AppendExclusionParameters(builder, element.OperatorClassParameters);
                    builder.Append(")");
                }
            }

            if (element.Descending)
            {
                builder.Append(" DESC");
            }

            builder.Append(element.NullSortOrder switch
            {
                BlueTuskExclusionNullSortOrder.Default => string.Empty,
                BlueTuskExclusionNullSortOrder.NullsFirst => " NULLS FIRST",
                BlueTuskExclusionNullSortOrder.NullsLast => " NULLS LAST",
                _ => throw new InvalidOperationException(
                    $"Unknown exclusion-constraint null sort order '{element.NullSortOrder}'."),
            });
        }

        builder.Append(" WITH ");
        if (element.OperatorSchema is null)
        {
            builder.Append(element.Operator);
        }
        else
        {
            builder.Append("OPERATOR(")
                .Append(helper.DelimitIdentifier(element.OperatorSchema))
                .Append(".")
                .Append(element.Operator)
                .Append(")");
        }
    }

    private static void AppendExclusionParameters(
        MigrationCommandListBuilder builder,
        IReadOnlyList<BlueTuskExclusionParameterDefinition> parameters)
    {
        for (var index = 0; index < parameters.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(parameters[index].Name)
                .Append(" = ")
                .Append(parameters[index].Value);
        }
    }

    private void Generate(CreateBlueTuskEventTriggerOperation operation, MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskEventTriggerMetadata.Normalize(operation.Definition);
        BlueTuskEventTriggerMetadata.Validate(definition);
        if (definition.Event == BlueTuskEventTriggerEvent.Login)
        {
            GenerateMinimumVersionGuarded(
                Array.Empty<string>(),
                170000,
                $"BlueTusk event trigger '{definition.Name}' uses the login event and requires PostgreSQL 17 or later.",
                "$BlueTuskEventTrigger$",
                builder);
        }

        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("CREATE EVENT TRIGGER ").Append(helper.DelimitIdentifier(definition.Name))
            .Append(" ON ").Append(EventTriggerEventSql(definition.Event));
        if (definition.Tags.Count > 0)
        {
            builder.Append(" WHEN TAG IN (");
            for (var index = 0; index < definition.Tags.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                AppendStringLiteral(builder, definition.Tags[index]);
            }

            builder.Append(")");
        }

        builder.Append(" EXECUTE FUNCTION ")
            .Append(helper.DelimitIdentifier(definition.Function.Name, definition.Function.Schema))
            .Append("()");
        EndStatement(builder);
        if (definition.EnabledMode != BlueTuskEventTriggerEnabledMode.Origin)
        {
            Generate(new AlterBlueTuskEventTriggerEnabledModeOperation
            {
                Name = definition.Name,
                EnabledMode = definition.EnabledMode,
            }, builder);
        }
    }

    private void Generate(DropBlueTuskEventTriggerOperation operation, MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        builder.Append("DROP EVENT TRIGGER ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" RESTRICT");
        EndStatement(builder);
    }

    private void Generate(RenameBlueTuskEventTriggerOperation operation, MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        builder.Append("ALTER EVENT TRIGGER ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" RENAME TO ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName));
        EndStatement(builder);
    }

    private void Generate(
        AlterBlueTuskEventTriggerEnabledModeOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        builder.Append("ALTER EVENT TRIGGER ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(operation.EnabledMode switch
            {
                BlueTuskEventTriggerEnabledMode.Origin => " ENABLE",
                BlueTuskEventTriggerEnabledMode.Disabled => " DISABLE",
                BlueTuskEventTriggerEnabledMode.Replica => " ENABLE REPLICA",
                BlueTuskEventTriggerEnabledMode.Always => " ENABLE ALWAYS",
                _ => throw new InvalidOperationException("Unknown PostgreSQL event-trigger enabled mode."),
            });
        EndStatement(builder);
    }

    private static string EventTriggerEventSql(BlueTuskEventTriggerEvent value) => value switch
    {
        BlueTuskEventTriggerEvent.DdlCommandStart => "ddl_command_start",
        BlueTuskEventTriggerEvent.DdlCommandEnd => "ddl_command_end",
        BlueTuskEventTriggerEvent.SqlDrop => "sql_drop",
        BlueTuskEventTriggerEvent.TableRewrite => "table_rewrite",
        BlueTuskEventTriggerEvent.Login => "login",
        _ => throw new InvalidOperationException("Unknown PostgreSQL event-trigger event."),
    };

    private void Generate(CreateBlueTuskTriggerOperation operation, MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        var definition = operation.Definition;
        BlueTuskTriggerMetadata.Validate(definition);
        if (operation.OrReplace && definition.IsConstraint)
        {
            throw new InvalidOperationException("PostgreSQL cannot replace a constraint trigger in place.");
        }

        if (definition.CanonicalCreateSql is not null)
        {
            var sql = definition.CanonicalCreateSql.Trim().TrimEnd(';');
            if (operation.OrReplace)
            {
                const string prefix = "CREATE TRIGGER ";
                if (!sql.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "A canonical trigger definition must begin with CREATE TRIGGER to use OR REPLACE.");
                }

                sql = "CREATE OR REPLACE TRIGGER " + sql[prefix.Length..];
            }

            builder.Append(sql);
            EndStatement(builder);
        }
        else
        {
            AppendStructuredTrigger(operation, builder);
        }

        if (definition.EnabledMode != BlueTuskTriggerEnabledMode.Origin)
        {
            AppendTriggerEnabledMode(
                builder,
                operation.Table,
                operation.Schema,
                definition.Name,
                definition.EnabledMode);
            EndStatement(builder);
        }

        if (definition.ExtensionDependency is not null)
        {
            var helper = Dependencies.SqlGenerationHelper;
            builder.Append("ALTER TRIGGER ")
                .Append(helper.DelimitIdentifier(definition.Name))
                .Append(" ON ")
                .Append(helper.DelimitIdentifier(operation.Table, operation.Schema))
                .Append(" DEPENDS ON EXTENSION ")
                .Append(helper.DelimitIdentifier(definition.ExtensionDependency));
            EndStatement(builder);
        }
    }

    private void AppendStructuredTrigger(
        CreateBlueTuskTriggerOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = operation.Definition;
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("CREATE ");
        if (operation.OrReplace)
        {
            builder.Append("OR REPLACE ");
        }

        if (definition.IsConstraint)
        {
            builder.Append("CONSTRAINT ");
        }

        builder.Append("TRIGGER ")
            .Append(helper.DelimitIdentifier(definition.Name))
            .Append(" ")
            .Append(definition.Timing switch
            {
                BlueTuskTriggerTiming.Before => "BEFORE",
                BlueTuskTriggerTiming.After => "AFTER",
                BlueTuskTriggerTiming.InsteadOf => "INSTEAD OF",
                _ => throw new InvalidOperationException($"Unknown trigger timing '{definition.Timing}'."),
            })
            .Append(" ");
        for (var index = 0; index < definition.Events.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(" OR ");
            }

            var triggerEvent = definition.Events[index];
            builder.Append(triggerEvent.Kind switch
            {
                BlueTuskTriggerEventKind.Insert => "INSERT",
                BlueTuskTriggerEventKind.Update => "UPDATE",
                BlueTuskTriggerEventKind.Delete => "DELETE",
                BlueTuskTriggerEventKind.Truncate => "TRUNCATE",
                _ => throw new InvalidOperationException($"Unknown trigger event '{triggerEvent.Kind}'."),
            });
            if (triggerEvent.UpdateColumns.Count > 0)
            {
                builder.Append(" OF ");
                for (var columnIndex = 0; columnIndex < triggerEvent.UpdateColumns.Count; columnIndex++)
                {
                    if (columnIndex > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(helper.DelimitIdentifier(triggerEvent.UpdateColumns[columnIndex]));
                }
            }
        }

        builder.Append(" ON ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema));
        if (definition.ReferencedTable is not null)
        {
            builder.Append(" FROM ")
                .Append(helper.DelimitIdentifier(definition.ReferencedTable, definition.ReferencedTableSchema));
        }

        if (definition.IsConstraint)
        {
            builder.Append(definition.IsDeferrable
                ? definition.IsInitiallyDeferred
                    ? " DEFERRABLE INITIALLY DEFERRED"
                    : " DEFERRABLE INITIALLY IMMEDIATE"
                : " NOT DEFERRABLE");
        }

        if (definition.OldTransitionTable is not null || definition.NewTransitionTable is not null)
        {
            builder.Append(" REFERENCING");
            if (definition.OldTransitionTable is not null)
            {
                builder.Append(" OLD TABLE AS ")
                    .Append(helper.DelimitIdentifier(definition.OldTransitionTable));
            }

            if (definition.NewTransitionTable is not null)
            {
                builder.Append(" NEW TABLE AS ")
                    .Append(helper.DelimitIdentifier(definition.NewTransitionTable));
            }
        }

        builder.Append(definition.Orientation == BlueTuskTriggerOrientation.Row
            ? " FOR EACH ROW"
            : " FOR EACH STATEMENT");
        if (definition.WhenSql is not null)
        {
            builder.Append(" WHEN (").Append(definition.WhenSql).Append(")");
        }

        builder.Append(" EXECUTE FUNCTION ")
            .Append(helper.DelimitIdentifier(definition.FunctionName!, definition.FunctionSchema))
            .Append("(");
        for (var index = 0; index < definition.Arguments.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            AppendStringLiteral(builder, definition.Arguments[index]);
        }

        builder.Append(")");
        EndStatement(builder);
    }

    private void Generate(DropBlueTuskTriggerOperation operation, MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("DROP TRIGGER ")
            .Append(helper.DelimitIdentifier(operation.Name))
            .Append(" ON ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" RESTRICT");
        EndStatement(builder);
    }

    private void Generate(RenameBlueTuskTriggerOperation operation, MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("ALTER TRIGGER ")
            .Append(helper.DelimitIdentifier(operation.Name))
            .Append(" ON ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" RENAME TO ")
            .Append(helper.DelimitIdentifier(operation.NewName));
        EndStatement(builder);
    }

    private void Generate(
        AlterBlueTuskTriggerEnabledModeOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        AppendTriggerEnabledMode(
            builder,
            operation.Table,
            operation.Schema,
            operation.Name,
            operation.EnabledMode);
        EndStatement(builder);
    }

    private void AppendTriggerEnabledMode(
        MigrationCommandListBuilder builder,
        string table,
        string? schema,
        string name,
        BlueTuskTriggerEnabledMode mode)
    {
        var action = mode switch
        {
            BlueTuskTriggerEnabledMode.Origin => "ENABLE TRIGGER ",
            BlueTuskTriggerEnabledMode.Disabled => "DISABLE TRIGGER ",
            BlueTuskTriggerEnabledMode.Replica => "ENABLE REPLICA TRIGGER ",
            BlueTuskTriggerEnabledMode.Always => "ENABLE ALWAYS TRIGGER ",
            _ => throw new InvalidOperationException($"Unknown trigger enabled mode '{mode}'."),
        };
        builder.Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(table, schema))
            .Append(" ")
            .Append(action)
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(name));
    }

    private void Generate(CreateBlueTuskRuleOperation operation, MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        var definition = operation.Definition;
        BlueTuskRuleMetadata.Validate(definition);
        if (definition.CanonicalCreateSql is not null)
        {
            var sql = definition.CanonicalCreateSql.Trim().TrimEnd(';');
            if (operation.OrReplace)
            {
                const string prefix = "CREATE RULE ";
                if (!sql.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "A canonical rule definition must begin with CREATE RULE to use OR REPLACE.");
                }

                sql = "CREATE OR REPLACE RULE " + sql[prefix.Length..];
            }

            builder.Append(sql);
        }
        else
        {
            var helper = Dependencies.SqlGenerationHelper;
            builder.Append(operation.OrReplace ? "CREATE OR REPLACE RULE " : "CREATE RULE ")
                .Append(helper.DelimitIdentifier(definition.Name))
                .Append(" AS ON ")
                .Append(definition.Event.ToString().ToUpperInvariant())
                .Append(" TO ")
                .Append(helper.DelimitIdentifier(operation.Table, operation.Schema));
            if (definition.ConditionSql is not null)
            {
                builder.Append(" WHERE (").Append(definition.ConditionSql).Append(")");
            }

            builder.Append(definition.IsInstead ? " DO INSTEAD " : " DO ALSO ")
                .Append(definition.ActionSql!);
        }

        EndStatement(builder);
        if (definition.EnabledMode != BlueTuskRuleEnabledMode.Origin)
        {
            AppendRuleEnabledMode(builder, operation.Table, operation.Schema, definition.Name, definition.EnabledMode);
            EndStatement(builder);
        }
    }

    private void Generate(DropBlueTuskRuleOperation operation, MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("DROP RULE ")
            .Append(helper.DelimitIdentifier(operation.Name))
            .Append(" ON ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" RESTRICT");
        EndStatement(builder);
    }

    private void Generate(RenameBlueTuskRuleOperation operation, MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("ALTER RULE ")
            .Append(helper.DelimitIdentifier(operation.Name))
            .Append(" ON ")
            .Append(helper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" RENAME TO ")
            .Append(helper.DelimitIdentifier(operation.NewName));
        EndStatement(builder);
    }

    private void Generate(
        AlterBlueTuskRuleEnabledModeOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Table);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        AppendRuleEnabledMode(builder, operation.Table, operation.Schema, operation.Name, operation.EnabledMode);
        EndStatement(builder);
    }

    private void AppendRuleEnabledMode(
        MigrationCommandListBuilder builder,
        string table,
        string? schema,
        string name,
        BlueTuskRuleEnabledMode mode)
    {
        var action = mode switch
        {
            BlueTuskRuleEnabledMode.Origin => "ENABLE RULE ",
            BlueTuskRuleEnabledMode.Disabled => "DISABLE RULE ",
            BlueTuskRuleEnabledMode.Replica => "ENABLE REPLICA RULE ",
            BlueTuskRuleEnabledMode.Always => "ENABLE ALWAYS RULE ",
            _ => throw new InvalidOperationException($"Unknown rule enabled mode '{mode}'."),
        };
        builder.Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(table, schema))
            .Append(" ")
            .Append(action)
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(name));
    }

    private void Generate(
        CreateBlueTuskPublicationOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskPublicationMetadata.Normalize(operation.Definition);
        BlueTuskPublicationMetadata.Validate(definition);
        var sql = BuildCreatePublicationSql(definition);
        var minimumVersion = BlueTuskPublicationMetadata.MinimumServerVersion(definition);
        if (minimumVersion > 150000)
        {
            GenerateMinimumVersionGuarded(
                [sql],
                minimumVersion,
                $"BlueTusk publication '{definition.Name}' requires PostgreSQL {minimumVersion / 10000} or later.",
                "$BlueTuskPublication$",
                builder);
            return;
        }

        builder.Append(sql);
        EndStatement(builder);
    }

    private void Generate(
        AlterBlueTuskPublicationOperation operation,
        MigrationCommandListBuilder builder)
    {
        var oldDefinition = BlueTuskPublicationMetadata.Normalize(operation.OldDefinition);
        var definition = BlueTuskPublicationMetadata.Normalize(operation.Definition);
        BlueTuskPublicationMetadata.Validate(oldDefinition);
        BlueTuskPublicationMetadata.Validate(definition);
        if (!string.Equals(oldDefinition.Name, definition.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Publication alteration cannot also rename the publication.");
        }

        if (oldDefinition.AllTables != definition.AllTables ||
            oldDefinition.AllSequences != definition.AllSequences)
        {
            throw new InvalidOperationException(
                "Changing FOR ALL TABLES or FOR ALL SEQUENCES requires a destructive publication replacement.");
        }

        var statements = BuildAlterPublicationSql(oldDefinition, definition);
        var minimumVersion = Math.Max(
            BlueTuskPublicationMetadata.MinimumServerVersion(oldDefinition),
            BlueTuskPublicationMetadata.MinimumServerVersion(definition));
        if (minimumVersion > 150000)
        {
            GenerateMinimumVersionGuarded(
                statements,
                minimumVersion,
                $"BlueTusk publication '{definition.Name}' requires PostgreSQL {minimumVersion / 10000} or later.",
                "$BlueTuskPublication$",
                builder);
            return;
        }

        foreach (var statement in statements)
        {
            builder.Append(statement);
            EndStatement(builder);
        }
    }

    private void Generate(
        DropBlueTuskPublicationOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        builder.Append("DROP PUBLICATION ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" RESTRICT");
        EndStatement(builder);
    }

    private void Generate(
        RenameBlueTuskPublicationOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        builder.Append("ALTER PUBLICATION ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" RENAME TO ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName));
        EndStatement(builder);
    }

    private string BuildCreatePublicationSql(BlueTuskPublicationDefinition definition)
    {
        var sql = new StringBuilder("CREATE PUBLICATION ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Name));
        if (HasPublicationMembership(definition))
        {
            sql.Append(" FOR ");
            AppendPublicationMembership(sql, definition);
        }

        var options = BuildPublicationOptions(definition, includeDefaults: false);
        if (options.Count > 0)
        {
            sql.Append(" WITH (").AppendJoin(", ", options).Append(')');
        }

        return sql.ToString();
    }

    private List<string> BuildAlterPublicationSql(
        BlueTuskPublicationDefinition oldDefinition,
        BlueTuskPublicationDefinition definition)
    {
        var helper = Dependencies.SqlGenerationHelper;
        var name = helper.DelimitIdentifier(definition.Name);
        var statements = new List<string>();
        if (!PublicationMembershipEquals(oldDefinition, definition))
        {
            if (HasPublicationMembership(definition))
            {
                var sql = new StringBuilder("ALTER PUBLICATION ").Append(name).Append(" SET ");
                AppendPublicationMembership(sql, definition);
                statements.Add(sql.ToString());
            }
            else
            {
                var oldTables = oldDefinition.Tables.Where(table => !table.IsExcluded).ToArray();
                if (oldTables.Length > 0)
                {
                    var sql = new StringBuilder("ALTER PUBLICATION ").Append(name).Append(" DROP TABLE ");
                    AppendPublicationTableList(sql, oldTables, includeDetails: false);
                    statements.Add(sql.ToString());
                }

                if (oldDefinition.Schemas.Count > 0)
                {
                    statements.Add(new StringBuilder("ALTER PUBLICATION ").Append(name)
                        .Append(" DROP TABLES IN SCHEMA ")
                        .AppendJoin(", ", oldDefinition.Schemas.Select(helper.DelimitIdentifier))
                        .ToString());
                }
            }
        }

        var changedOptions = BuildChangedPublicationOptions(oldDefinition, definition);
        if (changedOptions.Count > 0)
        {
            statements.Add(new StringBuilder("ALTER PUBLICATION ").Append(name)
                .Append(" SET (").AppendJoin(", ", changedOptions).Append(')').ToString());
        }

        return statements;
    }

    private void AppendPublicationMembership(StringBuilder sql, BlueTuskPublicationDefinition definition)
    {
        if (definition.AllTables)
        {
            sql.Append("ALL TABLES");
            var excluded = definition.Tables.Where(table => table.IsExcluded).ToArray();
            if (excluded.Length > 0)
            {
                sql.Append(" EXCEPT (TABLE ");
                AppendPublicationTableList(sql, excluded, includeDetails: false);
                sql.Append(')');
            }

            if (definition.AllSequences)
            {
                sql.Append(", ALL SEQUENCES");
            }

            return;
        }

        if (definition.AllSequences)
        {
            sql.Append("ALL SEQUENCES");
            return;
        }

        var tables = definition.Tables.Where(table => !table.IsExcluded).ToArray();
        if (tables.Length > 0)
        {
            sql.Append("TABLE ");
            AppendPublicationTableList(sql, tables, includeDetails: true);
        }

        if (definition.Schemas.Count > 0)
        {
            if (tables.Length > 0)
            {
                sql.Append(", ");
            }

            sql.Append("TABLES IN SCHEMA ")
                .AppendJoin(", ", definition.Schemas.Select(Dependencies.SqlGenerationHelper.DelimitIdentifier));
        }
    }

    private void AppendPublicationTableList(
        StringBuilder sql,
        BlueTuskPublicationTableDefinition[] tables,
        bool includeDetails)
    {
        var helper = Dependencies.SqlGenerationHelper;
        for (var index = 0; index < tables.Length; index++)
        {
            if (index > 0)
            {
                sql.Append(", ");
            }

            var table = tables[index];
            if (!table.IncludeDescendants)
            {
                sql.Append("ONLY ");
            }

            sql.Append(helper.DelimitIdentifier(table.Name, table.Schema));
            if (!includeDetails)
            {
                continue;
            }

            if (table.Columns is not null)
            {
                sql.Append(" (").AppendJoin(", ", table.Columns.Select(helper.DelimitIdentifier)).Append(')');
            }

            if (table.RowFilterSql is not null)
            {
                sql.Append(" WHERE (").Append(table.RowFilterSql).Append(')');
            }
        }
    }

    private static bool HasPublicationMembership(BlueTuskPublicationDefinition definition) =>
        definition.AllTables || definition.AllSequences || definition.Tables.Count > 0 || definition.Schemas.Count > 0;

    private static bool PublicationMembershipEquals(
        BlueTuskPublicationDefinition left,
        BlueTuskPublicationDefinition right) =>
        left.AllTables == right.AllTables &&
        left.AllSequences == right.AllSequences &&
        left.Tables.SequenceEqual(right.Tables) &&
        left.Schemas.SequenceEqual(right.Schemas, StringComparer.Ordinal);

    private static List<string> BuildPublicationOptions(
        BlueTuskPublicationDefinition definition,
        bool includeDefaults)
    {
        var options = new List<string>();
        if (includeDefaults || definition.Operations != BlueTuskPublicationOperations.All)
        {
            options.Add($"publish = '{BuildPublishedOperations(definition.Operations)}'");
        }

        if (includeDefaults || definition.PublishViaPartitionRoot)
        {
            options.Add($"publish_via_partition_root = {definition.PublishViaPartitionRoot.ToString().ToLowerInvariant()}");
        }

        if (definition.GeneratedColumns != BlueTuskPublicationGeneratedColumns.None)
        {
            options.Add("publish_generated_columns = stored");
        }

        return options;
    }

    private static List<string> BuildChangedPublicationOptions(
        BlueTuskPublicationDefinition oldDefinition,
        BlueTuskPublicationDefinition definition)
    {
        var options = new List<string>();
        if (oldDefinition.Operations != definition.Operations)
        {
            options.Add($"publish = '{BuildPublishedOperations(definition.Operations)}'");
        }

        if (oldDefinition.PublishViaPartitionRoot != definition.PublishViaPartitionRoot)
        {
            options.Add($"publish_via_partition_root = {definition.PublishViaPartitionRoot.ToString().ToLowerInvariant()}");
        }

        if (oldDefinition.GeneratedColumns != definition.GeneratedColumns)
        {
            options.Add("publish_generated_columns = " +
                        (definition.GeneratedColumns == BlueTuskPublicationGeneratedColumns.Stored ? "stored" : "none"));
        }

        return options;
    }

    private static string BuildPublishedOperations(BlueTuskPublicationOperations operations)
    {
        var values = new List<string>();
        if (operations.HasFlag(BlueTuskPublicationOperations.Insert))
        {
            values.Add("insert");
        }

        if (operations.HasFlag(BlueTuskPublicationOperations.Update))
        {
            values.Add("update");
        }

        if (operations.HasFlag(BlueTuskPublicationOperations.Delete))
        {
            values.Add("delete");
        }

        if (operations.HasFlag(BlueTuskPublicationOperations.Truncate))
        {
            values.Add("truncate");
        }

        return string.Join(", ", values);
    }

    private void Generate(CreateBlueTuskOperatorOperation operation, MigrationCommandListBuilder builder) =>
        GenerateCreateOperator(operation.Definition, builder);

    private void Generate(ReplaceBlueTuskOperatorOperation operation, MigrationCommandListBuilder builder)
    {
        if (BlueTuskSchemaProgramMetadata.OperatorKey.Create(operation.OldDefinition) !=
            BlueTuskSchemaProgramMetadata.OperatorKey.Create(operation.Definition))
        {
            throw new InvalidOperationException("An operator replacement cannot change its identity.");
        }

        GenerateDropOperator(operation.OldDefinition, builder);
        GenerateCreateOperator(operation.Definition, builder);
    }

    private void Generate(DropBlueTuskOperatorOperation operation, MigrationCommandListBuilder builder) =>
        GenerateDropOperator(operation.Definition, builder);

    private void GenerateCreateOperator(
        BlueTuskOperatorDefinition value,
        MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskSchemaProgramMetadata.Normalize(value);
        BlueTuskSchemaProgramMetadata.Validate(definition);
        builder.Append("CREATE OPERATOR ");
        AppendOperatorName(builder, new BlueTuskOperatorName(definition.Name, definition.Schema), false);
        builder.Append(" (FUNCTION = ");
        AppendSchemaProgramName(builder, definition.Function);
        if (definition.LeftType is not null)
        {
            builder.Append(", LEFTARG = ").Append(definition.LeftType);
        }

        builder.Append(", RIGHTARG = ").Append(definition.RightType);
        AppendOperatorOption(builder, "COMMUTATOR", definition.Commutator);
        AppendOperatorOption(builder, "NEGATOR", definition.Negator);
        AppendNameOption(builder, "RESTRICT", definition.RestrictionFunction);
        AppendNameOption(builder, "JOIN", definition.JoinFunction);
        if (definition.SupportsHashJoins)
        {
            builder.Append(", HASHES");
        }

        if (definition.SupportsMergeJoins)
        {
            builder.Append(", MERGES");
        }

        builder.Append(")");
        EndStatement(builder);
    }

    private void GenerateDropOperator(
        BlueTuskOperatorDefinition value,
        MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskSchemaProgramMetadata.Normalize(value);
        BlueTuskSchemaProgramMetadata.Validate(definition);
        builder.Append("DROP OPERATOR ");
        AppendOperatorName(builder, new BlueTuskOperatorName(definition.Name, definition.Schema), false);
        builder.Append(" (").Append(definition.LeftType ?? "NONE").Append(", ")
            .Append(definition.RightType).Append(") RESTRICT");
        EndStatement(builder);
    }

    private void Generate(
        CreateBlueTuskOperatorFamilyOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskSchemaProgramMetadata.Normalize(operation.Definition);
        BlueTuskSchemaProgramMetadata.Validate(definition);
        builder.Append("CREATE OPERATOR FAMILY ");
        AppendSchemaProgramName(builder, new BlueTuskSchemaProgramName(definition.Name, definition.Schema));
        builder.Append(" USING ").Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.IndexMethod));
        EndStatement(builder);
        AppendAlterOperatorFamilyMembers("ADD", definition, builder);
    }

    private void Generate(
        AlterBlueTuskOperatorFamilyOperation operation,
        MigrationCommandListBuilder builder)
    {
        var oldDefinition = BlueTuskSchemaProgramMetadata.Normalize(operation.OldDefinition);
        var definition = BlueTuskSchemaProgramMetadata.Normalize(operation.Definition);
        BlueTuskSchemaProgramMetadata.Validate(oldDefinition);
        BlueTuskSchemaProgramMetadata.Validate(definition);
        if (oldDefinition.Name != definition.Name || oldDefinition.Schema != definition.Schema ||
            oldDefinition.IndexMethod != definition.IndexMethod)
        {
            throw new InvalidOperationException("An operator-family alteration cannot change its identity.");
        }

        var oldOperators = oldDefinition.Operators.ToDictionary(OperatorMemberKey);
        var operators = definition.Operators.ToDictionary(OperatorMemberKey);
        var oldFunctions = oldDefinition.Functions.ToDictionary(OperatorFunctionKey);
        var functions = definition.Functions.ToDictionary(OperatorFunctionKey);
        var drops = oldDefinition with
        {
            Operators = oldDefinition.Operators.Where(member =>
                    !operators.TryGetValue(OperatorMemberKey(member), out var current) || current != member)
                .ToArray(),
            Functions = oldDefinition.Functions.Where(member =>
                    !functions.TryGetValue(OperatorFunctionKey(member), out var current) ||
                    !OperatorFunctionsEqual(current, member))
                .ToArray(),
        };
        var additions = definition with
        {
            Operators = definition.Operators.Where(member =>
                    !oldOperators.TryGetValue(OperatorMemberKey(member), out var previous) || previous != member)
                .ToArray(),
            Functions = definition.Functions.Where(member =>
                    !oldFunctions.TryGetValue(OperatorFunctionKey(member), out var previous) ||
                    !OperatorFunctionsEqual(previous, member))
                .ToArray(),
        };
        AppendAlterOperatorFamilyMembers("DROP", drops, builder);
        AppendAlterOperatorFamilyMembers("ADD", additions, builder);
    }

    private void Generate(
        DropBlueTuskOperatorFamilyOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskSchemaProgramMetadata.Normalize(operation.Definition);
        BlueTuskSchemaProgramMetadata.Validate(definition);
        builder.Append("DROP OPERATOR FAMILY ");
        AppendSchemaProgramName(builder, new BlueTuskSchemaProgramName(definition.Name, definition.Schema));
        builder.Append(" USING ").Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.IndexMethod))
            .Append(" RESTRICT");
        EndStatement(builder);
    }

    private void AppendAlterOperatorFamilyMembers(
        string action,
        BlueTuskOperatorFamilyDefinition definition,
        MigrationCommandListBuilder builder)
    {
        if (definition.Operators.Count == 0 && definition.Functions.Count == 0)
        {
            return;
        }

        builder.Append("ALTER OPERATOR FAMILY ");
        AppendSchemaProgramName(builder, new BlueTuskSchemaProgramName(definition.Name, definition.Schema));
        builder.Append(" USING ").Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.IndexMethod))
            .Append(" ").Append(action).Append(" ");
        var first = true;
        foreach (var member in definition.Operators)
        {
            AppendListSeparator(builder, ref first);
            if (action == "ADD")
            {
                AppendOperatorMember(builder, member);
            }
            else
            {
                builder.Append("OPERATOR ").Append(member.StrategyNumber.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" (").Append(member.LeftType).Append(", ").Append(member.RightType).Append(")");
            }
        }

        foreach (var member in definition.Functions)
        {
            AppendListSeparator(builder, ref first);
            if (action == "ADD")
            {
                AppendOperatorFunction(builder, member);
            }
            else
            {
                builder.Append("FUNCTION ").Append(member.SupportNumber.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" (").Append(member.LeftType).Append(", ").Append(member.RightType).Append(")");
            }
        }

        EndStatement(builder);
    }

    private void Generate(
        CreateBlueTuskOperatorClassOperation operation,
        MigrationCommandListBuilder builder) => GenerateCreateOperatorClass(operation.Definition, builder);

    private void Generate(
        ReplaceBlueTuskOperatorClassOperation operation,
        MigrationCommandListBuilder builder)
    {
        if (BlueTuskSchemaProgramMetadata.OperatorClassKey.Create(operation.OldDefinition) !=
            BlueTuskSchemaProgramMetadata.OperatorClassKey.Create(operation.Definition))
        {
            throw new InvalidOperationException("An operator-class replacement cannot change its identity.");
        }

        GenerateDropOperatorClass(operation.OldDefinition, builder);
        GenerateCreateOperatorClass(operation.Definition, builder);
    }

    private void Generate(
        DropBlueTuskOperatorClassOperation operation,
        MigrationCommandListBuilder builder) => GenerateDropOperatorClass(operation.Definition, builder);

    private void GenerateCreateOperatorClass(
        BlueTuskOperatorClassDefinition value,
        MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskSchemaProgramMetadata.Normalize(value);
        BlueTuskSchemaProgramMetadata.Validate(definition);
        builder.Append("CREATE OPERATOR CLASS ");
        AppendSchemaProgramName(builder, new BlueTuskSchemaProgramName(definition.Name, definition.Schema));
        if (definition.IsDefault)
        {
            builder.Append(" DEFAULT");
        }

        builder.Append(" FOR TYPE ").Append(definition.DataType)
            .Append(" USING ").Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.IndexMethod));
        if (definition.Family is not null)
        {
            builder.Append(" FAMILY ");
            AppendSchemaProgramName(builder, definition.Family);
        }

        builder.Append(" AS ");
        var first = true;
        foreach (var member in definition.Operators)
        {
            AppendListSeparator(builder, ref first);
            AppendOperatorMember(builder, member);
        }

        foreach (var member in definition.Functions)
        {
            AppendListSeparator(builder, ref first);
            AppendOperatorFunction(builder, member);
        }

        if (definition.StorageType is not null)
        {
            AppendListSeparator(builder, ref first);
            builder.Append("STORAGE ").Append(definition.StorageType);
        }

        EndStatement(builder);
    }

    private void GenerateDropOperatorClass(
        BlueTuskOperatorClassDefinition value,
        MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskSchemaProgramMetadata.Normalize(value);
        BlueTuskSchemaProgramMetadata.Validate(definition);
        builder.Append("DROP OPERATOR CLASS ");
        AppendSchemaProgramName(builder, new BlueTuskSchemaProgramName(definition.Name, definition.Schema));
        builder.Append(" USING ").Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.IndexMethod))
            .Append(" RESTRICT");
        EndStatement(builder);
    }

    private void Generate(CreateBlueTuskCastOperation operation, MigrationCommandListBuilder builder) =>
        GenerateCreateCast(operation.Definition, builder);

    private void Generate(ReplaceBlueTuskCastOperation operation, MigrationCommandListBuilder builder)
    {
        if (BlueTuskSchemaProgramMetadata.CastKey.Create(operation.OldDefinition) !=
            BlueTuskSchemaProgramMetadata.CastKey.Create(operation.Definition))
        {
            throw new InvalidOperationException("A cast replacement cannot change its identity.");
        }

        GenerateDropCast(operation.OldDefinition, builder);
        GenerateCreateCast(operation.Definition, builder);
    }

    private void Generate(DropBlueTuskCastOperation operation, MigrationCommandListBuilder builder) =>
        GenerateDropCast(operation.Definition, builder);

    private void GenerateCreateCast(BlueTuskCastDefinition value, MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskSchemaProgramMetadata.Normalize(value);
        BlueTuskSchemaProgramMetadata.Validate(definition);
        builder.Append("CREATE CAST (").Append(definition.SourceType).Append(" AS ")
            .Append(definition.TargetType).Append(") ");
        switch (definition.Method)
        {
            case BlueTuskCastMethod.Function:
                builder.Append("WITH FUNCTION ");
                AppendSchemaProgramName(builder, definition.Function!.Function);
                AppendTypeList(builder, definition.Function.ArgumentTypes);
                break;
            case BlueTuskCastMethod.Binary:
                builder.Append("WITHOUT FUNCTION");
                break;
            case BlueTuskCastMethod.InOut:
                builder.Append("WITH INOUT");
                break;
            default:
                throw new InvalidOperationException("Unknown PostgreSQL cast method.");
        }

        if (definition.Context == BlueTuskCastContext.Assignment)
        {
            builder.Append(" AS ASSIGNMENT");
        }
        else if (definition.Context == BlueTuskCastContext.Implicit)
        {
            builder.Append(" AS IMPLICIT");
        }

        EndStatement(builder);
    }

    private void GenerateDropCast(BlueTuskCastDefinition value, MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskSchemaProgramMetadata.Normalize(value);
        BlueTuskSchemaProgramMetadata.Validate(definition);
        builder.Append("DROP CAST (").Append(definition.SourceType).Append(" AS ")
            .Append(definition.TargetType).Append(") RESTRICT");
        EndStatement(builder);
    }

    private void Generate(CreateBlueTuskAggregateOperation operation, MigrationCommandListBuilder builder) =>
        GenerateCreateAggregate(operation.Definition, false, builder);

    private void Generate(ReplaceBlueTuskAggregateOperation operation, MigrationCommandListBuilder builder)
    {
        if (BlueTuskSchemaProgramMetadata.AggregateKey.Create(operation.OldDefinition) !=
            BlueTuskSchemaProgramMetadata.AggregateKey.Create(operation.Definition))
        {
            throw new InvalidOperationException("An aggregate replacement cannot change its identity.");
        }

        GenerateCreateAggregate(operation.Definition, true, builder);
    }

    private void Generate(DropBlueTuskAggregateOperation operation, MigrationCommandListBuilder builder) =>
        GenerateDropAggregate(operation.Definition, builder);

    private void GenerateCreateAggregate(
        BlueTuskAggregateDefinition value,
        bool replace,
        MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskSchemaProgramMetadata.Normalize(value);
        BlueTuskSchemaProgramMetadata.Validate(definition);
        builder.Append(replace ? "CREATE OR REPLACE AGGREGATE " : "CREATE AGGREGATE ");
        AppendSchemaProgramName(builder, new BlueTuskSchemaProgramName(definition.Name, definition.Schema));
        builder.Append(" (").Append(string.IsNullOrEmpty(definition.IdentityArgumentsSql)
            ? "*"
            : definition.IdentityArgumentsSql).Append(") (");
        var first = true;
        AppendAggregateNameOption(builder, ref first, "SFUNC", definition.TransitionFunction);
        AppendAggregateValueOption(builder, ref first, "STYPE", definition.StateType);
        AppendAggregateIntegerOption(builder, ref first, "SSPACE", definition.StateSpace);
        AppendAggregateNameOption(builder, ref first, "FINALFUNC", definition.FinalFunction);
        AppendAggregateFlag(builder, ref first, "FINALFUNC_EXTRA", definition.FinalFunctionExtra);
        if (definition.FinalFunction is not null)
        {
            AppendAggregateValueOption(builder, ref first, "FINALFUNC_MODIFY",
                AggregateFinalModifySql(definition.FinalFunctionModify));
        }

        AppendAggregateNameOption(builder, ref first, "COMBINEFUNC", definition.CombineFunction);
        AppendAggregateNameOption(builder, ref first, "SERIALFUNC", definition.SerialFunction);
        AppendAggregateNameOption(builder, ref first, "DESERIALFUNC", definition.DeserialFunction);
        AppendAggregateLiteralOption(builder, ref first, "INITCOND", definition.InitialCondition);
        AppendAggregateNameOption(builder, ref first, "MSFUNC", definition.MovingTransitionFunction);
        AppendAggregateNameOption(builder, ref first, "MINVFUNC", definition.MovingInverseFunction);
        AppendAggregateValueOption(builder, ref first, "MSTYPE", definition.MovingStateType);
        AppendAggregateIntegerOption(builder, ref first, "MSSPACE", definition.MovingStateSpace);
        AppendAggregateNameOption(builder, ref first, "MFINALFUNC", definition.MovingFinalFunction);
        AppendAggregateFlag(builder, ref first, "MFINALFUNC_EXTRA", definition.MovingFinalFunctionExtra);
        if (definition.MovingFinalFunction is not null)
        {
            AppendAggregateValueOption(builder, ref first, "MFINALFUNC_MODIFY",
                AggregateFinalModifySql(definition.MovingFinalFunctionModify));
        }

        AppendAggregateLiteralOption(builder, ref first, "MINITCOND", definition.MovingInitialCondition);
        if (definition.SortOperator is not null)
        {
            AppendAggregateOptionPrefix(builder, ref first, "SORTOP");
            AppendOperatorName(builder, definition.SortOperator, false);
        }

        AppendAggregateFlag(builder, ref first, "HYPOTHETICAL",
            definition.Kind == BlueTuskAggregateKind.HypotheticalSet);
        AppendAggregateValueOption(builder, ref first, "PARALLEL", definition.ParallelSafety switch
        {
            BlueTuskAggregateParallelSafety.Safe => "SAFE",
            BlueTuskAggregateParallelSafety.Restricted => "RESTRICTED",
            _ => "UNSAFE",
        });
        builder.Append(")");
        EndStatement(builder);
    }

    private void GenerateDropAggregate(
        BlueTuskAggregateDefinition value,
        MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskSchemaProgramMetadata.Normalize(value);
        BlueTuskSchemaProgramMetadata.Validate(definition);
        builder.Append("DROP AGGREGATE ");
        AppendSchemaProgramName(builder, new BlueTuskSchemaProgramName(definition.Name, definition.Schema));
        builder.Append(" (").Append(string.IsNullOrEmpty(definition.IdentityArgumentsSql)
            ? "*"
            : definition.IdentityArgumentsSql).Append(") RESTRICT");
        EndStatement(builder);
    }

    private void AppendOperatorMember(
        MigrationCommandListBuilder builder,
        BlueTuskOperatorMemberDefinition member)
    {
        builder.Append("OPERATOR ").Append(member.StrategyNumber.ToString(
                System.Globalization.CultureInfo.InvariantCulture)).Append(" ");
        AppendOperatorName(builder, member.Operator, false);
        builder.Append(" (").Append(member.LeftType).Append(", ").Append(member.RightType).Append(")");
        if (member.Purpose == BlueTuskOperatorPurpose.OrderBy)
        {
            builder.Append(" FOR ORDER BY ");
            AppendSchemaProgramName(builder, member.SortFamily!);
        }
        else
        {
            builder.Append(" FOR SEARCH");
        }
    }

    private void AppendOperatorFunction(
        MigrationCommandListBuilder builder,
        BlueTuskOperatorFunctionDefinition member)
    {
        builder.Append("FUNCTION ").Append(member.SupportNumber.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            .Append(" (").Append(member.LeftType).Append(", ").Append(member.RightType).Append(") ");
        AppendSchemaProgramName(builder, member.Function);
        AppendTypeList(builder, member.ArgumentTypes);
    }

    private static void AppendTypeList(MigrationCommandListBuilder builder, IReadOnlyList<string> types)
    {
        builder.Append(" (");
        for (var index = 0; index < types.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(types[index]);
        }

        builder.Append(")");
    }

    private void AppendSchemaProgramName(MigrationCommandListBuilder builder, BlueTuskSchemaProgramName name) =>
        builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(name.Name, name.Schema));

    private void AppendOperatorName(
        MigrationCommandListBuilder builder,
        BlueTuskOperatorName name,
        bool wrapped)
    {
        if (wrapped)
        {
            builder.Append("OPERATOR(");
        }

        if (name.Schema is not null)
        {
            builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(name.Schema)).Append(".");
        }

        builder.Append(name.Name);
        if (wrapped)
        {
            builder.Append(")");
        }
    }

    private void AppendOperatorOption(
        MigrationCommandListBuilder builder,
        string option,
        BlueTuskOperatorName? value)
    {
        if (value is null)
        {
            return;
        }

        builder.Append(", ").Append(option).Append(" = ");
        AppendOperatorName(builder, value, value.Schema is not null);
    }

    private void AppendNameOption(
        MigrationCommandListBuilder builder,
        string option,
        BlueTuskSchemaProgramName? value)
    {
        if (value is null)
        {
            return;
        }

        builder.Append(", ").Append(option).Append(" = ");
        AppendSchemaProgramName(builder, value);
    }

    private static void AppendListSeparator(MigrationCommandListBuilder builder, ref bool first)
    {
        if (!first)
        {
            builder.Append(", ");
        }

        first = false;
    }

    private static (int Strategy, string LeftType, string RightType) OperatorMemberKey(
        BlueTuskOperatorMemberDefinition value) => (value.StrategyNumber, value.LeftType, value.RightType);

    private static (int Support, string LeftType, string RightType) OperatorFunctionKey(
        BlueTuskOperatorFunctionDefinition value) => (value.SupportNumber, value.LeftType, value.RightType);

    private static bool OperatorFunctionsEqual(
        BlueTuskOperatorFunctionDefinition left,
        BlueTuskOperatorFunctionDefinition right) =>
        left.SupportNumber == right.SupportNumber &&
        left.LeftType == right.LeftType &&
        left.RightType == right.RightType &&
        left.Function == right.Function &&
        left.ArgumentTypes.SequenceEqual(right.ArgumentTypes, StringComparer.Ordinal);

    private static string AggregateFinalModifySql(BlueTuskAggregateFinalFunctionModify value) => value switch
    {
        BlueTuskAggregateFinalFunctionModify.Shareable => "SHAREABLE",
        BlueTuskAggregateFinalFunctionModify.ReadWrite => "READ_WRITE",
        _ => "READ_ONLY",
    };

    private static void AppendAggregateOptionPrefix(
        MigrationCommandListBuilder builder,
        ref bool first,
        string option)
    {
        AppendListSeparator(builder, ref first);
        builder.Append(option).Append(" = ");
    }

    private void AppendAggregateNameOption(
        MigrationCommandListBuilder builder,
        ref bool first,
        string option,
        BlueTuskSchemaProgramName? value)
    {
        if (value is null)
        {
            return;
        }

        AppendAggregateOptionPrefix(builder, ref first, option);
        AppendSchemaProgramName(builder, value);
    }

    private static void AppendAggregateValueOption(
        MigrationCommandListBuilder builder,
        ref bool first,
        string option,
        string? value)
    {
        if (value is null)
        {
            return;
        }

        AppendAggregateOptionPrefix(builder, ref first, option);
        builder.Append(value);
    }

    private static void AppendAggregateIntegerOption(
        MigrationCommandListBuilder builder,
        ref bool first,
        string option,
        int? value)
    {
        if (value is null)
        {
            return;
        }

        AppendAggregateOptionPrefix(builder, ref first, option);
        builder.Append(value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AppendAggregateLiteralOption(
        MigrationCommandListBuilder builder,
        ref bool first,
        string option,
        string? value)
    {
        if (value is null)
        {
            return;
        }

        AppendAggregateOptionPrefix(builder, ref first, option);
        AppendStringLiteral(builder, value);
    }

    private static void AppendAggregateFlag(
        MigrationCommandListBuilder builder,
        ref bool first,
        string option,
        bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        AppendListSeparator(builder, ref first);
        builder.Append(option);
    }

    private void Generate(
        CreateBlueTuskForeignDataWrapperOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskForeignDataMetadata.Normalize(operation.Definition);
        BlueTuskForeignDataMetadata.Validate(definition);
        AppendForeignDataWrapperVersionCheck(definition, builder);
        builder.Append("CREATE FOREIGN DATA WRAPPER ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Name));
        AppendForeignDataWrapperFunction(builder, " HANDLER ", " NO HANDLER", definition.HandlerFunction);
        AppendForeignDataWrapperFunction(builder, " VALIDATOR ", " NO VALIDATOR", definition.ValidatorFunction);
        if (definition.ConnectionFunction is not null)
        {
            AppendForeignDataWrapperFunction(builder, " CONNECTION ", " NO CONNECTION",
                definition.ConnectionFunction);
        }

        AppendCreateForeignOptions(builder, definition.Options);
        EndStatement(builder);
    }

    private void Generate(
        AlterBlueTuskForeignDataWrapperOperation operation,
        MigrationCommandListBuilder builder)
    {
        var oldDefinition = BlueTuskForeignDataMetadata.Normalize(operation.OldDefinition);
        var definition = BlueTuskForeignDataMetadata.Normalize(operation.Definition);
        BlueTuskForeignDataMetadata.Validate(oldDefinition);
        BlueTuskForeignDataMetadata.Validate(definition);
        if (oldDefinition.Name != definition.Name)
        {
            throw new InvalidOperationException("A foreign-data wrapper alteration cannot also rename the wrapper.");
        }

        AppendForeignDataWrapperVersionCheck(oldDefinition, definition, builder);
        builder.Append("ALTER FOREIGN DATA WRAPPER ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Name));
        if (oldDefinition.HandlerFunction != definition.HandlerFunction)
        {
            AppendForeignDataWrapperFunction(builder, " HANDLER ", " NO HANDLER", definition.HandlerFunction);
        }

        if (oldDefinition.ValidatorFunction != definition.ValidatorFunction)
        {
            AppendForeignDataWrapperFunction(builder, " VALIDATOR ", " NO VALIDATOR",
                definition.ValidatorFunction);
        }

        if (oldDefinition.ConnectionFunction != definition.ConnectionFunction)
        {
            AppendForeignDataWrapperFunction(builder, " CONNECTION ", " NO CONNECTION",
                definition.ConnectionFunction);
        }

        AppendAlterForeignOptions(builder, oldDefinition.Options, definition.Options);
        EndStatement(builder);
    }

    private void Generate(
        DropBlueTuskForeignDataWrapperOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        builder.Append("DROP FOREIGN DATA WRAPPER ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" RESTRICT");
        EndStatement(builder);
    }

    private void Generate(
        RenameBlueTuskForeignDataWrapperOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        builder.Append("ALTER FOREIGN DATA WRAPPER ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" RENAME TO ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName));
        EndStatement(builder);
    }

    private void Generate(
        CreateBlueTuskForeignServerOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskForeignDataMetadata.Normalize(operation.Definition);
        BlueTuskForeignDataMetadata.Validate(definition);
        builder.Append("CREATE SERVER ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Name));
        if (definition.Type is not null)
        {
            builder.Append(" TYPE ");
            AppendStringLiteral(builder, definition.Type);
        }

        if (definition.Version is not null)
        {
            builder.Append(" VERSION ");
            AppendStringLiteral(builder, definition.Version);
        }

        builder.Append(" FOREIGN DATA WRAPPER ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.ForeignDataWrapper));
        AppendCreateForeignOptions(builder, definition.Options);
        EndStatement(builder);
    }

    private void Generate(
        AlterBlueTuskForeignServerOperation operation,
        MigrationCommandListBuilder builder)
    {
        var oldDefinition = BlueTuskForeignDataMetadata.Normalize(operation.OldDefinition);
        var definition = BlueTuskForeignDataMetadata.Normalize(operation.Definition);
        BlueTuskForeignDataMetadata.Validate(oldDefinition);
        BlueTuskForeignDataMetadata.Validate(definition);
        if (oldDefinition.Name != definition.Name)
        {
            throw new InvalidOperationException("A foreign-server alteration cannot also rename the server.");
        }

        if (oldDefinition.ForeignDataWrapper != definition.ForeignDataWrapper ||
            oldDefinition.Type != definition.Type)
        {
            throw new NotSupportedException(
                $"PostgreSQL cannot change the wrapper or type of foreign server '{definition.Name}' in place. " +
                "Create an explicit drop/recreate migration after handling dependent foreign tables and mappings.");
        }

        builder.Append("ALTER SERVER ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Name));
        if (oldDefinition.Version != definition.Version)
        {
            builder.Append(" VERSION ");
            if (definition.Version is null)
            {
                builder.Append("NULL");
            }
            else
            {
                AppendStringLiteral(builder, definition.Version);
            }
        }

        AppendAlterForeignOptions(builder, oldDefinition.Options, definition.Options);
        EndStatement(builder);
    }

    private void Generate(
        DropBlueTuskForeignServerOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        builder.Append("DROP SERVER ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" RESTRICT");
        EndStatement(builder);
    }

    private void Generate(
        RenameBlueTuskForeignServerOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        builder.Append("ALTER SERVER ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" RENAME TO ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName));
        EndStatement(builder);
    }

    private void Generate(
        CreateBlueTuskUserMappingOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskForeignDataMetadata.Normalize(operation.Definition);
        BlueTuskForeignDataMetadata.Validate(definition);
        if (definition.OptionsRedacted)
        {
            throw new InvalidOperationException(
                $"User mapping for '{definition.UserName ?? "PUBLIC"}' on server '{definition.ServerName}' has " +
                "redacted options. Supply its credentials from a secret source in a manually reviewed migration.");
        }

        builder.Append("CREATE USER MAPPING FOR ");
        AppendUserMappingTarget(builder, definition.UserName);
        builder.Append(" SERVER ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.ServerName));
        AppendCreateForeignOptions(builder, definition.Options);
        EndStatement(builder);
    }

    private void Generate(
        AlterBlueTuskUserMappingOperation operation,
        MigrationCommandListBuilder builder)
    {
        var oldDefinition = BlueTuskForeignDataMetadata.Normalize(operation.OldDefinition);
        var definition = BlueTuskForeignDataMetadata.Normalize(operation.Definition);
        BlueTuskForeignDataMetadata.Validate(oldDefinition);
        BlueTuskForeignDataMetadata.Validate(definition);
        if (oldDefinition.ServerName != definition.ServerName || oldDefinition.UserName != definition.UserName)
        {
            throw new InvalidOperationException("A user-mapping alteration cannot change its server or local role.");
        }

        if (oldDefinition.OptionsRedacted || definition.OptionsRedacted)
        {
            throw new InvalidOperationException(
                "Redacted user-mapping options cannot generate an automatic alteration. " +
                "Supply an explicit secret-backed migration operation.");
        }

        builder.Append("ALTER USER MAPPING FOR ");
        AppendUserMappingTarget(builder, definition.UserName);
        builder.Append(" SERVER ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(definition.ServerName));
        AppendAlterForeignOptions(builder, oldDefinition.Options, definition.Options);
        EndStatement(builder);
    }

    private void Generate(
        DropBlueTuskUserMappingOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.ServerName);
        builder.Append("DROP USER MAPPING FOR ");
        AppendUserMappingTarget(builder, operation.UserName);
        builder.Append(" SERVER ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.ServerName));
        EndStatement(builder);
    }

    private void AppendForeignDataWrapperVersionCheck(
        BlueTuskForeignDataWrapperDefinition definition,
        MigrationCommandListBuilder builder)
    {
        if (definition.ConnectionFunction is not null)
        {
            AppendForeignDataWrapperVersionCheck(definition.Name, builder);
        }
    }

    private void AppendForeignDataWrapperVersionCheck(
        BlueTuskForeignDataWrapperDefinition oldDefinition,
        BlueTuskForeignDataWrapperDefinition definition,
        MigrationCommandListBuilder builder)
    {
        if (oldDefinition.ConnectionFunction is not null || definition.ConnectionFunction is not null)
        {
            AppendForeignDataWrapperVersionCheck(definition.Name, builder);
        }
    }

    private void AppendForeignDataWrapperVersionCheck(string name, MigrationCommandListBuilder builder) =>
        GenerateMinimumVersionGuarded(
            Array.Empty<string>(),
            190000,
            $"BlueTusk foreign-data wrapper '{name}' uses a connection function and requires PostgreSQL 19 or later.",
            "$BlueTuskForeignData$",
            builder);

    private void AppendForeignDataWrapperFunction(
        MigrationCommandListBuilder builder,
        string prefix,
        string absent,
        string? function)
    {
        if (function is null)
        {
            builder.Append(absent);
            return;
        }

        builder.Append(prefix);
        AppendQualifiedIdentifier(builder, function);
    }

    private void AppendUserMappingTarget(MigrationCommandListBuilder builder, string? userName)
    {
        if (userName is null)
        {
            builder.Append("PUBLIC");
        }
        else
        {
            builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(userName));
        }
    }

    private void AppendCreateForeignOptions(
        MigrationCommandListBuilder builder,
        IReadOnlyList<BlueTuskForeignOptionDefinition> options)
    {
        if (options.Count == 0)
        {
            return;
        }

        builder.Append(" OPTIONS (");
        for (var index = 0; index < options.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(options[index].Name)).Append(" ");
            AppendStringLiteral(builder, options[index].Value);
        }

        builder.Append(")");
    }

    private void AppendAlterForeignOptions(
        MigrationCommandListBuilder builder,
        IReadOnlyList<BlueTuskForeignOptionDefinition> oldOptions,
        IReadOnlyList<BlueTuskForeignOptionDefinition> options)
    {
        var oldByName = oldOptions.ToDictionary(option => option.Name, StringComparer.Ordinal);
        var newByName = options.ToDictionary(option => option.Name, StringComparer.Ordinal);
        var changes = oldByName.Keys.Except(newByName.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name => new ForeignOptionChange("DROP", name, null))
            .Concat(newByName.Values.Where(option => !oldByName.ContainsKey(option.Name))
                .OrderBy(option => option.Name, StringComparer.Ordinal)
                .Select(option => new ForeignOptionChange("ADD", option.Name, option.Value)))
            .Concat(newByName.Values.Where(option =>
                    oldByName.TryGetValue(option.Name, out var old) && old.Value != option.Value)
                .OrderBy(option => option.Name, StringComparer.Ordinal)
                .Select(option => new ForeignOptionChange("SET", option.Name, option.Value)))
            .ToArray();
        if (changes.Length == 0)
        {
            return;
        }

        builder.Append(" OPTIONS (");
        for (var index = 0; index < changes.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            var change = changes[index];
            builder.Append(change.Action).Append(" ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(change.Name));
            if (change.Value is not null)
            {
                builder.Append(" ");
                AppendStringLiteral(builder, change.Value);
            }
        }

        builder.Append(")");
    }

    private readonly record struct ForeignOptionChange(string Action, string Name, string? Value);

    private void Generate(
        CreateBlueTuskSubscriptionOperation operation,
        MigrationCommandListBuilder builder)
    {
        var definition = BlueTuskSubscriptionMetadata.Normalize(operation.Definition);
        BlueTuskSubscriptionMetadata.ValidateForCreate(definition);
        if (definition.Connection.Kind == BlueTuskSubscriptionConnectionKind.Redacted)
        {
            throw new InvalidOperationException(
                $"Subscription '{definition.Name}' has a redacted connection. Supply a connection string or " +
                "PostgreSQL 19 foreign server in a manually reviewed migration before creating it.");
        }

        AppendSubscriptionVersionCheck(definition, builder);
        builder.Append(BuildCreateSubscriptionSql(definition));
        EndIndexStatement(
            builder,
            suppressTransaction: definition.ConnectOnCreate && definition.CreateSlot);
    }

    private void Generate(
        AlterBlueTuskSubscriptionOperation operation,
        MigrationCommandListBuilder builder)
    {
        var oldDefinition = BlueTuskSubscriptionMetadata.Normalize(operation.OldDefinition);
        var definition = BlueTuskSubscriptionMetadata.Normalize(operation.Definition);
        BlueTuskSubscriptionMetadata.Validate(oldDefinition);
        BlueTuskSubscriptionMetadata.Validate(definition);
        if (!string.Equals(oldDefinition.Name, definition.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Subscription alteration cannot also rename the subscription.");
        }

        AppendSubscriptionVersionCheck(oldDefinition, definition, builder);
        foreach (var statement in BuildAlterSubscriptionSql(oldDefinition, definition))
        {
            builder.Append(statement.Sql);
            EndIndexStatement(builder, statement.SuppressTransaction);
        }
    }

    private void Generate(
        DropBlueTuskSubscriptionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        builder.Append("DROP SUBSCRIPTION ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" RESTRICT");
        EndIndexStatement(builder, suppressTransaction: operation.HasSlot);
    }

    private void Generate(
        RenameBlueTuskSubscriptionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        builder.Append("ALTER SUBSCRIPTION ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" RENAME TO ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName));
        EndStatement(builder);
    }

    private void Generate(
        RefreshBlueTuskSubscriptionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        builder.Append("ALTER SUBSCRIPTION ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" REFRESH PUBLICATION WITH (copy_data = ")
            .Append(operation.CopyData ? "true" : "false")
            .Append(")");
        EndIndexStatement(builder, suppressTransaction: true);
    }

    private void Generate(
        RefreshBlueTuskSubscriptionSequencesOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        GenerateMinimumVersionGuarded(
            Array.Empty<string>(),
            190000,
            "BlueTusk subscription sequence refresh requires PostgreSQL 19 or later.",
            "$BlueTuskSubscription$",
            builder);
        builder.Append("ALTER SUBSCRIPTION ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" REFRESH SEQUENCES");
        EndIndexStatement(builder, suppressTransaction: true);
    }

    private void Generate(
        SkipBlueTuskSubscriptionTransactionOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        if (operation.FinishLsn is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operation.FinishLsn);
        }

        builder.Append("ALTER SUBSCRIPTION ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" SKIP (lsn = ");
        if (operation.FinishLsn is null)
        {
            builder.Append("NONE");
        }
        else
        {
            AppendStringLiteral(builder, operation.FinishLsn);
        }

        builder.Append(")");
        EndStatement(builder);
    }

    private string BuildCreateSubscriptionSql(BlueTuskSubscriptionDefinition definition)
    {
        var helper = Dependencies.SqlGenerationHelper;
        var sql = new StringBuilder("CREATE SUBSCRIPTION ")
            .Append(helper.DelimitIdentifier(definition.Name)).Append(' ');
        AppendSubscriptionConnection(sql, definition.Connection);
        sql.Append(" PUBLICATION ")
            .AppendJoin(", ", definition.Publications.Select(helper.DelimitIdentifier));
        var options = BuildCreateSubscriptionOptions(definition);
        if (options.Count > 0)
        {
            sql.Append(" WITH (").AppendJoin(", ", options).Append(')');
        }

        return sql.ToString();
    }

    private List<SubscriptionStatement> BuildAlterSubscriptionSql(
        BlueTuskSubscriptionDefinition oldDefinition,
        BlueTuskSubscriptionDefinition definition)
    {
        var helper = Dependencies.SqlGenerationHelper;
        var name = helper.DelimitIdentifier(definition.Name);
        var statements = new List<SubscriptionStatement>();
        if (oldDefinition.Connection != definition.Connection)
        {
            if (definition.Connection.Kind == BlueTuskSubscriptionConnectionKind.Redacted)
            {
                throw new InvalidOperationException(
                    $"Subscription '{definition.Name}' has a redacted target connection and cannot alter its connection.");
            }

            var sql = new StringBuilder("ALTER SUBSCRIPTION ").Append(name).Append(' ');
            AppendSubscriptionConnection(sql, definition.Connection);
            statements.Add(new SubscriptionStatement(sql.ToString(), SuppressTransaction: false));
        }

        if (!oldDefinition.Publications.SequenceEqual(definition.Publications, StringComparer.Ordinal))
        {
            statements.Add(new SubscriptionStatement(
                new StringBuilder("ALTER SUBSCRIPTION ").Append(name)
                    .Append(" SET PUBLICATION ")
                    .AppendJoin(", ", definition.Publications.Select(helper.DelimitIdentifier))
                    .Append(" WITH (refresh = false)")
                    .ToString(),
                SuppressTransaction: false));
        }

        if (oldDefinition.Enabled != definition.Enabled)
        {
            statements.Add(new SubscriptionStatement(
                $"ALTER SUBSCRIPTION {name} {(definition.Enabled ? "ENABLE" : "DISABLE")}",
                SuppressTransaction: false));
        }

        var options = BuildChangedSubscriptionOptions(oldDefinition, definition);
        if (options.Count > 0)
        {
            statements.Add(new SubscriptionStatement(
                new StringBuilder("ALTER SUBSCRIPTION ").Append(name)
                    .Append(" SET (").AppendJoin(", ", options).Append(')').ToString(),
                SuppressTransaction:
                    oldDefinition.Failover != definition.Failover ||
                    (oldDefinition.TwoPhase && !definition.TwoPhase)));
        }

        return statements;
    }

    private void AppendSubscriptionConnection(
        StringBuilder sql,
        BlueTuskSubscriptionConnection connection)
    {
        switch (connection.Kind)
        {
            case BlueTuskSubscriptionConnectionKind.ConnectionString:
                sql.Append("CONNECTION '").Append(EscapeLiteral(connection.Value!)).Append('\'');
                break;
            case BlueTuskSubscriptionConnectionKind.ForeignServer:
                sql.Append("SERVER ")
                    .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(connection.Value!));
                break;
            case BlueTuskSubscriptionConnectionKind.Redacted:
                throw new InvalidOperationException("A redacted subscription connection cannot generate SQL.");
            default:
                throw new InvalidOperationException($"Unknown subscription connection kind '{connection.Kind}'.");
        }
    }

    private static List<string> BuildCreateSubscriptionOptions(BlueTuskSubscriptionDefinition definition)
    {
        var options = new List<string>();
        if (!definition.ConnectOnCreate)
        {
            options.Add("connect = false");
        }
        else
        {
            if (!definition.CreateSlot)
            {
                options.Add("create_slot = false");
            }

            if (!definition.CopyData)
            {
                options.Add("copy_data = false");
            }

            if (!definition.Enabled)
            {
                options.Add("enabled = false");
            }
        }

        if (definition.SlotName is null)
        {
            options.Add("slot_name = NONE");
        }
        else if (!string.Equals(definition.SlotName, definition.Name, StringComparison.Ordinal))
        {
            options.Add($"slot_name = '{EscapeLiteral(definition.SlotName)}'");
        }

        if (definition.Binary)
        {
            options.Add("binary = true");
        }

        options.Add("streaming = " + SubscriptionStreamingLiteral(definition.Streaming));
        if (definition.SynchronousCommit != BlueTuskSubscriptionSynchronousCommit.Off)
        {
            options.Add($"synchronous_commit = '{SubscriptionSynchronousCommitLiteral(definition.SynchronousCommit)}'");
        }

        if (definition.TwoPhase)
        {
            options.Add("two_phase = true");
        }

        if (definition.DisableOnError)
        {
            options.Add("disable_on_error = true");
        }

        if (!definition.PasswordRequired)
        {
            options.Add("password_required = false");
        }

        if (definition.RunAsOwner)
        {
            options.Add("run_as_owner = true");
        }

        if (definition.Origin != BlueTuskSubscriptionOrigin.Any)
        {
            options.Add("origin = none");
        }

        if (definition.Failover)
        {
            options.Add("failover = true");
        }

        if (definition.RetainDeadTuples)
        {
            options.Add("retain_dead_tuples = true");
        }

        if (definition.MaxRetentionDuration > 0)
        {
            options.Add($"max_retention_duration = {definition.MaxRetentionDuration.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (definition.WalReceiverTimeout is not null)
        {
            options.Add($"wal_receiver_timeout = '{EscapeLiteral(definition.WalReceiverTimeout)}'");
        }

        return options;
    }

    private static List<string> BuildChangedSubscriptionOptions(
        BlueTuskSubscriptionDefinition oldDefinition,
        BlueTuskSubscriptionDefinition definition)
    {
        var options = new List<string>();
        if (!string.Equals(oldDefinition.SlotName, definition.SlotName, StringComparison.Ordinal))
        {
            options.Add(definition.SlotName is null
                ? "slot_name = NONE"
                : $"slot_name = '{EscapeLiteral(definition.SlotName)}'");
        }

        if (oldDefinition.Binary != definition.Binary)
        {
            options.Add($"binary = {definition.Binary.ToString().ToLowerInvariant()}");
        }

        if (oldDefinition.Streaming != definition.Streaming)
        {
            options.Add("streaming = " + SubscriptionStreamingLiteral(definition.Streaming));
        }

        if (oldDefinition.SynchronousCommit != definition.SynchronousCommit)
        {
            options.Add($"synchronous_commit = '{SubscriptionSynchronousCommitLiteral(definition.SynchronousCommit)}'");
        }

        if (oldDefinition.TwoPhase != definition.TwoPhase)
        {
            options.Add($"two_phase = {definition.TwoPhase.ToString().ToLowerInvariant()}");
        }

        if (oldDefinition.DisableOnError != definition.DisableOnError)
        {
            options.Add($"disable_on_error = {definition.DisableOnError.ToString().ToLowerInvariant()}");
        }

        if (oldDefinition.PasswordRequired != definition.PasswordRequired)
        {
            options.Add($"password_required = {definition.PasswordRequired.ToString().ToLowerInvariant()}");
        }

        if (oldDefinition.RunAsOwner != definition.RunAsOwner)
        {
            options.Add($"run_as_owner = {definition.RunAsOwner.ToString().ToLowerInvariant()}");
        }

        if (oldDefinition.Origin != definition.Origin)
        {
            options.Add("origin = " + (definition.Origin == BlueTuskSubscriptionOrigin.Any ? "any" : "none"));
        }

        if (oldDefinition.Failover != definition.Failover)
        {
            options.Add($"failover = {definition.Failover.ToString().ToLowerInvariant()}");
        }

        if (oldDefinition.RetainDeadTuples != definition.RetainDeadTuples)
        {
            options.Add($"retain_dead_tuples = {definition.RetainDeadTuples.ToString().ToLowerInvariant()}");
        }

        if (oldDefinition.MaxRetentionDuration != definition.MaxRetentionDuration)
        {
            options.Add($"max_retention_duration = {definition.MaxRetentionDuration.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (!string.Equals(oldDefinition.WalReceiverTimeout, definition.WalReceiverTimeout, StringComparison.Ordinal))
        {
            options.Add($"wal_receiver_timeout = '{EscapeLiteral(definition.WalReceiverTimeout ?? "-1")}'");
        }

        return options;
    }

    private void AppendSubscriptionVersionCheck(
        BlueTuskSubscriptionDefinition definition,
        MigrationCommandListBuilder builder)
    {
        var minimumVersion = BlueTuskSubscriptionMetadata.MinimumServerVersion(definition);
        if (minimumVersion > 150000)
        {
            AppendSubscriptionVersionCheck(definition.Name, minimumVersion, builder);
        }
    }

    private void AppendSubscriptionVersionCheck(
        BlueTuskSubscriptionDefinition oldDefinition,
        BlueTuskSubscriptionDefinition definition,
        MigrationCommandListBuilder builder)
    {
        var minimumVersion = Math.Max(
            BlueTuskSubscriptionMetadata.MinimumServerVersion(oldDefinition),
            BlueTuskSubscriptionMetadata.MinimumServerVersion(definition));
        if (minimumVersion > 150000)
        {
            AppendSubscriptionVersionCheck(definition.Name, minimumVersion, builder);
        }
    }

    private void AppendSubscriptionVersionCheck(
        string name,
        int minimumVersion,
        MigrationCommandListBuilder builder) =>
        GenerateMinimumVersionGuarded(
            Array.Empty<string>(),
            minimumVersion,
            $"BlueTusk subscription '{name}' requires PostgreSQL {minimumVersion / 10000} or later.",
            "$BlueTuskSubscription$",
            builder);

    private static string SubscriptionStreamingLiteral(BlueTuskSubscriptionStreamingMode mode) => mode switch
    {
        BlueTuskSubscriptionStreamingMode.Off => "off",
        BlueTuskSubscriptionStreamingMode.On => "on",
        BlueTuskSubscriptionStreamingMode.Parallel => "parallel",
        _ => throw new InvalidOperationException($"Unknown subscription streaming mode '{mode}'."),
    };

    private static string SubscriptionSynchronousCommitLiteral(BlueTuskSubscriptionSynchronousCommit mode) =>
        mode switch
        {
            BlueTuskSubscriptionSynchronousCommit.Off => "off",
            BlueTuskSubscriptionSynchronousCommit.Local => "local",
            BlueTuskSubscriptionSynchronousCommit.RemoteWrite => "remote_write",
            BlueTuskSubscriptionSynchronousCommit.On => "on",
            BlueTuskSubscriptionSynchronousCommit.RemoteApply => "remote_apply",
            _ => throw new InvalidOperationException($"Unknown synchronous-commit mode '{mode}'."),
        };

    private readonly record struct SubscriptionStatement(string Sql, bool SuppressTransaction);

    private void AppendPolicyRole(
        MigrationCommandListBuilder builder,
        BlueTuskRowSecurityRoleDefinition role)
    {
        builder.Append(role.Kind switch
        {
            BlueTuskRowSecurityRoleKind.Named =>
                Dependencies.SqlGenerationHelper.DelimitIdentifier(role.Name!),
            BlueTuskRowSecurityRoleKind.Public => "PUBLIC",
            BlueTuskRowSecurityRoleKind.CurrentRole => "CURRENT_ROLE",
            BlueTuskRowSecurityRoleKind.CurrentUser => "CURRENT_USER",
            BlueTuskRowSecurityRoleKind.SessionUser => "SESSION_USER",
            _ => throw new InvalidOperationException($"Unknown policy role kind '{role.Kind}'."),
        });
    }

    private void Generate(
        CreateBlueTuskPropertyGraphOperation operation,
        MigrationCommandListBuilder builder)
    {
        if (operation.Definition is null)
        {
            throw new InvalidOperationException("A property-graph create operation requires a definition.");
        }

        GenerateCapabilityGuarded([BuildCreateSql(operation.Definition)], builder);
    }

    private void Generate(
        DropBlueTuskPropertyGraphOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        GenerateCapabilityGuarded(
            [$"DROP PROPERTY GRAPH {Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema)}"],
            builder);
    }

    private void Generate(
        AlterBlueTuskPropertyGraphOperation operation,
        MigrationCommandListBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.NewName);
        var helper = Dependencies.SqlGenerationHelper;
        var statements = new List<string>();
        var currentName = operation.Name;
        var currentSchema = operation.Schema;
        if (!string.Equals(operation.Schema, operation.NewSchema, StringComparison.Ordinal))
        {
            if (operation.NewSchema is null)
            {
                throw new InvalidOperationException(
                    "A property graph cannot be moved to an unspecified schema.");
            }

            statements.Add(
                $"ALTER PROPERTY GRAPH {helper.DelimitIdentifier(currentName, currentSchema)} " +
                $"SET SCHEMA {helper.DelimitIdentifier(operation.NewSchema)}");
            currentSchema = operation.NewSchema;
        }

        if (!string.Equals(operation.Name, operation.NewName, StringComparison.Ordinal))
        {
            statements.Add(
                $"ALTER PROPERTY GRAPH {helper.DelimitIdentifier(currentName, currentSchema)} " +
                $"RENAME TO {helper.DelimitIdentifier(operation.NewName)}");
        }

        if (statements.Count == 0)
        {
            throw new InvalidOperationException("A property-graph alter operation must change its name or schema.");
        }

        GenerateCapabilityGuarded(statements, builder);
    }

    private string BuildCreateSql(BlueTuskPropertyGraphDefinition graph)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graph.Name);
        if (graph.ElementTables.Count == 0)
        {
            throw new InvalidOperationException(
                $"Property graph '{graph.Name}' must contain at least one element table.");
        }

        var helper = Dependencies.SqlGenerationHelper;
        var sql = new StringBuilder()
            .Append("CREATE PROPERTY GRAPH ")
            .Append(helper.DelimitIdentifier(graph.Name, graph.Schema));
        AppendElementGroup(
            sql,
            "VERTEX",
            graph.ElementTables.Where(element => element.Kind == BlueTuskGraphElementKind.Vertex));
        AppendElementGroup(
            sql,
            "EDGE",
            graph.ElementTables.Where(element => element.Kind == BlueTuskGraphElementKind.Edge));
        return sql.ToString();
    }

    private void AppendElementGroup(
        StringBuilder sql,
        string keyword,
        IEnumerable<BlueTuskGraphElementTableDefinition> elements)
    {
        var materialized = elements.ToArray();
        if (materialized.Length == 0)
        {
            return;
        }

        sql.Append(' ').Append(keyword).Append(" TABLES (");
        for (var index = 0; index < materialized.Length; index++)
        {
            if (index > 0)
            {
                sql.Append(", ");
            }

            AppendElement(sql, materialized[index]);
        }

        sql.Append(')');
    }

    private void AppendElement(StringBuilder sql, BlueTuskGraphElementTableDefinition element)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(element.Alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(element.Table);
        var helper = Dependencies.SqlGenerationHelper;
        sql.Append(helper.DelimitIdentifier(element.Table, element.Schema))
            .Append(" AS ")
            .Append(helper.DelimitIdentifier(element.Alias));
        AppendColumns(sql, " KEY", element.KeyColumns);

        if (element.Kind == BlueTuskGraphElementKind.Edge)
        {
            AppendEndpoint(sql, " SOURCE", element.Source, element.Alias);
            AppendEndpoint(sql, " DESTINATION", element.Destination, element.Alias);
        }

        foreach (var label in element.Labels)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(label.Name);
            sql.Append(" LABEL ").Append(helper.DelimitIdentifier(label.Name));
            if (label.Properties.Count > 0)
            {
                sql.Append(" PROPERTIES (");
                for (var index = 0; index < label.Properties.Count; index++)
                {
                    if (index > 0)
                    {
                        sql.Append(", ");
                    }

                    var property = label.Properties[index];
                    ArgumentException.ThrowIfNullOrWhiteSpace(property.Expression);
                    ArgumentException.ThrowIfNullOrWhiteSpace(property.Name);
                    sql.Append(property.IsColumn
                            ? helper.DelimitIdentifier(property.Expression)
                            : property.Expression)
                        .Append(" AS ")
                        .Append(helper.DelimitIdentifier(property.Name));
                }

                sql.Append(')');
            }
        }
    }

    private void AppendEndpoint(
        StringBuilder sql,
        string keyword,
        BlueTuskGraphEndpointDefinition? endpoint,
        string edgeAlias)
    {
        if (endpoint is null)
        {
            throw new InvalidOperationException(
                $"Edge table '{edgeAlias}' requires both source and destination endpoints.");
        }

        if (endpoint.EdgeKeyColumns.Count == 0 ||
            endpoint.EdgeKeyColumns.Count != endpoint.VertexKeyColumns.Count)
        {
            throw new InvalidOperationException(
                $"Edge table '{edgeAlias}' endpoint key columns must be non-empty and have matching counts.");
        }

        sql.Append(keyword);
        AppendColumns(sql, " KEY", endpoint.EdgeKeyColumns);
        sql.Append(" REFERENCES ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(endpoint.VertexTableAlias));
        AppendColumns(sql, string.Empty, endpoint.VertexKeyColumns);
    }

    private void AppendColumns(StringBuilder sql, string prefix, IReadOnlyList<string> columns)
    {
        if (columns.Count == 0)
        {
            return;
        }

        sql.Append(prefix).Append(" (");
        for (var index = 0; index < columns.Count; index++)
        {
            if (index > 0)
            {
                sql.Append(", ");
            }

            sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(columns[index]));
        }

        sql.Append(')');
    }

    private void GenerateCapabilityGuarded(
        IReadOnlyList<string> statements,
        MigrationCommandListBuilder builder)
    {
        var delimiter = "$BlueTuskGraph$";
        while (statements.Any(statement => statement.Contains(delimiter, StringComparison.Ordinal)))
        {
            delimiter = delimiter.Insert(delimiter.Length - 1, "_");
        }

        builder
            .Append("DO ").AppendLine(delimiter)
            .AppendLine("BEGIN")
            .AppendLine("    IF current_setting('server_version_num')::integer < 190000")
            .AppendLine("       OR pg_catalog.to_regclass('information_schema.property_graphs') IS NULL THEN")
            .AppendLine("        RAISE EXCEPTION USING")
            .AppendLine("            ERRCODE = '0A000',")
            .AppendLine("            MESSAGE = 'BlueTusk property-graph migrations require PostgreSQL 19 with SQL/PGQ support.';")
            .AppendLine("    END IF;");
        foreach (var statement in statements)
        {
            builder.Append("    EXECUTE '")
                .Append(EscapeLiteral(statement))
                .AppendLine("';");
        }

        builder.AppendLine("END;")
            .Append(delimiter);
        EndStatement(builder);
    }

    private void GenerateMinimumVersionGuarded(
        IReadOnlyList<string> statements,
        int minimumVersion,
        string message,
        string initialDelimiter,
        MigrationCommandListBuilder builder)
    {
        var delimiter = initialDelimiter;
        while (statements.Any(statement => statement.Contains(delimiter, StringComparison.Ordinal)))
        {
            delimiter = delimiter.Insert(delimiter.Length - 1, "_");
        }

        builder.Append("DO ").AppendLine(delimiter)
            .AppendLine("BEGIN")
            .Append("    IF current_setting('server_version_num')::integer < ")
            .Append(minimumVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .AppendLine(" THEN")
            .AppendLine("        RAISE EXCEPTION USING")
            .AppendLine("            ERRCODE = '0A000',")
            .Append("            MESSAGE = '").Append(EscapeLiteral(message)).AppendLine("';")
            .AppendLine("    END IF;");
        foreach (var statement in statements)
        {
            builder.Append("    EXECUTE '").Append(EscapeLiteral(statement)).AppendLine("';");
        }

        builder.AppendLine("END;")
            .Append(delimiter);
        EndStatement(builder);
    }

    private static string EscapeLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
