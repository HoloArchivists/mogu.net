# syntax=docker/dockerfile:1
# The SDK stage runs natively and cross-compiles for the target architecture (no QEMU needed).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig Mogu.slnx ./
COPY src/Mogu.Core/Mogu.Core.csproj src/Mogu.Core/
COPY src/Mogu.Web/Mogu.Web.csproj src/Mogu.Web/
COPY tools/Mogu.LegacyImport/Mogu.LegacyImport.csproj tools/Mogu.LegacyImport/
RUN dotnet restore src/Mogu.Web -a $TARGETARCH && dotnet restore tools/Mogu.LegacyImport -a $TARGETARCH
COPY src/ src/
COPY tools/ tools/
ARG COMMIT_HASH=""
RUN dotnet publish src/Mogu.Web -c Release -a $TARGETARCH -o /out/web --no-restore \
 && dotnet publish tools/Mogu.LegacyImport -c Release -a $TARGETARCH -o /out/import --no-restore \
 && mkdir /out/data

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/web ./
COPY --from=build /out/import /import
ARG COMMIT_HASH=""
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Mogu="Data Source=/data/mogu.db" \
    Mogu__DataDirectory=/data \
    Mogu__CommitHash=${COMMIT_HASH}
# No RUN in this stage, so cross-platform builds need no emulation.
COPY --from=build --chown=app:app /out/data /data
VOLUME /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mogu.Web.dll"]
