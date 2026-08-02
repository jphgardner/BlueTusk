namespace Microsoft.EntityFrameworkCore.Migrations;

public sealed partial class BlueTuskMigrationsSqlGeneratorTest
{
    public override void AddColumnOperation_without_column_type()
    {
        base.AddColumnOperation_without_column_type();

        AssertSql(
            """
ALTER TABLE "People" ADD "Alias" text NOT NULL;

""");
    }

    public override void AddColumnOperation_with_unicode_overridden()
    {
        base.AddColumnOperation_with_unicode_overridden();

        AssertSql(
            """
ALTER TABLE "Person" ADD "Name" text NULL;

""");
    }

    public override void AddColumnOperation_with_unicode_no_model()
    {
        base.AddColumnOperation_with_unicode_no_model();

        AssertSql(
            """
ALTER TABLE "Person" ADD "Name" text NULL;

""");
    }

    public override void AddColumnOperation_with_fixed_length_no_model()
    {
        base.AddColumnOperation_with_fixed_length_no_model();

        AssertSql(
            """
ALTER TABLE "Person" ADD "Name" character(100) NULL;

""");
    }

    public override void AddColumnOperation_with_maxLength_overridden()
    {
        base.AddColumnOperation_with_maxLength_overridden();

        AssertSql(
            """
ALTER TABLE "Person" ADD "Name" character varying(32) NULL;

""");
    }

    public override void AddColumnOperation_with_maxLength_no_model()
    {
        base.AddColumnOperation_with_maxLength_no_model();

        AssertSql(
            """
ALTER TABLE "Person" ADD "Name" character varying(30) NULL;

""");
    }

    public override void AddColumnOperation_with_precision_and_scale_overridden()
    {
        base.AddColumnOperation_with_precision_and_scale_overridden();

        AssertSql(
            """
ALTER TABLE "Person" ADD "Pi" numeric(15,10) NOT NULL;

""");
    }

    public override void AddColumnOperation_with_precision_and_scale_no_model()
    {
        base.AddColumnOperation_with_precision_and_scale_no_model();

        AssertSql(
            """
ALTER TABLE "Person" ADD "Pi" numeric(20,7) NOT NULL;

""");
    }

    public override void AddForeignKeyOperation_without_principal_columns()
    {
        base.AddForeignKeyOperation_without_principal_columns();

        AssertSql(
            """
ALTER TABLE "People" ADD FOREIGN KEY ("SpouseId") REFERENCES "People";

""");
    }

    public override void AlterColumnOperation_without_column_type()
    {
        base.AlterColumnOperation_without_column_type();

        AssertSql(
            """
ALTER TABLE "People" ALTER COLUMN "LuckyNumber" TYPE integer;

""");
    }

    public override void RenameTableOperation_legacy()
    {
        base.RenameTableOperation_legacy();

        AssertSql(
            """
ALTER TABLE "dbo"."People" RENAME TO "Person";

""");
    }

    public override void RenameTableOperation()
    {
        base.RenameTableOperation();

        AssertSql(
            """
ALTER TABLE "dbo"."People" RENAME TO "Person";

""");
    }

    public override void SqlOperation()
    {
        base.SqlOperation();

        AssertSql(
            """
-- I <3 DDL

""");
    }

    public override void InsertDataOperation_all_args_spatial()
    {
        base.InsertDataOperation_all_args_spatial();

        AssertSql(
            """
INSERT INTO "dbo"."People" ("Id", "Full Name", "Geometry")
VALUES (0, NULL, NULL);
INSERT INTO "dbo"."People" ("Id", "Full Name", "Geometry")
VALUES (1, 'Daenerys Targaryen', NULL);
INSERT INTO "dbo"."People" ("Id", "Full Name", "Geometry")
VALUES (2, 'John Snow', NULL);
INSERT INTO "dbo"."People" ("Id", "Full Name", "Geometry")
VALUES (3, 'Arya Stark', NULL);
INSERT INTO "dbo"."People" ("Id", "Full Name", "Geometry")
VALUES (4, 'Harry Strickland', NULL);
INSERT INTO "dbo"."People" ("Id", "Full Name", "Geometry")
VALUES (5, 'The Imp', NULL);
INSERT INTO "dbo"."People" ("Id", "Full Name", "Geometry")
VALUES (6, 'The Kingslayer', NULL);
INSERT INTO "dbo"."People" ("Id", "Full Name", "Geometry")
VALUES (7, 'Aemon Targaryen', '0107000020E6100000080000000102000000040000009A9999999999F13F9A999999999901409A999999999901409A999999999901409A999999999901409A9999999999F13F6666666666661C40CDCCCCCCCCCC1C400102000000040000006666666666661C40CDCCCCCCCCCC1C403333333333333440333333333333344033333333333334409A9999999999F13F6666666666865140CDCCCCCCCC8C514001040000000300000001010000009A9999999999F13F9A9999999999014001010000009A999999999901409A9999999999014001010000009A999999999901409A9999999999F13F010300000001000000040000009A9999999999F13F9A999999999901409A999999999901409A999999999901409A999999999901409A9999999999F13F9A9999999999F13F9A99999999990140010300000001000000040000003333333333332440333333333333344033333333333334403333333333333440333333333333344033333333333324403333333333332440333333333333344001010000009A9999999999F13F9A999999999901400105000000020000000102000000040000009A9999999999F13F9A999999999901409A999999999901409A999999999901409A999999999901409A9999999999F13F6666666666661C40CDCCCCCCCCCC1C400102000000040000006666666666661C40CDCCCCCCCCCC1C403333333333333440333333333333344033333333333334409A9999999999F13F6666666666865140CDCCCCCCCC8C51400106000000020000000103000000010000000400000033333333333324403333333333333440333333333333344033333333333334403333333333333440333333333333244033333333333324403333333333333440010300000001000000040000009A9999999999F13F9A999999999901409A999999999901409A999999999901409A999999999901409A9999999999F13F9A9999999999F13F9A99999999990140'::geometry(GeometryCollection,4326));

""");
    }

