#requires -Version 5.1
param([string]$HelperPath)
$ErrorActionPreference='Stop'
if(-not $HelperPath){$HelperPath=Join-Path (Split-Path $PSScriptRoot -Parent) 'deploy/Lmc-Service.ps1'}
. ([ScriptBlock]::Create([IO.File]::ReadAllText($HelperPath)))
# Dados fictícios: nenhum evento do Windows é alterado.
function Get-WinEvent {
    param($FilterHashtable,$MaxEvents,$ErrorAction)
    [pscustomobject]@{ProviderName='.NET Runtime';Id=1026;Message="Application: Lmc.Web.exe`nSystem.Security.Cryptography.CryptographicException: Acesso negado.`nPassword=segredo-ficticio"}
    [pscustomobject]@{ProviderName='.NET Runtime';Id=1026;Message='Application: Outro.exe. Erro de outro programa.'}
}
$message=Get-LmcServiceFailure -Since (Get-Date) -Summary 'O serviço encerrou ao iniciar.'
if($message -notmatch 'CryptographicException' -or $message -notmatch 'Acesso negado' -or $message -match 'segredo-ficticio|Outro.exe'){throw 'Diagnóstico incorreto ou credencial exposta.'}
function Get-WinEvent {param($FilterHashtable,$MaxEvents,$ErrorAction) throw 'Sem acesso ao log'}
$message=Get-LmcServiceFailure -Since (Get-Date) -Summary 'Falha original.'
if(-not $message.StartsWith('Falha original.') -or $message -notmatch 'Aplicativo'){throw 'Falha original perdida quando o log não está disponível.'}
Write-Output 'OK: causa do serviço exibida, eventos alheios/credenciais omitidos e falha original preservada se o log estiver indisponível.'
