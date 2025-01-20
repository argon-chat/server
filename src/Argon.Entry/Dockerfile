FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["src/Argon.Entry/Argon.Entry.csproj", "src/Argon.Entry/"]
COPY ["src/Argon.Api/Argon.Api.csproj", "src/Argon.Api/"]
COPY ["src/Argon.Contracts/Argon.Shared.csproj", "src/Argon.Contracts/"]
COPY ["src/ServiceDefaults/ServiceDefaults.csproj", "src/ServiceDefaults/"]
RUN dotnet restore "./src/Argon.Entry/Argon.Entry.csproj"
COPY . .
WORKDIR "/src/src/Argon.Entry"
RUN dotnet build "./Argon.Entry.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Argon.Entry.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV DOTNET_ENABLE_HTTP3="true"
ENTRYPOINT ["dotnet", "Argon.Entry.dll"]