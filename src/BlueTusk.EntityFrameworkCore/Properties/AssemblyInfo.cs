using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Design;

[assembly: InternalsVisibleTo("BlueTusk.EntityFrameworkCore.Design")]
[assembly: InternalsVisibleTo("BlueTusk.EntityFrameworkCore.Tests")]
[assembly: InternalsVisibleTo("BlueTusk.ContinuousGraph")]
[assembly: InternalsVisibleTo("BlueTusk.ContinuousGraph.Tests")]
#if !BLUETUSK_EF7_OR_GREATER
[assembly: InternalsVisibleTo("BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore")]
#endif

[assembly: DesignTimeProviderServices(
    "BlueTusk.EntityFrameworkCore.Infrastructure.Internal.BlueTuskDesignTimeServicesBridge")]
