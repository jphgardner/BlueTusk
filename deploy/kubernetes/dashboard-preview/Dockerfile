FROM mcr.microsoft.com/dotnet/sdk:10.0.400-noble@sha256:0e53453ccfc8ff2d51319fe80c678971c6d0f8008dff3565fa88e15840b69854 AS build
ARG SOURCE_COMMIT
WORKDIR /source
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props README.md ./
COPY eng/versions/ eng/versions/
COPY src/ src/
COPY samples/BlueTusk.Samples.Dashboard/ samples/BlueTusk.Samples.Dashboard/
RUN dotnet restore samples/BlueTusk.Samples.Dashboard/BlueTusk.Samples.Dashboard.csproj \
    --locked-mode
RUN dotnet publish samples/BlueTusk.Samples.Dashboard/BlueTusk.Samples.Dashboard.csproj \
    --configuration Release \
    --no-restore \
    --output /app \
    /p:UseAppHost=false \
    /p:SourceRevisionId=${SOURCE_COMMIT}

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-noble-chiseled@sha256:edec6ea65a92f432083a8f75fc3c18addd004015bbd4d523ce1d13e23b347008
ARG SOURCE_COMMIT
LABEL org.opencontainers.image.title="BlueTusk Dashboard read-only preview" \
      org.opencontainers.image.source="https://github.com/jphgardner/BlueTusk" \
      org.opencontainers.image.revision="${SOURCE_COMMIT}"
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    HOME=/tmp
WORKDIR /app
COPY --from=build --chown=1654:1654 /app ./
USER 1654:1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "BlueTusk.Samples.Dashboard.dll"]
