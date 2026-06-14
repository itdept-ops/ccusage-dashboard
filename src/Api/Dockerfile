# --- build ---
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app --no-restore

# --- runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
# curl is only here so the container HEALTHCHECK (and compose's service_healthy gate) can probe.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=40s --retries=5 \
  CMD curl -fsS http://localhost:8080/api/health || exit 1
ENTRYPOINT ["dotnet", "Ccusage.Api.dll"]
