# BlueTusk.Extensions.PgTrgm

Preview PostgreSQL `pg_trgm` support for BlueTusk. Because pg_trgm adds
functions, operators, and index operator classes rather than a wire type, this
package contributes an immutable data-source feature and a parameterized
comparison API instead of a codec.

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.PgTrgm;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UsePgTrgm()
    .Build();

var result = await dataSource.ComparePgTrgmAsync("BlueTusk", "blue tusk");
Console.WriteLine(result.Similarity);
```

The comparison executes `similarity`, `word_similarity`,
`strict_word_similarity`, `show_trgm`, and the `%`, `<%`, and `<<%` threshold
operators in one round trip. Both strings are ordinary typed parameters. Pass
the installation schema to `UsePgTrgm(schema)` when it is not `public`; function
and operator qualification remains safe for quoted schema names.

PostgreSQL must have `CREATE EXTENSION pg_trgm` applied before comparison.
This package and the BlueTusk extension SDK are experimental `0.3.0-preview.1`
APIs, not stable or production-ready contracts.
