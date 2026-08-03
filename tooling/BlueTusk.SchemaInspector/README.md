# BlueTusk.SchemaInspector

Displays PostgreSQL 19 property graphs through BlueTusk's typed, read-only
information-schema discovery API. Output is human-readable by default and can
be emitted as JSON for automation.

```powershell
$env:BLUETUSK_CONNECTION_STRING = "Host=localhost;Port=5419;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
dotnet run --project tooling/BlueTusk.SchemaInspector -- --schema application --graph social
dotnet run --project tooling/BlueTusk.SchemaInspector -- --schema application --json
```

Options:

- `--connection <connection-string>` overrides `BLUETUSK_CONNECTION_STRING`;
- `--catalog <catalog>`, `--schema <schema>`, and `--graph <name>` apply exact
  filters;
- `--json` emits a versioned-server/capability envelope and the complete typed
  graph model; and
- `--help` displays usage.

The connection string is never printed. On PostgreSQL 15–18 the command exits
successfully with an empty graph collection because the server capability guard
prevents access to PostgreSQL 19-only views.
