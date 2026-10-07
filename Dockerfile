# syntax=docker/dockerfile:1

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first so the NuGet layer is cached between source changes.
COPY src/Notify.Web/Notify.Web.csproj src/Notify.Web/
RUN dotnet restore src/Notify.Web/Notify.Web.csproj

COPY src/ src/
# Tailwind is precompiled and committed (wwwroot/css/tailwind.css); the CLI is not present in the image,
# so the BuildTailwindCss target is skipped automatically.
RUN dotnet publish src/Notify.Web/Notify.Web.csproj -c Release -o /app/publish --no-restore

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# curl is only used by the HEALTHCHECK below.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

# Writable state: SQLite database + Data Protection keys, and uploaded logos. Both are volumes in compose.
RUN mkdir -p /app/App_Data /app/wwwroot/uploads/logos \
 && chown -R app:app /app
USER app

COPY --from=build --chown=app:app /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production \
    Kestrel__Endpoints__Http__Url=http://0.0.0.0:5600

EXPOSE 5600
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=5 \
  CMD curl -fsS http://localhost:5600/Account/Login > /dev/null || exit 1

ENTRYPOINT ["dotnet", "Notify.Web.dll"]
