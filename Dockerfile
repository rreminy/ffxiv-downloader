FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /source

COPY . .
RUN dotnet restore -p:ContinuousIntegrationBuild=true FFXIVDownloader.Command
RUN dotnet build -c Release --no-restore -p:ContinuousIntegrationBuild=true FFXIVDownloader.Command
RUN dotnet publish -c Release --no-build -o /app -p:ContinuousIntegrationBuild=true FFXIVDownloader.Command

FROM mcr.microsoft.com/dotnet/runtime:9.0-alpine

WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["/app/FFXIVDownloader.Command"]