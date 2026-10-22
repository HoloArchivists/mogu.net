# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig Mogu.slnx ./
COPY src/Mogu.Core/Mogu.Core.csproj src/Mogu.Core/
COPY src/Mogu.Web/Mogu.Web.csproj src/Mogu.Web/
COPY tools/Mogu.LegacyImport/Mogu.LegacyImport.csproj tools/Mogu.LegacyImport/
RUN dotnet restore src/Mogu.Web && dotnet restore tools/Mogu.LegacyImport
COPY src/ src/
COPY tools/ tools/
ARG COMMIT_HASH=""
RUN dotnet publish src/Mogu.Web -c Release -o /out/web --no-restore \
 && dotnet publish tools/Mogu.LegacyImport -c Release -o /out/import --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/web ./
COPY --from=build /out/import /import
ARG COMMIT_HASH=""
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Mogu="Data Source=/data/mogu.db" \
    Mogu__DataDirectory=/data \
    Mogu__CommitHash=${COMMIT_HASH}
RUN mkdir /data && chown app:app /data
VOLUME /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mogu.Web.dll"]
