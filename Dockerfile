FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base

# The server decodes its sounds with FFmpeg, and stops at its start without it. The base is
# Debian and builds as root.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY publish app/

WORKDIR /app

# The web host, with the liveness (/healthz) and readiness (/readyz) probes. Override with
# HTTP_PORT; the base's ASPNETCORE_HTTP_PORTS is not read.
EXPOSE 8080

ENTRYPOINT ["dotnet", "IntercomServer.dll"]
