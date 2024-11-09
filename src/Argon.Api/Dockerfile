FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Argon.Api/Argon.Api.csproj", "src/Argon.Api/"]
COPY ["src/Argon.Contracts/Argon.Contracts.csproj", "src/Argon.Contracts/"]
COPY ["src/Models/Models.csproj", "src/Models/"]
COPY ["src/ServiceDefaults/ServiceDefaults.csproj", "src/ServiceDefaults/"]
COPY ["src/Grains.Interfaces/Grains.Interfaces.csproj", "src/Grains.Interfaces/"]
COPY ["src/Services/Services.csproj", "src/Services/"]
COPY ["src/Grains/Grains.csproj", "src/Grains/"]
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
