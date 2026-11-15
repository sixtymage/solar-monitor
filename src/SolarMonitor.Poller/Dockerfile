# Build context: repository root.  docker build -f src/SolarMonitor.Poller/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/SolarMonitor.Core/SolarMonitor.Core.csproj src/SolarMonitor.Core/
COPY src/SolarMonitor.Poller/SolarMonitor.Poller.csproj src/SolarMonitor.Poller/
RUN dotnet restore src/SolarMonitor.Poller/SolarMonitor.Poller.csproj
COPY src/SolarMonitor.Core/ src/SolarMonitor.Core/
COPY src/SolarMonitor.Poller/ src/SolarMonitor.Poller/
RUN dotnet publish src/SolarMonitor.Poller/SolarMonitor.Poller.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0.14
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "SolarMonitor.Poller.dll"]
