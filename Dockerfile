# Build: restore first (cached until a project file changes), then publish.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/Hestia.Api/Hestia.Api.csproj", "src/Hestia.Api/"]
COPY ["src/Hestia.Platform/Hestia.Platform.csproj", "src/Hestia.Platform/"]
COPY ["src/Hestia.Contracts/Hestia.Contracts.csproj", "src/Hestia.Contracts/"]
# Modules: two lines per module (see docs/new-module.md).
COPY ["src/Modules/Alerts/Hestia.Modules.Alerts/Hestia.Modules.Alerts.csproj", "src/Modules/Alerts/Hestia.Modules.Alerts/"]
COPY ["src/Modules/Alerts/Hestia.Modules.Alerts.Contracts/Hestia.Modules.Alerts.Contracts.csproj", "src/Modules/Alerts/Hestia.Modules.Alerts.Contracts/"]
COPY ["src/Modules/Documents/Hestia.Modules.Documents/Hestia.Modules.Documents.csproj", "src/Modules/Documents/Hestia.Modules.Documents/"]
COPY ["src/Modules/Documents/Hestia.Modules.Documents.Contracts/Hestia.Modules.Documents.Contracts.csproj", "src/Modules/Documents/Hestia.Modules.Documents.Contracts/"]
COPY ["src/Modules/Home/Hestia.Modules.Home/Hestia.Modules.Home.csproj", "src/Modules/Home/Hestia.Modules.Home/"]
COPY ["src/Modules/Home/Hestia.Modules.Home.Contracts/Hestia.Modules.Home.Contracts.csproj", "src/Modules/Home/Hestia.Modules.Home.Contracts/"]
COPY ["src/Modules/People/Hestia.Modules.People/Hestia.Modules.People.csproj", "src/Modules/People/Hestia.Modules.People/"]
COPY ["src/Modules/People/Hestia.Modules.People.Contracts/Hestia.Modules.People.Contracts.csproj", "src/Modules/People/Hestia.Modules.People.Contracts/"]
COPY ["src/Modules/Identity/Hestia.Modules.Identity/Hestia.Modules.Identity.csproj", "src/Modules/Identity/Hestia.Modules.Identity/"]
COPY ["src/Modules/Identity/Hestia.Modules.Identity.Contracts/Hestia.Modules.Identity.Contracts.csproj", "src/Modules/Identity/Hestia.Modules.Identity.Contracts/"]
COPY ["src/Modules/Sample/Hestia.Modules.Sample/Hestia.Modules.Sample.csproj", "src/Modules/Sample/Hestia.Modules.Sample/"]
COPY ["src/Modules/Sample/Hestia.Modules.Sample.Contracts/Hestia.Modules.Sample.Contracts.csproj", "src/Modules/Sample/Hestia.Modules.Sample.Contracts/"]
COPY ["src/Modules/Tasks/Hestia.Modules.Tasks/Hestia.Modules.Tasks.csproj", "src/Modules/Tasks/Hestia.Modules.Tasks/"]
COPY ["src/Modules/Tasks/Hestia.Modules.Tasks.Contracts/Hestia.Modules.Tasks.Contracts.csproj", "src/Modules/Tasks/Hestia.Modules.Tasks.Contracts/"]
COPY ["src/Modules/Today/Hestia.Modules.Today/Hestia.Modules.Today.csproj", "src/Modules/Today/Hestia.Modules.Today/"]
COPY ["src/Modules/Today/Hestia.Modules.Today.Contracts/Hestia.Modules.Today.Contracts.csproj", "src/Modules/Today/Hestia.Modules.Today.Contracts/"]
RUN dotnet restore "src/Hestia.Api/Hestia.Api.csproj"

COPY src/ src/
RUN dotnet publish "src/Hestia.Api/Hestia.Api.csproj" -c Release -o /app/publish --no-restore

# Runtime: the .NET image listens on 8080 and provides a non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# The OCR of the Documents module (D9): ocrmypdf and Tesseract (English) for PDFs and images, pdftotext for the text of
# a PDF, and the HEIF tools for iPhone photos. Then the folder of the documents volume, owned by the app user, so a new
# volume gets that owner.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ocrmypdf tesseract-ocr-eng poppler-utils libheif-examples \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/documents \
    && chown "$APP_UID:$APP_UID" /data/documents
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Hestia.Api.dll"]
