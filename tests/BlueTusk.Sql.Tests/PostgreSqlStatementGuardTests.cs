namespace BlueTusk.Sql.Tests;

public sealed class PostgreSqlStatementGuardTests
{
    [Theory]
    [InlineData("SELECT ';' AS value;")]
    [InlineData("SELECT 'it''s; safe' AS value -- tail\n;")]
    [InlineData("SELECT E'backslash\\\';literal' AS value;")]
    [InlineData("SELECT $$a;b$$ AS value;")]
    [InlineData("SELECT $tag$a;b$tag$ AS value;")]
    [InlineData("/* outer /* inner; */ */ SELECT 1; -- trailing comment")]
    [InlineData("WITH rows AS (SELECT 1) SELECT * FROM rows;")]
    [InlineData("SELECT 1 AS \"semi;colon\";")]
    [InlineData("SELECT 1; /* complete trailing comment */")]
    public void Allows_single_query_and_handles_PostgreSQL_quoting(string sql) =>
        Assert.NotEmpty(PostgreSqlStatementGuard.AdmitReadQuery(sql));

    [Theory]
    [InlineData("SELECT 1; COMMIT; DELETE FROM events")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT 'unterminated")]
    [InlineData("SELECT $$unterminated")]
    [InlineData("SELECT 1 /* unterminated")]
    [InlineData("DELETE FROM events")]
    [InlineData("SELECT ';'; /* hidden */ COMMIT")]
    [InlineData("SELECT 1;;")]
    [InlineData("SELECT $tag$unterminated")]
    public void Rejects_statement_escape_and_incomplete_tokens(string sql) =>
        Assert.Throws<ArgumentException>(() => PostgreSqlStatementGuard.AdmitReadQuery(sql));
}
