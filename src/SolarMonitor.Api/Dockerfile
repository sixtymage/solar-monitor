# Build context: repository root.  docker build -f src/SolarMonitor.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/SolarMonitor.Core/SolarMonitor.Core.csproj src/SolarMonitor.Core/
COPY src/SolarMonitor.Api/SolarMonitor.Api.csproj src/SolarMonitor.Api/
RUN dotnet restore src/SolarMonitor.Api/SolarMonitor.Api.csproj
COPY src/SolarMonitor.Core/ src/SolarMonitor.Core/
COPY src/SolarMonitor.Api/ src/SolarMonitor.Api/
RUN dotnet publish src/SolarMonitor.Api/SolarMonitor.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "SolarMonitor.Api.dll"]
