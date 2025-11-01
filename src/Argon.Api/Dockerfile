FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base

WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["src/Argon.Api/Argon.Api.csproj", "src/Argon.Api/"]
COPY ["src/Argon.Core/Argon.Core.csproj", "src/Argon.Core/"]
COPY ["src/Argon.CodeGen/Argon.Ion.CodeGen.csproj", "src/Argon.CodeGen/"]
COPY ["src/Argon.Cassandra/Argon.Cassandra.csproj", "src/Argon.Cassandra/"]
RUN dotnet restore "./src/Argon.Api/Argon.Api.csproj"
COPY . .
WORKDIR "/src/src/Argon.Api"
RUN dotnet build "./Argon.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Argon.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
COPY src/Argon.Core/Migrations/*.sql /app/publish/Migrations/

FROM base AS final
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