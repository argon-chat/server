FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base

RUN apt-get update && apt-get install -y \
    curl \
    apt-transport-https \
    gnupg \
    && curl -fsSL https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -o packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && apt-get update && apt-get install -y \
    libmsquic \
    libssl-dev \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["src/Argon.Api/Argon.Api.csproj", "src/Argon.Api/"]
COPY ["src/Argon.Contracts/Argon.Shared.csproj", "src/Argon.Contracts/"]
COPY ["src/Argon.Metrics/Argon.Metrics.csproj", "src/Argon.Metrics/"]
RUN dotnet restore "./src/Argon.Api/Argon.Api.csproj"
COPY . .
WORKDIR "/src/src/Argon.Api"
RUN dotnet build "./Argon.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Argon.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
COPY src/Argon.Api/Migrations/*.sql /app/publish/Migrations/

FROM base AS final
#ENV Kestrel__Endpoints__Https__Certificate__Path="/origin.argon.gl.pem" \
#    Kestrel__Endpoints__Https__Certificate__KeyPath="/origin.argon.gl.key"
WORKDIR /app
COPY --from=publish /app/publish .
ENV DOTNET_ENABLE_HTTP3="true"
EXPOSE 5002
EXPOSE 5002/udp
EXPOSE 5001
EXPOSE 5001/udp
EXPOSE 5000
EXPOSE 5000/udp
ENTRYPOINT ["dotnet", "Argon.Api.dll"]