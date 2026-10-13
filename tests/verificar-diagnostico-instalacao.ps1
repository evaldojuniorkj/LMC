#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$AppPath,[Parameter(Mandatory=$true)][string]$ProtectedConfig)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

# Ensaio de leitura: usa uma configuração de desenvolvimento cujo certificado
# não é confiável. Não executa o restante do instalador nem registra serviços.
$workspace=Split-Path $PSScriptRoot -Parent
$installer=Join-Path $workspace 'deploy/Instalar-LMC.ps1'
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput([IO.File]::ReadAllText($installer),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Erro de sintaxe no instalador.'}
$diagnosticStatements=@($ast.EndBlock.Statements | Where-Object {
    $_.Extent.Text.StartsWith('$verification=') -or $_.Extent.Text.StartsWith('$verificationExitCode=') -or
    $_.Extent.Text.StartsWith('$verification |') -or $_.Extent.Text.StartsWith('if($verificationExitCode')
})
if($diagnosticStatements.Count -ne 4){throw 'Bloco de diagnóstico não localizado.'}
$diagnostic=[ScriptBlock]::Create(($diagnosticStatements | ForEach-Object {$_.Extent.Text}) -join [Environment]::NewLine)
$executable=(Resolve-Path -LiteralPath $AppPath).Path
$configPath=(Resolve-Path -LiteralPath $ProtectedConfig).Path
$failure=$null
try {& $diagnostic} catch {$failure=$_.Exception.Message}
if(-not $failure -or $failure -notmatch '-2146893019' -or $failure -notmatch '-TrustSqlCertificate' -or $failure -notmatch '1\)'){
    throw 'O instalador não preservou a causa do certificado e a saída 1 na exceção final.'
}
Write-Output "OK: causa do certificado preservada na mensagem final, PowerShell $($PSVersionTable.PSVersion). Nenhuma instalação ou gravação."
