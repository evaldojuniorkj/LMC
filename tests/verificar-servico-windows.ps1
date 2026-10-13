#requires -Version 7.0
#requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$AppDirectory,[switch]$MachineCertificate,[switch]$PrepareCertificate,[switch]$CheckLocalhost,[switch]$CheckPasswordChange)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
# Serviço temporário, certificado de teste e porta aleatória. CheckLocalhost
# reproduz a escuta da instalação (rede + loopback), sem criar regra de firewall.
# Não usa o serviço LMC, as credenciais do posto, SQL, firewall ou raízes confiáveis.
$id=[Guid]::NewGuid().ToString('N')
$serviceName='LMC-QA-'+$id
$parent=[IO.Path]::GetFullPath((Join-Path $env:ProgramData 'LMC-service-tests'))
$root=[IO.Path]::GetFullPath((Join-Path $parent $id))
$serviceCreated=$false;$client=$null;$cert=$null;$rsa=$null;$machineKey=$null
function Set-TestAcl([string]$Path,[bool]$Writable=$false){
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    foreach($entry in @(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'),@('S-1-5-19',$(if($Writable){'Modify'}else{'ReadAndExecute'})))){
        $sid=[Security.Principal.SecurityIdentifier]::new($entry[0])
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,[Security.AccessControl.FileSystemRights]$entry[1],'ContainerInherit,ObjectInherit','None','Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    Set-TestAcl $root
    Copy-Item -Path (Join-Path (Resolve-Path -LiteralPath $AppDirectory).Path '*') -Destination $root -Recurse
    $keys=Join-Path $root 'keys';New-Item -ItemType Directory -Path $keys | Out-Null;Set-TestAcl $keys $true
    if($MachineCertificate){
        $keyOptions=[Security.Cryptography.CngKeyCreationParameters]::new()
        $keyOptions.KeyCreationOptions=[Security.Cryptography.CngKeyCreationOptions]::MachineKey
        $keyOptions.ExportPolicy=[Security.Cryptography.CngExportPolicies]::AllowExport
        $machineKey=[Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::Rsa,('lmc-qa-'+$id),$keyOptions)
        $rsa=[Security.Cryptography.RSACng]::new($machineKey)
    }else{$rsa=[Security.Cryptography.RSA]::Create(2048)}
    $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost',$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san=[Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new();$san.AddDnsName('localhost');$san.AddIpAddress([Net.IPAddress]::Loopback)
    $lan=$null
    if($CheckLocalhost){
        $lan=[Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object {$_.OperationalStatus -eq 'Up' -and $_.NetworkInterfaceType -ne 'Loopback'} | ForEach-Object {$_.GetIPProperties().UnicastAddresses} | Where-Object {$_.Address.AddressFamily -eq 'InterNetwork' -and -not $_.Address.ToString().StartsWith('169.254.')} | Select-Object -First 1
        if($lan){$san.AddIpAddress($lan.Address)}
    }
    $request.CertificateExtensions.Add($san.Build())
    $cert=$request.CreateSelfSigned([DateTimeOffset]::Now.AddMinutes(-1),[DateTimeOffset]::Now.AddHours(1))
    if($MachineCertificate){
        $store=[Security.Cryptography.X509Certificates.X509Store]::new('My','LocalMachine');$store.Open('ReadWrite')
        try{$store.Add($cert)}finally{$store.Dispose()}
    }
    $certPath=Join-Path $root 'test.pfx';$certPassword=[Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllBytes($certPath,$cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx,$certPassword))
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
    $password=[Guid]::NewGuid().ToString('N');$salt=[Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $hash=[Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($password,$salt,600000,[Security.Cryptography.HashAlgorithmName]::SHA256,32)
    $config=@{
        Urls="https://127.0.0.1:$port";AllowedHosts='localhost;127.0.0.1'
        Deployment=@{NetworkAccess=$true;Label='Teste isolado de serviço';KeyDirectory=$keys}
        Access=@{Networks=@('127.0.0.0/8');Users=@(@{Username='teste';PasswordHash=('pbkdf2-sha256:600000:'+ [Convert]::ToBase64String($salt)+':'+[Convert]::ToBase64String($hash))})}
        Kestrel=@{Certificates=@{Default=@{Path=$certPath;Password=$certPassword}}}
        ConnectionStrings=@{AtxDados='Server=tcp:127.0.0.1,1;Database=TESTE_SEM_BANCO;User ID=ficticio;Password=ficticia;Connect Timeout=1'}
    }
    if($CheckLocalhost){
        $config.Urls="https://0.0.0.0:$port"
        if($lan){$config.AllowedHosts+=';'+$lan.Address;$config.Access.Networks+=($lan.Address.ToString()+'/32')}
    }
    $bytes=[Text.Encoding]::UTF8.GetBytes(($config | ConvertTo-Json -Depth 8))
    $configPath=Join-Path $root 'configuration.bin'
    [IO.File]::WriteAllBytes($configPath,[Security.Cryptography.ProtectedData]::Protect($bytes,[Text.Encoding]::UTF8.GetBytes('LMC-DEPLOY-V1'),[Security.Cryptography.DataProtectionScope]::LocalMachine))
    [Array]::Clear($bytes,0,$bytes.Length)
    $exe=Join-Path $root 'Lmc.Web.exe';$applicationArgs=@()
    if(-not (Test-Path -LiteralPath $exe)){$exe=(Get-Command dotnet).Source;$applicationArgs=@((Join-Path $root 'Lmc.Web.dll'))}
    & $exe @applicationArgs --ProtectedConfig $configPath --PrepareOperatorAccess true
    if($LASTEXITCODE -ne 0){throw 'Falha ao preparar acesso dos operadores no teste.'}
    if($PrepareCertificate){
        & $exe @applicationArgs --ProtectedConfig $configPath --PrepareServiceCertificate true
        if($LASTEXITCODE -ne 0){throw 'Falha ao preparar certificado do teste.'}
        $store=[Security.Cryptography.X509Certificates.X509Store]::new('My','LocalMachine');$store.Open('ReadOnly')
        try{
            $saved=$store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,$cert.Thumbprint,$false)[0]
            $savedKey=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($saved)
            if(-not $savedKey.Key.IsMachineKey){throw 'Certificado não foi preparado no computador.'}
            if($machineKey -and $savedKey.Key.UniqueName -ne $machineKey.UniqueName){throw 'A chave existente foi trocada desnecessariamente.'}
            $keyFile=Join-Path $env:ProgramData ('Microsoft\Crypto\Keys\'+$savedKey.Key.UniqueName)
            $rights=(Get-Acl -LiteralPath $keyFile).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) | Where-Object {$_.IdentityReference.Value -eq 'S-1-5-19' -and $_.AccessControlType -eq 'Allow'}
            if(-not $rights -or ($rights.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write)){throw 'Permissão da chave do serviço precisa ser somente leitura.'}
            $savedKey.Dispose();$saved.Dispose()
        }finally{$store.Dispose()}
    }
    $binary='"'+$exe+'" '+(($applicationArgs | ForEach-Object {'"'+$_+'"'}) -join ' ')+' --ProtectedConfig "'+$configPath+'"'
    New-Service -Name $serviceName -BinaryPathName $binary -StartupType Manual | Out-Null;$serviceCreated=$true
    & sc.exe config $serviceName obj= 'NT AUTHORITY\LocalService' | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Falha ao configurar LocalService no teste.'}
    $started=Get-Date
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(20))
    # Confia exclusivamente no certificado deste teste, sem alterar o Windows.
    Add-Type -TypeDefinition 'public static class LmcTestTls { public static System.Net.Http.HttpClientHandler Handler(string thumbprint) { return new System.Net.Http.HttpClientHandler { UseProxy=false, ServerCertificateCustomValidationCallback=(request,cert,chain,errors)=>cert?.Thumbprint==thumbprint }; } }'
    $client=[Net.Http.HttpClient]::new([LmcTestTls]::Handler($cert.Thumbprint));$client.BaseAddress="https://127.0.0.1:$port";$client.Timeout=[TimeSpan]::FromSeconds(2)
    $ready=$false
    for($i=0;$i -lt 40;$i++){
        try{$body=$client.GetStringAsync('/health/live').GetAwaiter().GetResult() | ConvertFrom-Json;if($body.status -eq 'ok'){$ready=$true;break}}catch{}
        if((Get-Service -Name $serviceName).Status -eq 'Stopped'){break}
        Start-Sleep -Milliseconds 250
    }
    if(-not $ready){
        $events=Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=$started.AddSeconds(-1)} -ErrorAction SilentlyContinue | Where-Object {$_.Message -match 'Lmc.Web|Lmc-service-tests'} | Select-Object -First 3
        foreach($event in $events){Write-Output ($event.ProviderName+': '+$event.Message)}
        throw 'Serviço isolado não respondeu ao HTTPS.'
    }
    if($CheckLocalhost){
        foreach($address in @('localhost','127.0.0.1')){
            $body=$client.GetStringAsync("https://${address}:$port/health/live").GetAwaiter().GetResult() | ConvertFrom-Json
            if($body.status -ne 'ok'){throw "Falha no acesso por $address"}
        }
        # Força o caminho IPv6 usando SNI e Host localhost, como faz o navegador.
        # A raiz de teste é validada só neste cliente, sem importar no Windows.
        Add-Type -TypeDefinition @'
public static class LmcTestLocalhost {
    public static async System.Threading.Tasks.Task Check(string host, string address, int port, byte[] rawCert) {
        using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(rawCert);
        using var handler = new System.Net.Http.SocketsHttpHandler { UseProxy=false };
        handler.SslOptions.CertificateChainPolicy = new System.Security.Cryptography.X509Certificates.X509ChainPolicy {
            TrustMode=System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust,
            RevocationMode=System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
        };
        handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(cert);
        handler.ConnectCallback = async (context, ct) => {
            var ip = System.Net.IPAddress.Parse(address);
            var socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try { await socket.ConnectAsync(ip,port,ct); return new System.Net.Sockets.NetworkStream(socket,true); }
            catch { socket.Dispose(); throw; }
        };
        using var client = new System.Net.Http.HttpClient(handler) { Timeout=System.TimeSpan.FromSeconds(5) };
        using var result=await client.GetAsync($"https://{host}:{port}/health/live");
        result.EnsureSuccessStatusCode();
        if (!(await result.Content.ReadAsStringAsync()).Contains("\"status\":\"ok\"")) throw new System.Exception("Resposta inesperada no teste HTTPS.");
    }
}
'@
        $publicCert=$cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert)
        [LmcTestLocalhost]::Check('localhost','127.0.0.1',$port,$publicCert).GetAwaiter().GetResult() | Out-Null
        if([Net.Sockets.Socket]::OSSupportsIPv6){[LmcTestLocalhost]::Check('localhost','::1',$port,$publicCert).GetAwaiter().GetResult() | Out-Null}
        Write-Output 'OK: localhost em IPv4 e IPv6 respondeu; nome localhost do certificado validado.'
        if($lan){
            [LmcTestLocalhost]::Check($lan.Address.ToString(),$lan.Address.ToString(),$port,$publicCert).GetAwaiter().GetResult() | Out-Null
            Write-Output 'OK: IP da rede respondeu com nome/IP e cadeia do certificado válidos no cliente de teste.'
        }
        $badHost=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,"https://localhost:$port/health/live");$badHost.Headers.Host='nao-autorizado.local'
        $reply=$client.SendAsync($badHost).GetAwaiter().GetResult()
        if([int]$reply.StatusCode -ne 400){throw 'Filtro de host foi removido indevidamente.'}
        $reply.Dispose();$badHost.Dispose()
        Write-Output 'OK: host não autorizado continua bloqueado.'
    }
    if($PrepareCertificate){
        & $exe @applicationArgs --ProtectedConfig $configPath --CheckServiceHttps true
        if($LASTEXITCODE -ne 0){throw 'Diagnóstico HTTPS real do instalador falhou.'}
        $wrong=$request.CreateSelfSigned([DateTimeOffset]::Now.AddMinutes(-1),[DateTimeOffset]::Now.AddHours(1))
        try{[IO.File]::WriteAllBytes((Join-Path $root 'wrong.pfx'),$wrong.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx,$certPassword))}finally{$wrong.Dispose()}
        $config.Kestrel.Certificates.Default.Path=Join-Path $root 'wrong.pfx'
        $bytes=[Text.Encoding]::UTF8.GetBytes(($config|ConvertTo-Json -Depth 8));$wrongConfig=Join-Path $root 'wrong-config.bin'
        [IO.File]::WriteAllBytes($wrongConfig,[Security.Cryptography.ProtectedData]::Protect($bytes,[Text.Encoding]::UTF8.GetBytes('LMC-DEPLOY-V1'),[Security.Cryptography.DataProtectionScope]::LocalMachine))
        [Array]::Clear($bytes,0,$bytes.Length)
        $unexpected=@(& $exe @applicationArgs --ProtectedConfig $wrongConfig --CheckServiceHttps true)
        if($LASTEXITCODE -ne 1){throw 'Diagnóstico aceitou certificado de outra instalação.'}
        Write-Output 'OK: diagnóstico recusou um certificado diferente; permissão concedida à chave é somente leitura.'
    }
    $session=$client.GetStringAsync('/api/session').GetAwaiter().GetResult() | ConvertFrom-Json
    if(-not $session.requiresLogin -or $session.authenticated){throw 'Proteção do acesso não aplicada.'}
    $client.DefaultRequestHeaders.Add('X-LMC-CSRF',[string]$session.token)
    $payload=[Net.Http.StringContent]::new((@{Username='teste';Password=$password}|ConvertTo-Json),[Text.Encoding]::UTF8,'application/json')
    $response=$client.PostAsync('/api/login',$payload).GetAwaiter().GetResult()
    if(-not $response.IsSuccessStatusCode){throw 'LocalService não conseguiu criar a sessão autenticada.'}
    $session=$client.GetStringAsync('/api/session').GetAwaiter().GetResult() | ConvertFrom-Json
    if(-not $session.authenticated){throw 'Cookie de sessão não validado.'}
    Write-Output 'OK: LocalService iniciou, abriu HTTPS e criou/validou sessão autenticada com DPAPI e ACL restrita. Nenhum SQL acessado.'
    if($CheckPasswordChange){
        function Post-Json($Http,[string]$Url,$Data){
            $body=[Net.Http.StringContent]::new(($Data|ConvertTo-Json),[Text.Encoding]::UTF8,'application/json')
            try{return $Http.PostAsync($Url,$body).GetAwaiter().GetResult()}finally{$body.Dispose()}
        }
        function Refresh-Token($Http){
            $s=$Http.GetStringAsync('/api/session').GetAwaiter().GetResult()|ConvertFrom-Json
            $Http.DefaultRequestHeaders.Remove('X-LMC-CSRF')|Out-Null;$Http.DefaultRequestHeaders.Add('X-LMC-CSRF',[string]$s.token)
            return $s
        }
        $other=[Net.Http.HttpClient]::new([LmcTestTls]::Handler($cert.Thumbprint));$other.BaseAddress=$client.BaseAddress
        try{
            $newPassword='Nova!56789';$change=@{CurrentPassword=$password;NewPassword=$newPassword;ConfirmPassword=$newPassword;Username='outro-operador'}
            $r=Post-Json $other '/api/account/password' $change
            if([int]$r.StatusCode -ne 401){throw 'Troca sem autenticação aceita.'};$r.Dispose()
            $null=Refresh-Token $other;$r=Post-Json $other '/api/login' @{Username='teste';Password=$password};$r.EnsureSuccessStatusCode()|Out-Null;$r.Dispose()
            $null=Refresh-Token $other
            $client.DefaultRequestHeaders.Remove('X-LMC-CSRF')|Out-Null
            $r=Post-Json $client '/api/account/password' $change
            if([int]$r.StatusCode -ne 403){throw 'Troca sem token aceita.'};$r.Dispose()
            $null=Refresh-Token $client
            foreach($invalid in @(
                @{CurrentPassword=$password;NewPassword='123456789';ConfirmPassword='123456789'},
                @{CurrentPassword=$password;NewPassword=$newPassword;ConfirmPassword='diferente'},
                @{CurrentPassword='errada';NewPassword=$newPassword;ConfirmPassword=$newPassword}
            )){
                $r=Post-Json $client '/api/account/password' $invalid
                if([int]$r.StatusCode -ne 400){throw 'Troca inválida foi aceita.'};$r.Dispose()
            }
            $before=[IO.File]::ReadAllBytes($configPath)
            $accessPath=Join-Path $root 'operator-access/accounts.bin'
            $locked=[IO.File]::Open($accessPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
            try{$r=Post-Json $client '/api/account/password' $change;if([int]$r.StatusCode -ne 503){throw 'Falha de gravação não foi tratada.'};$r.Dispose()}finally{$locked.Dispose()}
            if(-not (Refresh-Token $other).authenticated){throw 'Falha ao salvar invalidou senha anterior.'}
            $r=Post-Json $client '/api/account/password' $change;$r.EnsureSuccessStatusCode()|Out-Null;$r.Dispose()
            if(-not (Refresh-Token $client).authenticated){throw 'Sessão atual não foi renovada.'}
            if((Refresh-Token $other).authenticated){throw 'Outra sessão permaneceu válida após a troca.'}
            if([Convert]::ToBase64String($before) -ne [Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath))){throw 'Troca alterou configuração SQL/certificado.'}
            $plain=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($accessPath),[Text.Encoding]::UTF8.GetBytes('LMC-OPERATORS-V1'),[Security.Cryptography.DataProtectionScope]::LocalMachine)
            $saved=[Text.Encoding]::UTF8.GetString($plain)
            if($saved.Contains($password) -or $saved.Contains($newPassword) -or $saved -notmatch 'pbkdf2-sha256'){throw 'Armazenamento não usa apenas hashes.'}
            [Array]::Clear($plain,0,$plain.Length);$saved=$null
            & $exe @applicationArgs --ProtectedConfig $configPath --PrepareOperatorAccess true
            if($LASTEXITCODE -ne 0){throw 'Preparação de atualização falhou.'}
            Restart-Service -Name $serviceName
            (Get-Service -Name $serviceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(20))
            for($attempt=0;$attempt -lt 30;$attempt++){
                try{$null=Refresh-Token $other;break}catch{if($attempt -eq 29){throw};Start-Sleep -Milliseconds 200}
            }
            $r=Post-Json $other '/api/login' @{Username='teste';Password=$password}
            if([int]$r.StatusCode -ne 401){throw 'Senha antiga voltou a funcionar após reiniciar.'};$r.Dispose()
            $r=Post-Json $other '/api/login' @{Username='teste';Password=$newPassword};$r.EnsureSuccessStatusCode()|Out-Null;$r.Dispose()
            $null=Refresh-Token $other
            for($attempt=0;$attempt -lt 9;$attempt++){$r=Post-Json $other '/api/account/password' @{CurrentPassword='errada';NewPassword=$password;ConfirmPassword=$password};$code=[int]$r.StatusCode;$r.Dispose()}
            if($code -ne 429){throw 'Tentativas de senha atual não foram limitadas.'}
            Write-Output 'OK: troca exige login, token, confirmação, senha atual e mínimo de 10; falha ao gravar preserva a senha; outras sessões expiram; nova senha persiste no reinício e na preparação da atualização; tentativas limitadas.'
        }finally{$other.Dispose()}
    }
} finally {
    if($client){$client.Dispose()}
    if($serviceCreated){
        Stop-Service -Name $serviceName -ErrorAction SilentlyContinue
        $svc=Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if($svc){$svc.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20));$svc.Dispose()}
        & sc.exe delete $serviceName | Out-Null
    }
    if($cert){
        $store=[Security.Cryptography.X509Certificates.X509Store]::new('My','LocalMachine');$store.Open('ReadWrite')
        try {
            $matches=$store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,$cert.Thumbprint,$false)
            foreach($match in $matches){
                $key=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($match)
                $store.Remove($match)
                if($key -is [Security.Cryptography.RSACng] -and -not $key.Key.IsEphemeral){$key.Key.Delete()}
                if($key){$key.Dispose()};$match.Dispose()
            }
        }finally{$store.Dispose()}
        $cert.Dispose()
    }
    if($rsa){$rsa.Dispose()};if($machineKey){$machineKey.Dispose()}
    if($root -ne (Join-Path $parent $id) -or -not $root.StartsWith($parent+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Pasta temporária fora do destino esperado.'}
    if(Test-Path -LiteralPath $root){
        if(Get-ChildItem -LiteralPath $root -Force -Recurse | Where-Object {($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0}){throw 'Link inesperado na pasta temporária.'}
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
