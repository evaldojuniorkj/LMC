$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
Write-Host 'Interface LMC: http://127.0.0.1:5080'
Write-Host 'Consulta, previa e correcao de entradas no banco local. Pressione Ctrl+C para encerrar.'
dotnet run --project src/Lmc.Web/Lmc.Web.csproj --no-launch-profile
