#if NETSTANDARD2_0
using System;
using System.Collections.Generic;
using System.Linq;
#endif
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace BlueTusk.Sql.SourceGeneration;

internal static class SqlContractModel
{
    private static readonly char[] MemberSeparators = [' ', '\t'];
    private static readonly HashSet<string> ReservedRecordMembers = new(StringComparer.Ordinal)
        { "ToString", "Equals", "GetHashCode", "EqualityContract", "PrintMembers", "Clone" };
    private static readonly Dictionary<string, PgType> Types = new(StringComparer.Ordinal)
    {
        ["bool"] = new("bool", "bool", 16),
        ["int2"] = new("short", "int2", 21),
        ["int4"] = new("int", "int4", 23),
        ["int8"] = new("long", "int8", 20),
        ["text"] = new("string", "text", 25),
        ["varchar"] = new("string", "varchar", 1043),
        ["uuid"] = new("global::System.Guid", "uuid", 2950),
        ["float4"] = new("float", "float4", 700),
        ["float8"] = new("double", "float8", 701),
        ["numeric"] = new("decimal", "numeric", 1700),
        ["timestamp"] = new("global::System.DateTime", "timestamp", 1114),
        ["timestamptz"] = new("global::System.DateTimeOffset", "timestamptz", 1184),
        ["bytea"] = new("byte[]", "bytea", 17),
        ["json"] = new("string", "json", 114),
        ["jsonb"] = new("string", "jsonb", 3802),
        ["numeric-precise"] = new("global::BlueTusk.TypeSystem.BlueTuskNumeric", "numeric", 1700),
        ["date"] = new("global::System.DateOnly", "date", 1082),
        ["time"] = new("global::System.TimeOnly", "time", 1083),
        ["interval-pg"] = new("global::BlueTusk.TypeSystem.BlueTuskInterval", "interval", 1186),
        ["bool[]"] = new("bool[]", "_bool", 1000),
        ["int2[]"] = new("short[]", "_int2", 1005),
        ["int4[]"] = new("int[]", "_int4", 1007),
        ["int8[]"] = new("long[]", "_int8", 1016),
        ["int2?[]"] = new("short?[]", "_int2", 1005),
        ["int4?[]"] = new("int?[]", "_int4", 1007),
        ["int8?[]"] = new("long?[]", "_int8", 1016),
        ["int4[,]"] = new("int[,]", "_int4", 1007),
        ["text[]"] = new("string?[]", "_text", 1009),
        ["varchar[]"] = new("string?[]", "_varchar", 1015),
        ["uuid[]"] = new("global::System.Guid[]", "_uuid", 2951),
        ["float4[]"] = new("float[]", "_float4", 1021),
        ["float8[]"] = new("double[]", "_float8", 1022),
        ["numeric[]"] = new("decimal[]", "_numeric", 1231),
        ["numeric-precise[]"] = new("global::BlueTusk.TypeSystem.BlueTuskNumeric[]", "_numeric", 1231),
        ["timestamp[]"] = new("global::System.DateTime[]", "_timestamp", 1115),
        ["timestamptz[]"] = new("global::System.DateTimeOffset[]", "_timestamptz", 1185),
        ["date[]"] = new("global::System.DateOnly[]", "_date", 1182),
        ["json[]"] = new("string?[]", "_json", 199),
        ["jsonb[]"] = new("string?[]", "_jsonb", 3807),
    };

    internal static Query? Parse(SqlSource source, out string error)
    {
        error = string.Empty;
        if (source.Text.Length > 1024 * 1024 || Encoding.UTF8.GetByteCount(source.Text) > 1024 * 1024)
        {
            error = "SQL source is bounded to one MiB";
            return null;
        }

        string? identity = null;
        var parameters = new List<Member>();
        var columns = new List<Member>();
        var sql = new StringBuilder();
        var maximumRows = 1000;
        var sawMaximum = false;
        var requiresValidation = false;
        var sqlStarted = false;
        foreach (var rawLine in source.Text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("-- bluetusk-", StringComparison.Ordinal))
            {
                if (line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal))
                {
                    sqlStarted = true;
                }
                sql.AppendLine(rawLine);
                continue;
            }

