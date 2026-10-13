#requires -Version 5.1
# Executar no servidor de destino, no PowerShell como Administrador.
# Nenhuma rotina deste instalador altera dados ou estrutura do SQL Server.
[CmdletBinding()]
param(
    [string]$SqlServer,
    [string]$ServerAddress,
    [string]$SqlUser='sa',
    [string]$Database='ATXDADOS',
    [string]$Operator='operador',
    [ValidateRange(1024,65535)][int]$Port=5443,
    [string]$CertificatePath,
    [switch]$TrustSqlCertificate,
    [switch]$InputFromStdin,
    [switch]$TrustLocalHttpsCertificate,
    [switch]$CreateDesktopShortcut
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Lmc-Service.ps1')
function New-LmcSqlConnectionString {
    param([string]$Server,[string]$Username,[Security.SecureString]$Password,[bool]$TrustCertificate,[string]$Database='ATXDADOS')
    $builder=[System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    try {
        # PowerShell adapta o builder como dicionário: use as palavras-chave SQL,
        # pois atribuir .DataSource é interpretado como a chave inválida DataSource.
        $builder['Data Source']=$Server
        $builder['Initial Catalog']=$Database
        $builder['User ID']=$Username
        $builder['Password']=[Net.NetworkCredential]::new('',$Password).Password
        $builder['Encrypt']=$true
        $builder['TrustServerCertificate']=$TrustCertificate
        $builder['Connect Timeout']=10
        $builder['Application Name']='LMC 1.0.0'
        return $builder.ConnectionString
    } finally {$builder.Clear()}
}
if(-not [Environment]::Is64BitOperatingSystem -or -not [Environment]::Is64BitProcess){throw 'Use o PowerShell de 64 bits em um Windows de 64 bits.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Abra o PowerShell como Administrador.'}
if(Get-Service -Name LMC -ErrorAction SilentlyContinue){throw 'O serviço LMC já existe. Use Atualizar-LMC.ps1 para manter a configuração.'}
if($InputFromStdin){
    [Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
    [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
    # Recebido somente pelo pipe privado do instalador visual; nunca em argumentos ou arquivo de texto.
    $inputData=[Console]::In.ReadToEnd() | ConvertFrom-Json
    $SqlServer=[string]$inputData.SqlServer;$ServerAddress=[string]$inputData.ServerAddress
    $SqlUser=[string]$inputData.SqlUser;$Operator=[string]$inputData.Operator;$Port=[int]$inputData.Port
    $Database=[string]$inputData.Database
    if($Port -lt 1024 -or $Port -gt 65535){throw 'A porta deve estar entre 1024 e 65535.'}
    $TrustSqlCertificate=[bool]$inputData.TrustSqlCertificate
    $TrustLocalHttpsCertificate=[bool]$inputData.TrustLocalHttpsCertificate
    $CreateDesktopShortcut=[bool]$inputData.CreateDesktopShortcut
    $sqlPassword=ConvertTo-SecureString ([string]$inputData.SqlPassword) -AsPlainText -Force
    $operatorPassword=ConvertTo-SecureString ([string]$inputData.OperatorPassword) -AsPlainText -Force
    $operatorConfirm=ConvertTo-SecureString ([string]$inputData.OperatorPasswordConfirm) -AsPlainText -Force
    $inputData=$null
}
if(-not $SqlServer){$SqlServer=Read-Host 'Servidor SQL (ex.: localhost, SERVIDOR\INSTANCIA ou 192.168.1.10)'}
if(-not $ServerAddress){$ServerAddress=Read-Host 'Nome ou IP fixo deste servidor, usado pelos computadores da rede'}
if([string]::IsNullOrWhiteSpace($SqlServer)){throw 'Informe o servidor SQL.'}
if([string]::IsNullOrWhiteSpace($Database) -or $Database.Length -gt 128){throw 'Informe o nome do banco existente, com até 128 caracteres.'}
if($ServerAddress -notmatch '^[a-zA-Z0-9][a-zA-Z0-9.-]*$'){throw 'Informe somente nome DNS ou endereço IPv4, sem http, porta ou caminho.'}
if($Operator -notmatch '^[a-zA-Z0-9._-]{3,64}$'){throw 'O operador deve ter de 3 a 64 letras, números, pontos, sublinhados ou hífens.'}
if(-not $InputFromStdin){
Write-Host "1/2 - Conexão com o banco ${Database}: informe a senha que o usuário SQL '$SqlUser' já utiliza."
$sqlPassword=Read-Host 'Senha existente do SQL Server' -AsSecureString
Write-Host "2/2 - Acesso à nossa interface LMC: vamos criar o usuário '$Operator'."
Write-Host 'Escolha uma senha NOVA para entrar pelo navegador. Essa senha não altera o acesso ao SQL Server nem ao Xpert.'
$operatorPassword=Read-Host 'Nova senha da interface LMC (mínimo 10 caracteres)' -AsSecureString
$operatorConfirm=Read-Host 'Repita a NOVA senha da interface LMC' -AsSecureString
}
function Plain([Security.SecureString]$Value){return [Net.NetworkCredential]::new('',$Value).Password}
$operatorText=Plain $operatorPassword
if($operatorText.Length -lt 10 -or $operatorText.Length -gt 256 -or $operatorText -cne (Plain $operatorConfirm)){throw 'As senhas da interface devem ser iguais e ter de 10 a 256 caracteres.'}
$connectionString=New-LmcSqlConnectionString -Server $SqlServer -Username $SqlUser -Password $sqlPassword -TrustCertificate ([bool]$TrustSqlCertificate) -Database $Database

$programRoot=Join-Path $env:ProgramFiles 'LMC'
$dataRoot=Join-Path $env:ProgramData 'LMC'
foreach($folder in @($programRoot,$dataRoot)){
    if((Test-Path -LiteralPath $folder) -and -not (Test-Path -LiteralPath (Join-Path $folder '.lmc-installation'))){throw 'Já existe uma pasta LMC não reconhecida pelo instalador. Confira o destino antes de continuar.'}
}
$source=Join-Path $PSScriptRoot 'app'
if(-not (Test-Path -LiteralPath (Join-Path $source 'Lmc.Web.exe'))){throw 'Extraia o pacote completo antes de executar o instalador.'}
# Diretórios com ACL restrita; o serviço recebe somente leitura, exceto nas chaves de sessão.
function Restrict-Folder([string]$Path,[bool]$Writable=$false){
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    foreach($entry in @(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'),@('S-1-5-19',$(if($Writable){'Modify'}else{'ReadAndExecute'})))){
        $sid=[Security.Principal.SecurityIdentifier]::new($entry[0])
        $rule=[Security.AccessControl.FileSystemAccessRule]::new($sid,[Security.AccessControl.FileSystemRights]$entry[1],'ContainerInherit,ObjectInherit','None','Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
Write-Host 'Preparando pastas protegidas e copiando o programa...'
New-Item -ItemType Directory -Path $programRoot,$dataRoot -Force | Out-Null
Restrict-Folder $programRoot;Restrict-Folder $dataRoot
foreach($folder in @($programRoot,$dataRoot)){Set-Content -LiteralPath (Join-Path $folder '.lmc-installation') -Value 'LMC-DEPLOY-V1'}
$release=Join-Path $programRoot '1.0.0'
New-Item -ItemType Directory -Path $release -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $release -Recurse -Force
$keyPath=Join-Path $dataRoot 'keys';New-Item -ItemType Directory -Path $keyPath -Force | Out-Null;Restrict-Folder $keyPath $true

$certificateFile=Join-Path $dataRoot 'https.pfx'
$clientCertificate=Join-Path $PSScriptRoot 'LMC-certificado-publico.cer'
Write-Host 'Preparando o certificado HTTPS da interface...'
if($CertificatePath){
    $certificatePassword=Read-Host 'Senha do certificado PFX' -AsSecureString
    Copy-Item -LiteralPath (Resolve-Path -LiteralPath $CertificatePath).Path -Destination $certificateFile -Force
}else{
    $certificatePassword=ConvertTo-SecureString ([Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')) -AsPlainText -Force
    $parsedIp=$null
    $san=if([Net.IPAddress]::TryParse($ServerAddress,[ref]$parsedIp)){"2.5.29.17={text}IPAddress=$ServerAddress&DNS=localhost"}else{"2.5.29.17={text}DNS=$ServerAddress&DNS=localhost"}
    $cert=New-SelfSignedCertificate -Subject "CN=$ServerAddress" -FriendlyName 'LMC HTTPS' -CertStoreLocation 'Cert:\LocalMachine\My' -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(2) -TextExtension @($san,'2.5.29.37={text}1.3.6.1.5.5.7.3.1')
    Export-PfxCertificate -Cert $cert -FilePath $certificateFile -Password $certificatePassword -Force | Out-Null
    Export-Certificate -Cert $cert -FilePath $clientCertificate -Force | Out-Null
    Export-Certificate -Cert $cert -FilePath (Join-Path $dataRoot 'LMC-certificado-publico.cer') -Force | Out-Null
    Write-Host "Certificado público para os computadores clientes: $clientCertificate"
    Write-Host "Impressão digital para conferir: $($cert.Thumbprint)"
}
$salt=New-Object byte[] 16;$rng=[Security.Cryptography.RandomNumberGenerator]::Create();$rng.GetBytes($salt);$rng.Dispose()
$derive=[Security.Cryptography.Rfc2898DeriveBytes]::new($operatorText,$salt,600000,[Security.Cryptography.HashAlgorithmName]::SHA256)
$hash='pbkdf2-sha256:600000:'+ [Convert]::ToBase64String($salt)+':'+[Convert]::ToBase64String($derive.GetBytes(32));$derive.Dispose();$operatorText=$null
$config=@{
    ConnectionStrings=@{AtxDados=$connectionString};Urls="https://0.0.0.0:$Port";AllowedHosts="$ServerAddress;localhost;127.0.0.1"
    Deployment=@{NetworkAccess=$true;Label='Produção · rede local';KeyDirectory=$keyPath}
    Access=@{Networks=@('10.0.0.0/8','172.16.0.0/12','192.168.0.0/16');Users=@(@{Username=$Operator;PasswordHash=$hash})}
    Kestrel=@{Certificates=@{Default=@{Path=$certificateFile;Password=(Plain $certificatePassword)}}}
}
Add-Type -AssemblyName System.Security
$bytes=[Text.Encoding]::UTF8.GetBytes(($config | ConvertTo-Json -Depth 8))
$protected=[Security.Cryptography.ProtectedData]::Protect($bytes,[Text.Encoding]::UTF8.GetBytes('LMC-DEPLOY-V1'),[Security.Cryptography.DataProtectionScope]::LocalMachine)
$configPath=Join-Path $dataRoot 'configuration.bin';[IO.File]::WriteAllBytes($configPath,$protected)
[Array]::Clear($bytes,0,$bytes.Length);$config=$null;$connectionString=$null;$sqlPassword.Dispose();$operatorPassword.Dispose();$operatorConfirm.Dispose();$certificatePassword.Dispose()
$executable=Join-Path $release 'Lmc.Web.exe'
& $executable --ProtectedConfig $configPath --PrepareOperatorAccess true
if($LASTEXITCODE -ne 0){throw 'Não foi possível preparar o acesso dos operadores. O serviço não foi instalado.'}
Write-Host "Verificando conexão e estrutura existente de $Database (somente leitura)..."
$verification=@(& $executable --ProtectedConfig $configPath --CheckInstallation true)
$verificationExitCode=$LASTEXITCODE
$verification | ForEach-Object {Write-Host $_}
if($verificationExitCode -ne 0){throw ('O serviço não foi instalado. '+($verification -join [Environment]::NewLine)+" (Saída do verificador: $verificationExitCode)")}
Write-Host 'Preparando a permissão do certificado para a conta do serviço...'
& $executable --ProtectedConfig $configPath --PrepareServiceCertificate true
if($LASTEXITCODE -ne 0){throw 'O certificado HTTPS não pôde ser preparado. O serviço não foi instalado.'}
$binary='"'+$executable+'" --ProtectedConfig "'+$configPath+'"'
$serviceCreated=$false;$firewallCreated=$false
$serviceStarted=Get-Date
try {
Write-Host 'Registrando o serviço LMC e a regra da rede local...'
New-Service -Name LMC -DisplayName 'LMC - Controle de combustíveis' -Description 'Interface de notas e LMC no banco existente do posto' -BinaryPathName $binary -StartupType Automatic | Out-Null
$serviceCreated=$true
& sc.exe config LMC obj= 'NT AUTHORITY\LocalService' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Não foi possível configurar a identidade do serviço.'}
& sc.exe failure LMC reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
if($LASTEXITCODE -ne 0){throw 'Não foi possível configurar a recuperação do serviço.'}
if(-not [Diagnostics.EventLog]::SourceExists('Lmc.Web')){[Diagnostics.EventLog]::CreateEventSource('Lmc.Web','Application')}
if(Get-NetFirewallRule -DisplayName 'LMC HTTPS - Rede local' -ErrorAction SilentlyContinue){throw 'Já existe uma regra de firewall LMC. Confira a configuração anterior antes de continuar.'}
New-NetFirewallRule -DisplayName 'LMC HTTPS - Rede local' -Direction Inbound -Action Allow -Program $executable -Protocol TCP -LocalPort $Port -RemoteAddress LocalSubnet -Profile Domain,Private | Out-Null
$firewallCreated=$true
Start-Service LMC
Write-Host 'Aguardando o serviço e a porta HTTPS...'
(Get-Service LMC).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
$listening=$false
for($attempt=0;$attempt -lt 20;$attempt++){
    $running=Get-CimInstance Win32_Service -Filter "Name='LMC'"
    if($running.State -eq 'Stopped'){throw 'O serviço encerrou ao iniciar. Consulte o log Aplicativo do Windows.'}
    if(Get-NetTCPConnection -State Listen -LocalPort $Port -OwningProcess $running.ProcessId -ErrorAction SilentlyContinue){$listening=$true;break}
    Start-Sleep -Milliseconds 500
}
if(-not $listening){throw 'O serviço não abriu a porta HTTPS. Confira certificado, porta e log Aplicativo.'}
& $executable --ProtectedConfig $configPath --CheckServiceHttps true
if($LASTEXITCODE -ne 0){throw 'A porta abriu, mas o serviço não confirmou o HTTPS com o certificado desta instalação.'}
}catch{
    $failure=Get-LmcServiceFailure -Since $serviceStarted -Summary $_.Exception.Message
    if($serviceCreated){Stop-Service LMC -ErrorAction SilentlyContinue;& sc.exe delete LMC | Out-Null}
    if($firewallCreated){Get-NetFirewallRule -DisplayName 'LMC HTTPS - Rede local' -ErrorAction SilentlyContinue | Remove-NetFirewallRule}
    throw $failure
}
Write-Host "Instalação concluída. Endereço: https://${ServerAddress}:$Port"
if($TrustLocalHttpsCertificate -and -not $CertificatePath){
    try {
        Import-Certificate -FilePath $clientCertificate -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
        Write-Host 'Certificado deste LMC configurado para acesso neste computador.'
    }catch{Write-Host 'O serviço foi instalado, mas o certificado HTTPS precisa ser configurado neste computador.'}
}
if($CreateDesktopShortcut){
    try {
        $shortcut=Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Abrir LMC.url'
        [IO.File]::WriteAllText($shortcut,"[InternetShortcut]`r`nURL=https://${ServerAddress}:$Port`r`n",[Text.UTF8Encoding]::new($false))
        Write-Host 'Atalho Abrir LMC criado na área de trabalho.'
    }catch{Write-Host 'O serviço foi instalado, mas não foi possível criar o atalho na área de trabalho.'}
}
Write-Host 'Instale/confira o certificado nos clientes conforme o guia. O instalador não alterou o banco.'