    public override void InsertDataOperation_required_args()
    {
        base.InsertDataOperation_required_args();

        AssertSql(
            """
INSERT INTO "dbo"."People" ("First Name")
VALUES ('John');

""");
    }

    public override void InsertDataOperation_required_args_composite()
    {
        base.InsertDataOperation_required_args_composite();

        AssertSql(
            """
INSERT INTO "dbo"."People" ("First Name", "Last Name")
VALUES ('John', 'Snow');

""");
    }

    public override void InsertDataOperation_required_args_multiple_rows()
    {
        base.InsertDataOperation_required_args_multiple_rows();

        AssertSql(
            """
INSERT INTO "dbo"."People" ("First Name")
VALUES ('John');
INSERT INTO "dbo"."People" ("First Name")
VALUES ('Daenerys');

""");
    }

    public override void InsertDataOperation_throws_for_unsupported_column_types()
        => base.InsertDataOperation_throws_for_unsupported_column_types();

    public override void DeleteDataOperation_all_args()
    {
        base.DeleteDataOperation_all_args();

        AssertSql(
            """
DELETE FROM "People"
WHERE "First Name" = 'Hodor'
RETURNING 1;
DELETE FROM "People"
WHERE "First Name" = 'Daenerys'
RETURNING 1;
DELETE FROM "People"
WHERE "First Name" = 'John'
RETURNING 1;
DELETE FROM "People"
WHERE "First Name" = 'Arya'
RETURNING 1;
DELETE FROM "People"
WHERE "First Name" = 'Harry'
RETURNING 1;

""");
    }

    public override void DeleteDataOperation_all_args_composite()
    {
        base.DeleteDataOperation_all_args_composite();

        AssertSql(
            """
DELETE FROM "People"
WHERE "First Name" = 'Hodor' AND "Last Name" IS NULL
RETURNING 1;
DELETE FROM "People"
WHERE "First Name" = 'Daenerys' AND "Last Name" = 'Targaryen'
RETURNING 1;
DELETE FROM "People"
WHERE "First Name" = 'John' AND "Last Name" = 'Snow'
RETURNING 1;
DELETE FROM "People"
WHERE "First Name" = 'Arya' AND "Last Name" = 'Stark'
RETURNING 1;
DELETE FROM "People"
WHERE "First Name" = 'Harry' AND "Last Name" = 'Strickland'
RETURNING 1;

""");
    }

    public override void DeleteDataOperation_required_args()
    {
        base.DeleteDataOperation_required_args();

        AssertSql(
            """
DELETE FROM "People"
WHERE "Last Name" = 'Snow'
RETURNING 1;

""");
    }

    public override void DeleteDataOperation_required_args_composite()
    {
        base.DeleteDataOperation_required_args_composite();

        AssertSql(
            """
DELETE FROM "People"
WHERE "First Name" = 'John' AND "Last Name" = 'Snow'
RETURNING 1;

""");
    }

    public override void UpdateDataOperation_all_args()
    {
        base.UpdateDataOperation_all_args();

        AssertSql(
            """
UPDATE "People" SET "Birthplace" = 'Winterfell', "House Allegiance" = 'Stark', "Culture" = 'Northmen'
WHERE "First Name" = 'Hodor'
RETURNING 1;
UPDATE "People" SET "Birthplace" = 'Dragonstone', "House Allegiance" = 'Targaryen', "Culture" = 'Valyrian'
WHERE "First Name" = 'Daenerys'
RETURNING 1;

""");
    }

