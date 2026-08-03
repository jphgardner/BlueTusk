# BlueTusk.Extensions.Testing

Framework-neutral compatibility checks for optional BlueTusk extensions.

`BlueTuskExtensionCompatibility.VerifyAsync` verifies that a plug-in feature
survives data-source construction and that a live PostgreSQL catalogue entry is
bound to the expected CLR and codec types. Extension packages should retain
their own binary/text round-trip and SQL-behaviour tests alongside this shared
contract check.
