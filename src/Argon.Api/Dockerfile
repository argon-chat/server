FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine-arm64v8 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine-arm64v8 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Argon.Api/Argon.Api.csproj", "src/Argon.Api/"]
COPY ["src/Argon.Grains/Argon.Grains.csproj", "src/Argon.Grains/"]
COPY ["src/Argon.Grains.Interfaces/Argon.Grains.Interfaces.csproj", "src/Argon.Grains.Interfaces/"]
COPY ["src/ServiceDefaults/ServiceDefaults.csproj", "src/ServiceDefaults/"]
RUN dotnet restore "src/Argon.Api/Argon.Api.csproj"
COPY . .
WORKDIR "/src/src/Argon.Api"
RUN dotnet build "Argon.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Argon.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
COPY src/Argon.Api/Migrations/*.sql /app/publish/Migrations/

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Argon.Api.dll"]
