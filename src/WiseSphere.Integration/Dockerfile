# syntax=docker/dockerfile:1.7
# Build from the repository root:  docker build -f src/WiseSphere.Integration/Dockerfile -t wisesphere/integration .
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY Directory.Build.props WiseSphere.slnx ./
COPY src/WiseSphere.Core/WiseSphere.Core.csproj src/WiseSphere.Core/
COPY src/WiseSphere.Glpi/WiseSphere.Glpi.csproj src/WiseSphere.Glpi/
COPY src/WiseSphere.Integration/WiseSphere.Integration.csproj src/WiseSphere.Integration/
# Optional corporate proxy CA (docker build --secret id=ca,src=/path/ca.crt); ignored when absent
RUN --mount=type=secret,id=ca,required=false \
    if [ -f /run/secrets/ca ]; then cp /run/secrets/ca /usr/local/share/ca-certificates/proxy-ca.crt && update-ca-certificates >/dev/null; fi; \
    dotnet restore src/WiseSphere.Integration/WiseSphere.Integration.csproj
COPY src/ src/
RUN dotnet publish src/WiseSphere.Integration/WiseSphere.Integration.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS runtime
WORKDIR /app
COPY --from=build /app .
COPY config/ /config/
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=1
RUN mkdir -p /data && chown app:app /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "WiseSphere.Integration.dll"]