    public override void UpdateDataOperation_all_args_composite()
    {
        base.UpdateDataOperation_all_args_composite();

        AssertSql(
            """
UPDATE "People" SET "House Allegiance" = 'Stark'
WHERE "First Name" = 'Hodor' AND "Last Name" IS NULL
RETURNING 1;
UPDATE "People" SET "House Allegiance" = 'Targaryen'
WHERE "First Name" = 'Daenerys' AND "Last Name" = 'Targaryen'
RETURNING 1;

""");
    }

    public override void UpdateDataOperation_all_args_composite_multi()
    {
        base.UpdateDataOperation_all_args_composite_multi();

        AssertSql(
            """
UPDATE "People" SET "Birthplace" = 'Winterfell', "House Allegiance" = 'Stark', "Culture" = 'Northmen'
WHERE "First Name" = 'Hodor' AND "Last Name" IS NULL
RETURNING 1;
UPDATE "People" SET "Birthplace" = 'Dragonstone', "House Allegiance" = 'Targaryen', "Culture" = 'Valyrian'
WHERE "First Name" = 'Daenerys' AND "Last Name" = 'Targaryen'
RETURNING 1;

""");
    }

    public override void UpdateDataOperation_all_args_multi()
    {
        base.UpdateDataOperation_all_args_multi();

        AssertSql(
            """
UPDATE "People" SET "Birthplace" = 'Dragonstone', "House Allegiance" = 'Targaryen', "Culture" = 'Valyrian'
WHERE "First Name" = 'Daenerys'
RETURNING 1;

""");
    }

    public override void UpdateDataOperation_required_args()
    {
        base.UpdateDataOperation_required_args();

        AssertSql(
            """
UPDATE "People" SET "House Allegiance" = 'Targaryen'
WHERE "First Name" = 'Daenerys'
RETURNING 1;

""");
    }

    public override void UpdateDataOperation_required_args_multiple_rows()
    {
        base.UpdateDataOperation_required_args_multiple_rows();

        AssertSql(
            """
UPDATE "People" SET "House Allegiance" = 'Stark'
WHERE "First Name" = 'Hodor'
RETURNING 1;
UPDATE "People" SET "House Allegiance" = 'Targaryen'
WHERE "First Name" = 'Daenerys'
RETURNING 1;

""");
    }

    public override void UpdateDataOperation_required_args_composite()
    {
        base.UpdateDataOperation_required_args_composite();

        AssertSql(
            """
UPDATE "People" SET "House Allegiance" = 'Targaryen'
WHERE "First Name" = 'Daenerys' AND "Last Name" = 'Targaryen'
RETURNING 1;

""");
    }

    public override void UpdateDataOperation_required_args_composite_multi()
    {
        base.UpdateDataOperation_required_args_composite_multi();

        AssertSql(
            """
UPDATE "People" SET "Birthplace" = 'Dragonstone', "House Allegiance" = 'Targaryen', "Culture" = 'Valyrian'
WHERE "First Name" = 'Daenerys' AND "Last Name" = 'Targaryen'
RETURNING 1;

""");
    }

    public override void UpdateDataOperation_required_args_multi()
    {
        base.UpdateDataOperation_required_args_multi();

        AssertSql(
            """
UPDATE "People" SET "Birthplace" = 'Dragonstone', "House Allegiance" = 'Targaryen', "Culture" = 'Valyrian'
WHERE "First Name" = 'Daenerys'
RETURNING 1;

""");
    }

    public override void DefaultValue_with_line_breaks(bool isUnicode)
    {
        base.DefaultValue_with_line_breaks(isUnicode);

        const string defaultValue = "\r\nVarious Line\rBreaks\n";
        AssertSql(
            $"CREATE TABLE \"dbo\".\"TestLineBreaks\" ({EOL}" +
            $"    \"TestDefaultValue\" text NOT NULL DEFAULT '{defaultValue}'{EOL}" +
            $");{EOL}");
    }

    public override void DefaultValue_with_line_breaks_2(bool isUnicode)
    {
        base.DefaultValue_with_line_breaks_2(isUnicode);

        var defaultValue = string.Concat(Enumerable.Range(0, 300).Select(value => $"{value}\r\n"));
        AssertSql(
            $"CREATE TABLE \"dbo\".\"TestLineBreaks\" ({EOL}" +
            $"    \"TestDefaultValue\" text NOT NULL DEFAULT '{defaultValue}'{EOL}" +
            $");{EOL}");
    }

    public override void Sequence_restart_operation(long? startsAt)
    {
        base.Sequence_restart_operation(startsAt);

        AssertSql(
            startsAt.HasValue
                ? $"""
ALTER SEQUENCE "dbo"."TestRestartSequenceOperation" START WITH {startsAt};
GO

ALTER SEQUENCE "dbo"."TestRestartSequenceOperation" RESTART;
"""
                : """ALTER SEQUENCE "dbo"."TestRestartSequenceOperation" RESTART;""");
    }
}
