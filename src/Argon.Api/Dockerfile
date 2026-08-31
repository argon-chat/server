FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
LABEL org.opencontainers.image.title="Argon.Api" \
      org.opencontainers.image.description="Argon communication platform API service" \
      org.opencontainers.image.url="https://argon.gl" \
      org.opencontainers.image.source="https://github.com/argon-chat/server" \
      org.opencontainers.image.revision="$GIT_COMMIT" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.vendor="Argon" \
      org.opencontainers.image.licenses="BSL 1.1" \
      org.opencontainers.image.created="$BUILD_DATE"

WORKDIR /app


# The Aegis sign-in widget, which the identity server serves itself from Aegis:StaticRoot rather
# than from a CDN of its own. Its own stage, and one buildkit runs alongside the .NET ones: it
# shares nothing with them but the final image.
FROM oven/bun:1 AS widget
WORKDIR /widget

# Manifests and workspace packages first, so a change to a component does not re-resolve the whole
# dependency tree. The packages have to come along with the lockfile — bun reads every workspace
# member's package.json to install, and `packages/*` is where they are.
COPY src/Frontend/Aegis/package.json src/Frontend/Aegis/bun.lock ./
COPY src/Frontend/Aegis/packages ./packages
RUN bun install --frozen-lockfile --no-progress

COPY src/Frontend/Aegis ./
RUN bun run build


FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY Directory.Build.props .

# Every project Argon.Api reaches transitively has to be here, or the restore layer fails on a
# ProjectReference it cannot resolve.
COPY src/Argon.Api/Argon.Api.csproj src/Argon.Api/
COPY src/Argon.Core/Argon.Core.csproj src/Argon.Core/
COPY src/Argon.CodeGen/Argon.Ion.CodeGen.csproj src/Argon.CodeGen/
COPY src/Argon.CodeGenAdmin/Argon.Ion.CodeGenAdmin.csproj src/Argon.CodeGenAdmin/
COPY src/Argon.CodeGenAccount/Argon.Ion.CodeGenAccount.csproj src/Argon.CodeGenAccount/

RUN dotnet restore src/Argon.Api/Argon.Api.csproj

COPY . .

WORKDIR /src/src/Argon.Api
RUN dotnet build Argon.Api.csproj -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish Argon.Api.csproj -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

COPY src/Argon.Core/Migrations/*.sql /app/publish/Migrations/

FROM base AS final
WORKDIR /app

COPY --from=publish /app/publish .

# Where Aegis:StaticRoot points. Only the identity server reads it, and only when it is configured
# to — a deployment that puts the widget behind its own CDN leaves the setting empty and the
# directory unread.
COPY --from=widget /widget/dist ./wwwroot

ENV DOTNET_ENABLE_HTTP3="true"

EXPOSE 5002 5002/udp
EXPOSE 5001 5001/udp
EXPOSE 5000 5000/udp

# Consoles listen on ports of their own, one role each: admin, then the developer account console.
EXPOSE 8920
EXPOSE 8930

ENTRYPOINT ["dotnet", "Argon.Api.dll"]
