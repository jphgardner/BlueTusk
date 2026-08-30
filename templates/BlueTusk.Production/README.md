# BlueTusk production templates

Install the package and create a complete Clean Architecture application:

```console
dotnet new install BlueTusk.Production.Templates
dotnet new bluetusk-production --name Contoso.Orders --ClientFramework react
```

Choose `angular` for the Angular client. The generated system includes an API,
worker, EF Core migrations, tests, a same-origin BFF client, containers, a local
dependency stack, Helm, telemetry defaults, service-level objectives, and an
incident runbook.
