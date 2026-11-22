# syntax=docker/dockerfile:1.7
# WiseSphere portal: Angular front-end + ASP.NET Core backend in one image.
# Build from the repository root:  docker build -f src/WiseSphere.Portal/Dockerfile -t wisesphere/portal .
FROM node:22.23.5-alpine AS web
WORKDIR /web
COPY web/portal/package.json web/portal/package-lock.json ./
RUN --mount=type=secret,id=ca,required=false \
    if [ -f /run/secrets/ca ]; then export NODE_EXTRA_CA_CERTS=/run/secrets/ca; fi; npm ci --no-audit --no-fund
COPY web/portal/ ./
RUN npx ng build --configuration production

FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS api
WORKDIR /src
COPY Directory.Build.props ./
COPY src/WiseSphere.Core/WiseSphere.Core.csproj src/WiseSphere.Core/
COPY src/WiseSphere.Glpi/WiseSphere.Glpi.csproj src/WiseSphere.Glpi/
COPY src/WiseSphere.Portal/WiseSphere.Portal.csproj src/WiseSphere.Portal/
RUN --mount=type=secret,id=ca,required=false \
    if [ -f /run/secrets/ca ]; then cp /run/secrets/ca /usr/local/share/ca-certificates/proxy-ca.crt && update-ca-certificates >/dev/null; fi; \
    dotnet restore src/WiseSphere.Portal/WiseSphere.Portal.csproj
COPY src/WiseSphere.Core/ src/WiseSphere.Core/
COPY src/WiseSphere.Glpi/ src/WiseSphere.Glpi/
COPY src/WiseSphere.Portal/ src/WiseSphere.Portal/
RUN dotnet publish src/WiseSphere.Portal/WiseSphere.Portal.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS runtime
WORKDIR /app
COPY --from=api /app .
COPY --from=web /web/dist/portal/browser ./wwwroot
ENV ASPNETCORE_ENVIRONMENT=Production DOTNET_gcServer=1
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "WiseSphere.Portal.dll"]
