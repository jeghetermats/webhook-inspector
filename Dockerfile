# syntax=docker/dockerfile:1

# 1. Build the React SPA
FROM node:24-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-fund --no-audit
COPY web/ ./
RUN npm run build

# 2. Publish the API and build the EF Core migration bundle
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props dotnet-tools.json ./
COPY src/WebhookInspector.Api/WebhookInspector.Api.csproj src/WebhookInspector.Api/
RUN dotnet restore src/WebhookInspector.Api/WebhookInspector.Api.csproj \
 && dotnet tool restore
COPY src/ src/
RUN dotnet publish src/WebhookInspector.Api -c Release -o /out --no-restore /p:UseAppHost=false
# A self-contained executable that applies pending migrations. Run by the migrate job
# before each app update; the app itself never migrates at startup (PLAN.md section 11).
RUN dotnet ef migrations bundle --project src/WebhookInspector.Api --configuration Release \
      --self-contained --target-runtime linux-x64 --output /migrate/efbundle

# 3. Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out ./
COPY --from=web /web/dist ./wwwroot
COPY --from=build /migrate/efbundle ./efbundle
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0
EXPOSE 8080
# Non-root user provided by the .NET images.
USER $APP_UID
ENTRYPOINT ["dotnet", "WebhookInspector.Api.dll"]
