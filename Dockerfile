# Build: restore first (cached until a project file changes), then publish.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/Hestia.Api/Hestia.Api.csproj", "src/Hestia.Api/"]
COPY ["src/Hestia.Platform/Hestia.Platform.csproj", "src/Hestia.Platform/"]
COPY ["src/Hestia.Contracts/Hestia.Contracts.csproj", "src/Hestia.Contracts/"]
# Modules: two lines per module (see docs/new-module.md).
COPY ["src/Modules/Sample/Hestia.Modules.Sample/Hestia.Modules.Sample.csproj", "src/Modules/Sample/Hestia.Modules.Sample/"]
COPY ["src/Modules/Sample/Hestia.Modules.Sample.Contracts/Hestia.Modules.Sample.Contracts.csproj", "src/Modules/Sample/Hestia.Modules.Sample.Contracts/"]
RUN dotnet restore "src/Hestia.Api/Hestia.Api.csproj"

COPY src/ src/
RUN dotnet publish "src/Hestia.Api/Hestia.Api.csproj" -c Release -o /app/publish --no-restore

# Runtime: the .NET image listens on 8080 and provides a non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Hestia.Api.dll"]
