# Multi-stage build for every Wings LB process. Select the project with --build-arg PROJECT=...
#   src/Wings.Gateway/Wings.Gateway.csproj | src/Wings.Admin.Api/Wings.Admin.Api.csproj |
#   src/Wings.Monitor.Service/Wings.Monitor.Service.csproj | samples/Wings.MockBackend/Wings.MockBackend.csproj
ARG DOTNET_VERSION=10.0

FROM node:24-alpine AS web
WORKDIR /src
COPY web/package*.json web/
RUN cd web && npm ci
COPY web/ web/
RUN mkdir -p src/Wings.Admin.Api && cd web && npm run build -- --configuration production

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG PROJECT
WORKDIR /src
COPY Directory.Build.props Wings.LoadBalancer.slnx ./
COPY src/ src/
COPY samples/ samples/
COPY --from=web /src/src/Wings.Admin.Api/wwwroot/ src/Wings.Admin.Api/wwwroot/
RUN dotnet publish "$PROJECT" -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app
COPY --from=build /app ./
ARG ENTRY
ENV ENTRY=${ENTRY} ASPNETCORE_URLS=http://+:8080 DOTNET_gcServer=1
EXPOSE 8080
USER app
ENTRYPOINT ["sh", "-c", "exec dotnet \"$ENTRY\""]
