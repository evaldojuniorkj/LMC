#requires -Version 5.1
[CmdletBinding()]
param([string]$InstallerPath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if(-not $InstallerPath){$InstallerPath=Join-Path (Split-Path $PSScriptRoot -Parent) 'deploy/Instalar-LMC.ps1'}

# Carrega somente a função real de conexão, sem instalar serviço, certificado
# ou executar qualquer acesso ao banco. Credenciais abaixo são dados fictícios.
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput([IO.File]::ReadAllText($InstallerPath),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Erro de sintaxe no instalador.'}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-LmcSqlConnectionString'},$true)
if(-not $definition){throw 'Função de conexão não encontrada no instalador.'}
. ([ScriptBlock]::Create($definition.Extent.Text))
$script:checks=0
function Check([bool]$Condition,[string]$Label){
    if(-not $Condition){throw "FALHOU: $Label"}
    $script:checks++;Write-Output "OK: $Label"
}
$plain=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Plain'},$true)
if(-not $plain){throw 'Função de leitura da senha não encontrada.'}
. ([ScriptBlock]::Create($plain.Extent.Text))
$passwordCheck=$ast.Find({param($node) $node -is [Management.Automation.Language.IfStatementAst] -and $node.Extent.Text.StartsWith('if($operatorText.Length')},$true)
if(-not $passwordCheck){throw 'Validação da senha não encontrada.'}
$validatePassword=[ScriptBlock]::Create($passwordCheck.Extent.Text)
foreach($passwordCase in @(
    @{Value='123456789';Confirm='123456789';Accept=$false},
    @{Value='1234567890';Confirm='1234567890';Accept=$true},
    @{Value=('a'*256);Confirm=('a'*256);Accept=$true},
    @{Value=('a'*257);Confirm=('a'*257);Accept=$false},
    @{Value='Abcdefghij';Confirm='abcdefghij';Accept=$false}
)){
    $operatorText=$passwordCase.Value
    $operatorConfirm=ConvertTo-SecureString $passwordCase.Confirm -AsPlainText -Force
    try {
        $accepted=$true
        try {& $validatePassword} catch {$accepted=$false}
        Check ($accepted -eq $passwordCase.Accept) 'limites de 10 a 256 caracteres e confirmação exata da senha'
    } finally {$operatorText=$null;$operatorConfirm.Dispose()}
}
$cases=@(
    @{Server='localhost';Username='sa';Password='senha-de-teste';Trust=$false},
    @{Server='SERVIDOR\INSTANCIA';Username='operador-teste';Password='aspas";igual= e espaço ';Trust=$true},
    @{Server='tcp:192.168.1.10,1433';Username='teste;"=';Password=" áéç'`";= `$ texto ";Trust=$false}
)
foreach($case in $cases){
    $secure=ConvertTo-SecureString $case.Password -AsPlainText -Force
    $parsed=$null;$serialized=$null
    try {
        $serialized=New-LmcSqlConnectionString -Server $case.Server -Username $case.Username -Password $secure -TrustCertificate $case.Trust
        Check ($serialized -is [string]) 'uma única string de conexão é retornada'
        # Também faz o percurso JSON utilizado na configuração protegida.
        $config=@{ConnectionStrings=@{AtxDados=$serialized}} | ConvertTo-Json -Depth 3 | ConvertFrom-Json
        $parsed=[System.Data.SqlClient.SqlConnectionStringBuilder]::new($config.ConnectionStrings.AtxDados)
        Check ($parsed.DataSource -ceq $case.Server) 'servidor, instância e porta são preservados'
        Check ($parsed.InitialCatalog -ceq 'ATXDADOS') 'destino é o banco existente ATXDADOS'
        Check ($parsed.UserID -ceq $case.Username -and -not $parsed.IntegratedSecurity) 'autenticação SQL preservada'
        Check ($parsed.Password -ceq $case.Password) 'senha com caracteres especiais é preservada'
        Check ($parsed.Encrypt -and $parsed.TrustServerCertificate -eq $case.Trust) 'criptografia e validação de certificado respeitam a opção'
        Check ($parsed.ConnectTimeout -eq 10 -and $parsed.ApplicationName -eq 'LMC 1.0.0') 'tempo limite e identificação da aplicação preservados'
        Check (-not $parsed.PersistSecurityInfo) 'conexão não persiste credenciais após abertura'
    } finally {
        if($parsed){$parsed.Clear()}
        $serialized=$null;$config=$null;$secure.Dispose()
    }
}
foreach($database in @('ATXDADOS_CLIENTE','ATX;DADOS"=TESTE')){
    $secure=ConvertTo-SecureString 'senha-ficticia' -AsPlainText -Force
    try {
        $serialized=New-LmcSqlConnectionString -Server 'localhost' -Username 'teste' -Password $secure -TrustCertificate $true -Database $database
        $parsed=[System.Data.SqlClient.SqlConnectionStringBuilder]::new($serialized)
        Check ($parsed.InitialCatalog -ceq $database) 'nome de banco editado preservado inclusive com caracteres especiais'
    } finally {$parsed.Clear();$serialized=$null;$secure.Dispose()}
}
Write-Output "$script:checks verificações passaram no PowerShell $($PSVersionTable.PSVersion). Nenhuma instalação ou acesso ao banco."
