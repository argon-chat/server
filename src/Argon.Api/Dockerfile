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


# Both front-ends: the Aegis sign-in widget and the developer console. One stage, because they are
# one bun workspace — a single install resolves the dependencies they share instead of twice. Its
# own stage, and one buildkit runs alongside the .NET ones: it shares nothing with them but the
# final image.
FROM oven/bun:1 AS widgets
WORKDIR /frontend

# Manifests and workspace packages first, so a change to a component does not re-resolve the whole
# dependency tree. Every workspace member's package.json has to be here for the install to plan, so
# the shared packages come along whole and the two apps contribute their manifests.
COPY src/Frontend/package.json src/Frontend/bun.lock ./
COPY src/Frontend/packages ./packages
COPY src/Frontend/Aegis/package.json ./Aegis/
COPY src/Frontend/Console/package.json ./Console/
RUN bun install --frozen-lockfile --no-progress

COPY src/Frontend ./
RUN bun run --cwd Aegis build && bun run --cwd Console build


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

# Where Aegis:StaticRoot and AccountConsole:StaticRoot point. Each is read by one role, and only
# when that role is configured to — a deployment that puts a front-end behind its own CDN leaves
# the setting empty and the directory unread.
#
# Named after the app rather than one of them being `wwwroot`: with two of them neither is "the"
# static root, and a directory actually called wwwroot is one ASP.NET adopts as WebRootPath on
# every role in this image, which would quietly put the sign-in widget behind any future bare
# UseStaticFiles() call.
COPY --from=widgets /frontend/Aegis/dist ./aegis
COPY --from=widgets /frontend/Console/dist ./console

ENV DOTNET_ENABLE_HTTP3="true"

EXPOSE 5002 5002/udp
EXPOSE 5001 5001/udp
EXPOSE 5000 5000/udp

# Consoles listen on ports of their own, one role each: admin, then the developer account console.
EXPOSE 8920
EXPOSE 8930

ENTRYPOINT ["dotnet", "Argon.Api.dll"]
