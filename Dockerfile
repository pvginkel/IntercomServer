FROM mcr.microsoft.com/dotnet/runtime:9.0 AS base

COPY publish app/

WORKDIR /app

# Liveness (/healthz) and readiness (/readyz) probes. Override with HEALTH_PORT.
EXPOSE 8080

ENTRYPOINT ["dotnet", "IntercomServer.dll"]
