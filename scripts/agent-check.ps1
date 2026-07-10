$ErrorActionPreference = 'Stop'
dotnet restore Harmony.Resolver.slnx
dotnet build Harmony.Resolver.slnx --no-restore --configuration Release
dotnet test Harmony.Resolver.slnx --no-build --configuration Release
docker compose config --quiet
