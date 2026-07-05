# Dedicated game server image (PLAN §5: everything backend ships as a container,
# deployed with Docker Compose — never Kubernetes, Pillar #10).
# Build context is the repository root: docker build -f server/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY shared/ shared/
COPY server/ server/
RUN dotnet publish server/Prison.Server.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
COPY content/ /app/content/
ENV PRISON_SERVER_CONFIG=/app/config/server.toml
ENV PRISON_CONTENT_ROOT=/app/content
ENTRYPOINT ["dotnet", "Prison.Server.dll"]
