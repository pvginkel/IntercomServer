FROM mcr.microsoft.com/dotnet/runtime:9.0 AS base

COPY publish app/

WORKDIR /app

ENTRYPOINT ["dotnet", "IntercomServer.dll"]
