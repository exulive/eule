# Eule API — built by Coolify from the repository root:
#   Base Directory: /        Dockerfile Location: /src/Eule.Server/Dockerfile
# Only the server and the parser are copied in, so desktop changes do not affect the image.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Eule.Parsing/Eule.Parsing.csproj src/Eule.Parsing/
COPY src/Eule.Server/Eule.Server.csproj src/Eule.Server/
RUN dotnet restore src/Eule.Server/Eule.Server.csproj
COPY src/Eule.Parsing/ src/Eule.Parsing/
COPY src/Eule.Server/ src/Eule.Server/
RUN dotnet publish src/Eule.Server/Eule.Server.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# curl: for Coolify's health check. tzdata: Europe/Istanbul and other zones.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl tzdata \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://localhost:8080/health || exit 1
# The built-in non-root user of the .NET images.
USER app
ENTRYPOINT ["dotnet", "Eule.Server.dll"]
