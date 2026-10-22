# Frontend and API are served together by ASP.NET Core.
FROM node:24-bookworm-slim AS frontend
WORKDIR /source
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /source
COPY global.json ./
COPY backend/CarWise.Api/CarWise.Api.csproj backend/CarWise.Api/packages.lock.json backend/CarWise.Api/
RUN dotnet restore backend/CarWise.Api --locked-mode
COPY backend/CarWise.Api backend/CarWise.Api
COPY data data
COPY supabase/migrations supabase/migrations
COPY --from=frontend /source/dist dist
RUN dotnet publish backend/CarWise.Api -c Release --no-restore -o /published

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=backend /published .
COPY deploy/start-web.sh /app/start-web.sh
ENV ASPNETCORE_ENVIRONMENT=Production \
    CARWISE_ROOT=/app \
    CARWISE_KEYRING_DIR=/var/carwise/admin-keys \
    CARWISE_SERVER_SETTINGS_PATH=/var/carwise/server-settings/supabase.protected.json
EXPOSE 8000
ENTRYPOINT ["/bin/sh", "/app/start-web.sh"]
