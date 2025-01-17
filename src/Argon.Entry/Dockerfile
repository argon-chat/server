FROM mcr.microsoft.com/dotnet/runtime:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

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
ENV Kestrel__Endpoints__Https__Certificate__Path="/origin.argon.gl.pem" \
    Kestrel__Endpoints__Https__Certificate__KeyPath="/origin.argon.gl.key"
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Argon.Entry.dll"]