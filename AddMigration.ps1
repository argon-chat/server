param(
    [string]$MigrationName
)

if (-not $MigrationName) {
    $MigrationName = Read-Host "Enter MigrationName"
}

dotnet ef migrations add $MigrationName `
    --project src/Argon.Core/Argon.Core.csproj `
    --startup-project src/Argon.Api/Argon.Api.csproj

