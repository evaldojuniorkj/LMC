# Mantém configuração, certificado e chaves; não executa migrações no SQL Server.
[CmdletBinding()]
param([string]$Version='1.0.0')
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'Lmc-Service.ps1')
if($Version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.-]+)?$'){throw 'Versão inválida.'}
$service=Get-CimInstance Win32_Service -Filter "Name='LMC'"
if(-not $service){throw 'Serviço LMC não localizado.'}
$root=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'LMC'))
$destination=[IO.Path]::GetFullPath((Join-Path $root ($Version+'-'+(Get-Date -Format 'yyyyMMddHHmmss'))))
if(-not $destination.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Destino de atualização inválido.'}
$source=Join-Path $PSScriptRoot 'app';$config=Join-Path $env:ProgramData 'LMC\configuration.bin'
if(-not (Test-Path -LiteralPath (Join-Path $source 'Lmc.Web.exe'))){throw 'Extraia o pacote completo.'}
New-Item -ItemType Directory -Path $destination | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse
$exe=Join-Path $destination 'Lmc.Web.exe'
& $exe --ProtectedConfig $config --PrepareOperatorAccess true
if($LASTEXITCODE -ne 0){throw 'Não foi possível preparar o acesso dos operadores. O serviço atual foi mantido.'}
$verification=@(& $exe --ProtectedConfig $config --CheckInstallation true)
$verificationExitCode=$LASTEXITCODE
$verification | ForEach-Object {Write-Host $_}
if($verificationExitCode -ne 0){throw ('O serviço atual foi mantido. '+($verification -join [Environment]::NewLine)+" (Saída do verificador: $verificationExitCode)")}
Write-Host 'Preparando a permissão do certificado existente para a conta do serviço...'
& $exe --ProtectedConfig $config --PrepareServiceCertificate true
if($LASTEXITCODE -ne 0){throw 'O certificado HTTPS não pôde ser preparado. O serviço atual foi mantido.'}
$previous=$service.PathName
Stop-Service LMC
$serviceStarted=Get-Date
try{
    & sc.exe config LMC binPath= ('"'+$exe+'" --ProtectedConfig "'+$config+'"') | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Falha ao atualizar caminho do serviço.'}
    Get-NetFirewallRule -DisplayName 'LMC HTTPS - Rede local' | Get-NetFirewallApplicationFilter | Set-NetFirewallApplicationFilter -Program $exe
    Start-Service LMC;(Get-Service LMC).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
    $listening=$false
    for($attempt=0;$attempt -lt 20;$attempt++){
        $running=Get-CimInstance Win32_Service -Filter "Name='LMC'"
        if($running.State -eq 'Stopped'){throw 'A nova versão encerrou ao iniciar.'}
        if(Get-NetTCPConnection -State Listen -OwningProcess $running.ProcessId -ErrorAction SilentlyContinue){$listening=$true;break}
        Start-Sleep -Milliseconds 500
    }
    if(-not $listening){throw 'A nova versão não abriu a porta de atendimento.'}
    & $exe --ProtectedConfig $config --CheckServiceHttps true
    if($LASTEXITCODE -ne 0){throw 'A nova versão abriu a porta, mas não confirmou o HTTPS.'}
    Set-Content -LiteralPath (Join-Path $env:ProgramData 'LMC\previous-service-path.txt') -Value $previous
    Write-Host 'Atualização concluída. Versão anterior preservada no disco.'
}catch{
    $failure=Get-LmcServiceFailure -Since $serviceStarted -Summary $_.Exception.Message
    Stop-Service LMC -ErrorAction SilentlyContinue
    & sc.exe config LMC binPath= $previous | Out-Null
    if($previous -match '^"([^"]+)"'){Get-NetFirewallRule -DisplayName 'LMC HTTPS - Rede local' | Get-NetFirewallApplicationFilter | Set-NetFirewallApplicationFilter -Program $matches[1]}
    Start-Service LMC
    throw $failure
}
