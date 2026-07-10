$answer = Read-Host 'Delete all Harmony Resolver development data? Type RESET'
if ($answer -ne 'RESET') { throw 'Reset cancelled' }
docker compose down --volumes
