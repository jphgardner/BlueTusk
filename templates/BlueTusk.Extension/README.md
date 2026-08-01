# BlueTusk extension template

Install the package and create an independently packaged codec extension:

```powershell
dotnet new install BlueTusk.Templates
dotnet new bluetusk-extension `
  -n Contoso.BlueTusk.Extensions.Citext `
  --ExtensionName Citext `
  --PostgreSqlTypeName citext
```

The generated source and test projects include a value type, binary/text codec,
plug-in, data-source builder extension, immutable feature descriptor, unit
round trip, and optional live compatibility contract.
