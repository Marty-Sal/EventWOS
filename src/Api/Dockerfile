FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# ── Restore dependencies (cached layer) ──────────────────────────────────────
COPY src/Shared/EventWOS.Shared.csproj             src/Shared/
COPY src/Domain/EventWOS.Domain.csproj             src/Domain/
COPY src/Application/EventWOS.Application.csproj   src/Application/
COPY src/Infrastructure/EventWOS.Infrastructure.csproj src/Infrastructure/
COPY src/Persistence/EventWOS.Persistence.csproj   src/Persistence/
COPY src/Api/EventWOS.Api.csproj                   src/Api/

RUN dotnet restore src/Api/EventWOS.Api.csproj --no-cache

# ── Copy source and build ─────────────────────────────────────────────────────
# Cache bust: 1779662064
COPY . .

RUN dotnet publish src/Api/EventWOS.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ── Runtime image ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Railway injects HTTP_PORTS=$PORT at runtime — ASP.NET Core 8+ reads it automatically.
EXPOSE 8080

ENTRYPOINT ["dotnet", "EventWOS.Api.dll"]
