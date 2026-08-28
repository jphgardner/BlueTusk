using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Design;

[assembly: InternalsVisibleTo("BlueTusk.EntityFrameworkCore.Design")]
[assembly: InternalsVisibleTo("BlueTusk.EntityFrameworkCore.Tests")]
[assembly: InternalsVisibleTo("BlueTusk.ContinuousGraph")]

[assembly: DesignTimeProviderServices(
    "BlueTusk.EntityFrameworkCore.Infrastructure.Internal.BlueTuskDesignTimeServicesBridge")]
