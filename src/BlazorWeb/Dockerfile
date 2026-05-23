# ─────────────────────────────────────────────────────────────────────────────
# EventWOS Blazor WASM — Nginx static file server
# ─────────────────────────────────────────────────────────────────────────────

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore dependencies
COPY EventWOS.BlazorWeb.csproj ./
RUN dotnet restore EventWOS.BlazorWeb.csproj

# Copy all project files
COPY . .

# Inject API URL into appsettings at build time
ARG API_BASE_URL=https://eventwos-production.up.railway.app
RUN echo "{\"ApiBaseUrl\": \"${API_BASE_URL}\"}" > wwwroot/appsettings.json

# Publish Blazor WASM
RUN dotnet publish EventWOS.BlazorWeb.csproj \
    -c Release \
    -o /app/publish

# Blazor WASM publishes static assets to /app/publish/wwwroot
# Verify and show what was produced
RUN echo "=== Publish output ===" && ls /app/publish/ && \
    echo "=== wwwroot ===" && ls /app/publish/wwwroot/ && \
    if [ -d /app/publish/wwwroot/_framework ]; then \
      echo "=== _framework found ===" && ls /app/publish/wwwroot/_framework/ | head -10; \
    else \
      echo "WARNING: _framework not in wwwroot, checking alternate paths..." && \
      find /app/publish -name "blazor.webassembly.js" 2>/dev/null | head -5 && \
      find /src -name "blazor.webassembly.js" 2>/dev/null | head -5; \
    fi

# ─── Runtime: Nginx serves the Blazor WASM static files ──────────────────────
FROM nginx:1.27-alpine AS final

RUN rm /etc/nginx/conf.d/default.conf
COPY nginx.conf /etc/nginx/conf.d/default.conf

# Copy the full wwwroot (includes _framework/, index.html, etc.)
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html

# Verify nginx html root
RUN echo "=== Nginx html dir ===" && ls /usr/share/nginx/html/

EXPOSE 8080

# Substitute $PORT in nginx config at runtime, then start nginx
CMD ["/bin/sh", "-c", \
     "envsubst '$PORT' < /etc/nginx/conf.d/default.conf > /tmp/default.conf \
      && mv /tmp/default.conf /etc/nginx/conf.d/default.conf \
      && nginx -g 'daemon off;'"]
