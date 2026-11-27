FROM mcr.microsoft.com/dotnet/runtime:9.0 AS base

# The server decodes its sounds with FFmpeg, and stops at its start without it. The base is
# Debian and builds as root.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY publish app/

WORKDIR /app

# Liveness (/healthz) and readiness (/readyz) probes. Override with HEALTH_PORT.
EXPOSE 8080

ENTRYPOINT ["dotnet", "IntercomServer.dll"]
