#if NETSTANDARD2_0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#endif
using System.Xml;
using System.Xml.Linq;
using static BlueTusk.Sql.SourceGeneration.SqlContractModel;

namespace BlueTusk.Sql.SourceGeneration;

// Shared by the offline generator and explicit database validation tool.
internal static class SqlValidationDocument
{
    internal const int MaximumCharacters = 8 * 1024 * 1024;

    internal sealed class Entry(string queryFingerprint, string schemaFingerprint, string snapshotFingerprint)
    {
        internal string QueryFingerprint { get; } = queryFingerprint;
        internal string SchemaFingerprint { get; } = schemaFingerprint;
        internal string SnapshotFingerprint { get; } = snapshotFingerprint;
    }

    internal static Dictionary<string, Entry> Read(IEnumerable<SqlSource> sources)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var fileCount = 0;
        long characters = 0;
        foreach (var source in sources)
        {
            characters += source.Text.Length;
            if (++fileCount > 16 || characters > MaximumCharacters) { throw new FormatException("Validation file bounds exceeded."); }
            using var text = new StringReader(source.Text);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumCharacters,
                IgnoreComments = true,
            });
            // Check depth and width before constructing an object graph.
            var nodes = 0;
            while (reader.Read())
            {
                if (reader.Depth > 2 || ++nodes > 10_000) { throw new FormatException("Validation structure bounds exceeded."); }
            }
            using var graphText = new StringReader(source.Text);
            using var graphReader = XmlReader.Create(graphText, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumCharacters,
            });
            var root = XDocument.Load(graphReader).Root ?? throw new FormatException("Validation document is empty.");
            if (root.Name != "BlueTuskSqlValidation" || root.Attributes().Count() != 3 ||
                (string?)root.Attribute("formatVersion") != "1" || !Digest((string?)root.Attribute("schemaFingerprint")) ||
                !Digest((string?)root.Attribute("snapshotFingerprint")))
            { throw new FormatException("Unsupported validation contract."); }
            foreach (var item in root.Elements())
            {
                var identity = (string?)item.Attribute("name");
                var fingerprint = (string?)item.Attribute("fingerprint");
                if (entries.Count >= 1000 || item.Name != "Query" || item.HasElements || item.Attributes().Count() != 2 ||
                    !string.IsNullOrWhiteSpace(item.Value) || identity is null || identity.Length > 256 ||
                    !identity.Split('.').All(ValidIdentifier) || !Digest(fingerprint) || entries.ContainsKey(identity))
                { throw new FormatException("Invalid or duplicate query validation."); }
                entries.Add(identity, new Entry(fingerprint!, (string)root.Attribute("schemaFingerprint")!, (string)root.Attribute("snapshotFingerprint")!));
            }
            if (root.Nodes().OfType<XText>().Any(node => !string.IsNullOrWhiteSpace(node.Value)))
            { throw new FormatException("Invalid validation document content."); }
        }
        return entries;
    }

    internal static string Write(string schemaFingerprint, string snapshotFingerprint, IEnumerable<Query> queries)
    {
        if (!Digest(schemaFingerprint) || !Digest(snapshotFingerprint)) { throw new ArgumentException("Canonical schema and snapshot fingerprints are required.", nameof(schemaFingerprint)); }
        var root = new XElement("BlueTuskSqlValidation", new XAttribute("formatVersion", "1"), new XAttribute("schemaFingerprint", schemaFingerprint),
            new XAttribute("snapshotFingerprint", snapshotFingerprint));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var query in queries.OrderBy(query => query.Identity, StringComparer.Ordinal))
        {
            if (names.Count >= 1000 || !names.Add(query.Identity)) { throw new ArgumentException("Query validation bounds exceeded.", nameof(queries)); }
            root.Add(new XElement("Query", new XAttribute("name", query.Identity), new XAttribute("fingerprint", Fingerprint(query))));
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static bool Digest(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