            if (sqlStarted)
            {
                error = "typed SQL directives must precede the SQL body";
                return null;
            }

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                error = "directives require a colon";
                return null;
            }

            var directive = line.Substring(0, colon);
            var value = line.Substring(colon + 1).Trim();
            if (directive == "-- bluetusk-query")
            {
                if (identity is not null || !ValidIdentity(value))
                {
                    error = "one query identity is required, using valid namespace and class identifiers";
                    return null;
                }

                identity = value;
            }
            else if (directive == "-- bluetusk-max-rows")
            {
                if (sawMaximum || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out maximumRows) || maximumRows is < 1 or > 1_000_000)
                {
                    error = "one maximum row bound between 1 and 1000000 is supported";
                    return null;
                }

                sawMaximum = true;
            }
            else if (directive == "-- bluetusk-validation")
            {
                if (requiresValidation || value != "required") { error = "one required validation directive is supported"; return null; }
                requiresValidation = true;
            }
            else if (directive is "-- bluetusk-param" or "-- bluetusk-result")
            {
                var parts = value.Split(MemberSeparators, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || !ValidIdentifier(parts[0]) || !TryGetType(parts[1], out var type) ||
                    parts[2] is not ("nullable" or "required"))
                {
                    error = "parameters/results require 'name PostgreSQL-type required|nullable'; supported catalogue types are enum:schema.name[ ] and domain:schema.name:built-in[ ] (without the space before [])";
                    return null;
                }

                var members = directive == "-- bluetusk-param" ? parameters : columns;
                if (members.Any(member => member.Name == parts[0]) || members.Count >= 64 || ReservedRecordMembers.Contains(parts[0]) ||
                    parts[0] == (directive == "-- bluetusk-param" ? "Arguments" : "Row"))
                {
                    error = "parameter/result names must be unique and each list is bounded to 64 members";
                    return null;
                }

                members.Add(new Member(parts[0], type, parts[2] == "nullable"));
            }
            else
            {
                error = "unknown BlueTusk SQL directive";
                return null;
            }
        }

        if (identity is null || columns.Count == 0 || string.IsNullOrWhiteSpace(sql.ToString()))
        {
            error = "a query identity, at least one result column and SQL are required";
            return null;
        }

        if (!ValidateParameterReferences(sql.ToString(), parameters.Count))
        {
            error = "SQL positional parameters must exactly cover the contiguous declared parameter list; literals/comments are excluded";
            return null;
        }

        return new Query(identity, sql.ToString().Replace("\r\n", "\n").Trim(), parameters, columns, maximumRows, requiresValidation);
    }

    private static bool ValidateParameterReferences(string sql, int count)
    {
        var used = new bool[count];
        for (var index = 0; index < sql.Length;)
        {
            var character = sql[index];
            if (character == '\0') { return false; }
            if (character == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not ('\r' or '\n')) { index++; }
                continue;
            }
            if (character == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index += 2;
                var depth = 1;
                while (index < sql.Length && depth != 0)
                {
                    if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
                    { if (++depth > 32) { return false; } index += 2; }
                    else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/') { depth--; index += 2; }
                    else { index++; }
                }
                if (depth != 0) { return false; }
                continue;
            }
            if (character is '\'' or '"')
            {
                var quote = character;
                var escape = quote == '\'' && index > 0 && sql[index - 1] is 'e' or 'E' && (index < 2 || !Identifier(sql[index - 2]));
                index++;
                var closed = false;
                while (index < sql.Length)
                {
                    if (escape && sql[index] == '\\') { index += 2; }
                    else if (sql[index] == quote)
                    {
                        index++;
                        if (index < sql.Length && sql[index] == quote) { index++; }
                        else { closed = true; break; }
                    }
                    else { index++; }
                }
                if (!closed) { return false; }
                continue;
            }
            if (character == '$' && (index == 0 || !Identifier(sql[index - 1])))
            {
                var end = index + 1;
                if (end < sql.Length && sql[end] is >= '0' and <= '9')
                {
                    var ordinal = 0;
                    while (end < sql.Length && sql[end] is >= '0' and <= '9')
                    {
                        ordinal = ordinal * 10 + sql[end++] - '0';
                        if (ordinal > count) { return false; }
                    }
                    if (ordinal == 0 || end < sql.Length && Identifier(sql[end])) { return false; }
                    used[ordinal - 1] = true;
                    index = end;
                    continue;
                }
                if (end < sql.Length && (sql[end] == '$' || char.IsLetter(sql[end]) || sql[end] == '_'))
                {
                    while (end < sql.Length && Identifier(sql[end]) && sql[end] != '$') { end++; }
                    if (end < sql.Length && sql[end] == '$')
                    {
                        var delimiter = sql.Substring(index, end + 1 - index);
                        var close = sql.IndexOf(delimiter, end + 1, StringComparison.Ordinal);
                        if (close < 0) { return false; }
                        index = close + delimiter.Length;
                        continue;
                    }
                }
            }
            index++;
        }
        return used.All(value => value);
        static bool Identifier(char value) => char.IsLetterOrDigit(value) || value is '_' or '$';
    }

    internal static bool ValidIdentifier(string text) => SyntaxFacts.IsValidIdentifier(text) &&
        SyntaxFacts.GetKeywordKind(text) == SyntaxKind.None && SyntaxFacts.GetContextualKeywordKind(text) == SyntaxKind.None;

    private static bool TryGetType(string annotation, out PgType type)
    {
        if (Types.TryGetValue(annotation, out type!)) { return true; }
        var array = annotation.EndsWith("[]", StringComparison.Ordinal);
        var scalar = array ? annotation.Substring(0, annotation.Length - 2) : annotation;
        if (scalar.StartsWith("enum:", StringComparison.Ordinal))
        {
            var name = scalar.Substring("enum:".Length);
            if (ValidQualifiedTypeName(name))
            {
                type = new PgType(array ? "global::BlueTusk.TypeSystem.BlueTuskEnumValue[]" :
                    "global::BlueTusk.TypeSystem.BlueTuskEnumValue", annotation, 0, name, 'e', 0, array);
                return true;
            }
        }
        else if (scalar.StartsWith("domain:", StringComparison.Ordinal))
        {
            var separator = scalar.LastIndexOf(':');
            if (separator > "domain:".Length)
            {
                var name = scalar.Substring("domain:".Length, separator - "domain:".Length);
                var baseName = scalar.Substring(separator + 1);
                if (ValidQualifiedTypeName(name) && Types.TryGetValue(baseName, out var baseType) &&
                    !baseName.Contains('[') && (!array || Types.TryGetValue(baseName + "[]", out baseType)))
                {
                    type = new PgType(baseType.ClrName, annotation, 0, name, 'd',
                        Types[baseName].Oid, array);
                    return true;
                }
            }
        }
        type = null!;
        return false;
    }

    private static bool ValidQualifiedTypeName(string name)
    {
        var dot = name.IndexOf('.');
        return dot > 0 && dot == name.LastIndexOf('.') &&
            ValidPart(name.Substring(0, dot)) && ValidPart(name.Substring(dot + 1));

        static bool ValidPart(string part)
        {
            if (part.Length is < 1 or > 63 || part[0] is not (>= 'a' and <= 'z' or '_')) { return false; }
            return part.Skip(1).All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
        }
    }

    private static bool ValidIdentity(string text) => text.Length <= 256 && text.Split('.').All(ValidIdentifier);

    internal sealed class PgType(string clrName, string postgresName, uint oid,
        string? qualifiedName = null, char catalogKind = '\0', uint baseOid = 0, bool isArray = false)
    {
        public string ClrName { get; } = clrName;
        public string PostgresName { get; } = postgresName;
        public uint Oid { get; } = oid;
        public string? QualifiedName { get; } = qualifiedName;
        public char CatalogKind { get; } = catalogKind;
        public uint BaseOid { get; } = baseOid;
        public bool IsArray { get; } = isArray;
        public string ParameterTypeName => QualifiedName + (IsArray ? "[]" : string.Empty);
    }

    internal sealed class Member(string name, PgType type, bool nullable)
    {
        public string Name { get; } = name;
        public PgType Type { get; } = type;
        public bool Nullable { get; } = nullable;
        public string ClrType => Type.ClrName + (Nullable ? "?" : string.Empty);
    }

    internal sealed class Query(string identity, string sql, List<Member> parameters, List<Member> columns, int maximumRows, bool requiresValidation)
    {
        public string Identity { get; } = identity;
        public string Sql { get; } = sql;
        public List<Member> Parameters { get; } = parameters;
        public List<Member> Columns { get; } = columns;
        public int MaximumRows { get; } = maximumRows;
        public bool RequiresValidation { get; } = requiresValidation;
        public bool UsesCatalogueTypes => Parameters.Any(item => item.Type.QualifiedName is not null) ||
            Columns.Any(item => item.Type.QualifiedName is not null);
    }

    internal sealed class SqlSource(string path, string text)
    {
        public string Path { get; } = path;
        public string Text { get; } = text;
    }
    internal static string Fingerprint(Query query)
    {
        var text = new StringBuilder("BlueTusk.Sql.Validation:1\n");
        Add(query.Identity); Add(query.Sql); Add(query.MaximumRows.ToString(CultureInfo.InvariantCulture));
        foreach (var item in query.Parameters) { Add(item.Name); Add(item.Type.PostgresName); Add(item.Type.ClrName); Add(item.Nullable ? "nullable" : "required"); }
        Add("results");
        foreach (var item in query.Columns) { Add(item.Name); Add(item.Type.PostgresName); Add(item.Type.ClrName); Add(item.Nullable ? "nullable" : "required"); }
        return DocumentFingerprint(text.ToString());
        void Add(string value) { text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n'); }
    }

    internal static string DocumentFingerprint(string text)
    {
#if NET8_0_OR_GREATER
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
#else
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
#endif
    }
}
