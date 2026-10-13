[CmdletBinding()]
param([string]$Version='1.0.0')
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Versão inválida.'}
$workspace=Split-Path $PSScriptRoot -Parent
$output=Join-Path $workspace ('artifacts\LMC-'+$Version+'-win-x64-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$appPath=Join-Path $output 'app'
New-Item -ItemType Directory -Path $appPath | Out-Null
dotnet publish (Join-Path $workspace 'src/Lmc.Web/Lmc.Web.csproj') -c Release -r win-x64 --self-contained true -p:IsTransformWebConfigDisabled=true -p:DebugType=None -o $appPath --nologo
if($LASTEXITCODE -ne 0){throw 'Publicação não concluída.'}
if(Test-Path -LiteralPath (Join-Path $appPath 'appsettings.Local.json')){throw 'Configuração local não pode entrar no pacote.'}
# Um executável extraído não deve conectar automaticamente à base de desenvolvimento.
$defaults=@{ConnectionStrings=@{AtxDados=''};Deployment=@{NetworkAccess=$true;Label='Produção - configurar instalação'};AllowedHosts='localhost;127.0.0.1';Logging=@{LogLevel=@{Default='Information';'Microsoft.AspNetCore'='Warning'}}}
[IO.File]::WriteAllText((Join-Path $appPath 'appsettings.json'),($defaults|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
foreach($file in @('Instalar-LMC.ps1','Atualizar-LMC.ps1','Lmc-Service.ps1')){
    [IO.File]::WriteAllText((Join-Path $output $file),[IO.File]::ReadAllText((Join-Path $PSScriptRoot $file)),[Text.UTF8Encoding]::new($true))
}
Copy-Item -LiteralPath (Join-Path $workspace 'docs/producao.md') -Destination (Join-Path $output 'LEIA-ME.md')
$manifest=Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash,$_.FullName.Substring($output.Length+1) }
$manifest | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding UTF8
Compress-Archive -Path (Join-Path $output '*') -DestinationPath ($output+'.zip') -CompressionLevel Optimal
$hash=(Get-FileHash -LiteralPath ($output+'.zip') -Algorithm SHA256).Hash
Set-Content -LiteralPath ($output+'.zip.sha256') -Value $hash -Encoding ASCII
Write-Output $output
Write-Output ($output+'.zip')
Write-Output "SHA256: $hash"

# O instalador visual é um único executável, com runtime e pacote incorporados.
$setupOutput=Join-Path $workspace ('local\setup-publish-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
dotnet publish (Join-Path $workspace 'src/Lmc.Setup/Lmc.Setup.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true "-p:PayloadPath=$output.zip" -o $setupOutput --nologo
if($LASTEXITCODE -ne 0){throw 'O ZIP foi gerado, mas a publicação do instalador visual falhou.'}
$setupFile=$output+'-Instalar.exe'
Copy-Item -LiteralPath (Join-Path $setupOutput 'Instalar-LMC.exe') -Destination $setupFile
$setupHash=(Get-FileHash -LiteralPath $setupFile -Algorithm SHA256).Hash
Set-Content -LiteralPath ($setupFile+'.sha256') -Value $setupHash -Encoding ASCII
Write-Output $setupFile
Write-Output "SHA256 instalador: $setupHash"
