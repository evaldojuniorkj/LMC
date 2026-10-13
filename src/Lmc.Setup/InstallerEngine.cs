using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace Lmc.Setup;

public sealed record CommandResult(int ExitCode,string Output);
public sealed record InstalledInfo(string Url,string Operator,string CertificatePath,string Thumbprint,bool SelfSigned);

public sealed class InstallerEngine : IDisposable
{
    private string? work;
    private bool prepared;
    private readonly string? payloadOverride;
    public InstallerEngine(string? payloadOverride=null){this.payloadOverride=payloadOverride;}
    public static string DataRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"LMC");
    public static bool IsAdministrator()=>new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static bool ServiceExists()=>ServiceController.GetServices().Any(s=>{using(s)return s.ServiceName=="LMC";});
    public async Task<string> Prepare(Action<string> report)
    {
        if(work is not null&&prepared)return work;
        if(work is not null)Dispose();
        report("Preparando os arquivos do instalador...");
        var target=Path.Combine(Path.GetTempPath(),"LMC-setup",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
        foreach(var sid in new[]{WindowsIdentity.GetCurrent().User!,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
            acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(target).SetAccessControl(acl);
        work=target;await File.WriteAllTextAsync(Path.Combine(target,".lmc-setup"),Path.GetFileName(target));
        await Task.Run(()=>
        {
            using var stream=payloadOverride is null?Assembly.GetExecutingAssembly().GetManifestResourceStream("Lmc.Setup.payload.zip")
                ??throw new InvalidOperationException("Pacote da aplicação ausente. Use o instalador completo publicado."):File.OpenRead(payloadOverride);
            using var zip=new ZipArchive(stream,ZipArchiveMode.Read);
            foreach(var entry in zip.Entries)
            {
                var path=SafeEntryPath(target,entry.FullName);
                if(entry.FullName.EndsWith('/')){Directory.CreateDirectory(path);continue;}
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);entry.ExtractToFile(path,false);
            }
            var manifest=Path.Combine(target,"SHA256SUMS.txt");
            if(!File.Exists(manifest))throw new InvalidDataException("Pacote sem manifesto de integridade.");
            var listed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var line in File.ReadAllLines(manifest).Where(s=>s.Length>0))
            {
                if(line.Length<67||line.Substring(64,2)!="  ")throw new InvalidDataException("Manifesto inválido.");
                var path=SafeEntryPath(target,line[66..]);using var file=File.OpenRead(path);
                if(!Convert.ToHexString(SHA256.HashData(file)).Equals(line[..64],StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("O pacote está incompleto ou foi alterado.");
                listed.Add(path);
            }
            foreach(var path in Directory.EnumerateFiles(target,"*",SearchOption.AllDirectories))
                if(Path.GetFileName(path) is not (".lmc-setup" or "SHA256SUMS.txt")&&!listed.Contains(path))throw new InvalidDataException("Arquivo não reconhecido no pacote.");
            if(!File.Exists(Path.Combine(target,"app","Lmc.Web.exe")))throw new InvalidDataException("Executável LMC ausente.");
        });
        prepared=true;report("Arquivos conferidos.");return target;
    }
    public static string SafeEntryPath(string root,string entry)
    {
        var full=Path.GetFullPath(Path.Combine(root,entry.Replace('/',Path.DirectorySeparatorChar)));
        if(Path.IsPathRooted(entry)||entry.Contains(':')||!full.StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Caminho inválido no pacote.");
        return full;
    }
    public async Task<CommandResult> CheckSql(SetupOptions options,Action<string> report)
    {
        var root=await Prepare(report);var path=Path.Combine(root,"sql-check.bin");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{Deployment=new{NetworkAccess=false},ConnectionStrings=new{AtxDados=options.ConnectionString()}});
        try
        {
            await File.WriteAllBytesAsync(path,ProtectedData.Protect(bytes,Encoding.UTF8.GetBytes("LMC-DEPLOY-V1"),DataProtectionScope.LocalMachine));
            report($"Testando o SQL Server e as colunas de {options.Database}, sem alterar dados...");
            return await Run(Path.Combine(root,"app","Lmc.Web.exe"),["--ProtectedConfig",path,"--CheckInstallation","true"],root,null,s=>report(options.Redact(s)),TimeSpan.FromMinutes(2));
        }
        finally{CryptographicOperations.ZeroMemory(bytes);if(File.Exists(path))File.Delete(path);}
    }
    public async Task<CommandResult> Install(SetupOptions options,Action<string> report)
    {
        if(!IsAdministrator())throw new InvalidOperationException("Abra o instalador como Administrador.");
        if(ServiceExists())throw new InvalidOperationException("Já existe um LMC instalado. Use a tela de manutenção para preservar a configuração.");
        if(options.ValidateSql() is { } sql)throw new InvalidOperationException(sql);
        if(options.ValidateAccess() is { } access)throw new InvalidOperationException(access);
        if(!await SetupOptions.IsLocalAddress(options.ServerAddress))throw new InvalidOperationException("O endereço de acesso não pertence a este computador. Escolha o IP da máquina onde está instalando.");
        if(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p=>p.Port==options.Port))throw new InvalidOperationException("A porta informada já está em uso. Escolha outra porta.");
        var root=await Prepare(report);
        return await Run(PowerShell,["-NoProfile","-NonInteractive","-ExecutionPolicy","RemoteSigned","-OutputFormat","Text","-File",Path.Combine(root,"Instalar-LMC.ps1"),"-InputFromStdin"],root,
            JsonSerializer.Serialize(options),s=>report(options.Redact(s)),null);
    }
    public async Task<CommandResult> Update(Action<string> report)
    {
        if(!IsAdministrator())throw new InvalidOperationException("Abra o instalador como Administrador.");
        var root=await Prepare(report);
        return await Run(PowerShell,["-NoProfile","-NonInteractive","-ExecutionPolicy","RemoteSigned","-OutputFormat","Text","-File",Path.Combine(root,"Atualizar-LMC.ps1")],root,null,report,null);
    }
    public static string PowerShell=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe");
    public static async Task<CommandResult> Run(string exe,string[] args,string directory,string? input,Action<string> report,TimeSpan? timeout)
    {
        var start=new ProcessStartInfo(exe){WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
            RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=input is not null,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        // Um instalador aberto pelo PowerShell 7 pode herdar módulos incompatíveis
        // com o Windows PowerShell 5.1. Este processo usa somente módulos do Windows.
        if(exe.Equals(PowerShell,StringComparison.OrdinalIgnoreCase))
            start.Environment["PSModulePath"]=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","Modules");
        if(input is not null)start.StandardInputEncoding=new UTF8Encoding(false);
        using var process=Process.Start(start)??throw new InvalidOperationException("Não foi possível iniciar a verificação.");
        var output=new StringBuilder();var outputLock=new object();
        async Task Drain(StreamReader reader){while(await reader.ReadLineAsync() is {} line){lock(outputLock)output.AppendLine(line);report(line);}}
        var stdout=Drain(process.StandardOutput);var stderr=Drain(process.StandardError);
        if(input is not null){await process.StandardInput.WriteAsync(input);process.StandardInput.Close();}
        using var cts=timeout is {} limit?new CancellationTokenSource(limit):new CancellationTokenSource();
        try{await process.WaitForExitAsync(cts.Token);}
        catch(OperationCanceledException){process.Kill(true);await process.WaitForExitAsync();await Task.WhenAll(stdout,stderr);throw new InvalidOperationException("A verificação excedeu o tempo esperado. Confira o endereço e a conexão.");}
        await Task.WhenAll(stdout,stderr);return new(process.ExitCode,output.ToString());
    }
    public static InstalledInfo ReadInstalled()
    {
        var path=Path.Combine(DataRoot,"configuration.bin");
        var bytes=ProtectedData.Unprotect(File.ReadAllBytes(path),Encoding.UTF8.GetBytes("LMC-DEPLOY-V1"),DataProtectionScope.LocalMachine);
        try
        {
            using var json=JsonDocument.Parse(bytes);var root=json.RootElement;
            var host=root.GetProperty("AllowedHosts").GetString()!.Split(';')[0];
            var port=new Uri(root.GetProperty("Urls").GetString()!).Port;
            var value=root.GetProperty("Kestrel").GetProperty("Certificates").GetProperty("Default");
            using var cert=X509CertificateLoader.LoadPkcs12FromFile(value.GetProperty("Path").GetString()!,value.GetProperty("Password").GetString(),X509KeyStorageFlags.EphemeralKeySet);
            var publicPath=Path.Combine(DataRoot,"LMC-certificado-publico.cer");
            // Permite exportar somente o certificado público também de instalações antigas.
            var content=cert.Export(X509ContentType.Cert);
            if(!File.Exists(publicPath)||!File.ReadAllBytes(publicPath).SequenceEqual(content))File.WriteAllBytes(publicPath,content);
            return new($"https://{host}:{port}",root.GetProperty("Access").GetProperty("Users")[0].GetProperty("Username").GetString()!,publicPath,cert.Thumbprint,cert.SubjectName.RawData.SequenceEqual(cert.IssuerName.RawData));
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    public static void TrustCertificate(InstalledInfo info)
    {
        if(!info.SelfSigned)throw new InvalidOperationException("Este LMC usa um certificado externo. Configure a cadeia de confiança com o emissor.");
        using var cert=X509CertificateLoader.LoadCertificateFromFile(info.CertificatePath);
        if(cert.Thumbprint!=info.Thumbprint)throw new InvalidOperationException("Certificado diferente do configurado no serviço.");
        using var store=new X509Store(StoreName.Root,StoreLocation.LocalMachine);store.Open(OpenFlags.ReadWrite);store.Add(cert);
    }
    public static async Task<string> NetworkProfiles()
    {
        var r=await Run(PowerShell,["-NoProfile","-NonInteractive","-ExecutionPolicy","RemoteSigned","-OutputFormat","Text","-Command","[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $ErrorActionPreference='Stop'; Get-NetConnectionProfile | Select-Object InterfaceAlias,NetworkCategory | ConvertTo-Json -Compress"],Environment.GetFolderPath(Environment.SpecialFolder.Windows),null,_=>{},TimeSpan.FromSeconds(15));
        if(r.ExitCode!=0)return "Não foi possível consultar o perfil da rede.";
        try
        {
            if(string.IsNullOrWhiteSpace(r.Output))return "Nenhuma rede ativa identificada.";
            using var json=JsonDocument.Parse(r.Output);var rows=json.RootElement.ValueKind==JsonValueKind.Array?json.RootElement.EnumerateArray().ToArray():[json.RootElement];
            return string.Join(Environment.NewLine,rows.Select(row=>$"{row.GetProperty("InterfaceAlias").GetString()}: "+(row.GetProperty("NetworkCategory").GetInt32() switch{
                0=>"Pública — a regra do LMC não permite acesso de outros computadores nesse perfil. Altere para Privada somente se esta for a rede confiável do posto.",
                1=>"Privada — perfil compatível com a regra do LMC.",_=>"Domínio — perfil compatível com a regra do LMC."})));
        }
        catch(JsonException){return "Não foi possível interpretar o perfil da rede.";}
    }
    public static async Task<string> Diagnose(InstalledInfo info)
    {
        var lines=new List<string>();
        using var service=new ServiceController("LMC");service.Refresh();lines.Add($"Serviço LMC: {service.Status}.");
        var uri=new Uri(info.Url);using var tcp=new TcpClient();
        try{await tcp.ConnectAsync(IPAddress.Loopback,uri.Port).WaitAsync(TimeSpan.FromSeconds(4));lines.Add($"Porta {uri.Port}: responde neste computador.");}
        catch(Exception ex)when(ex is SocketException or System.TimeoutException){lines.Add($"Porta {uri.Port}: sem resposta local. Confira o serviço e o log Aplicativo do Windows.");}
        using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(5)};
        foreach(var url in new[]{$"https://localhost:{uri.Port}",info.Url}.Distinct())
        {
            try
            {
                using var response=await http.GetAsync(url+"/health/live");
                var body=await response.Content.ReadAsStringAsync();
                var valid=false;
                if(response.IsSuccessStatusCode){try{using var j=JsonDocument.Parse(body);valid=j.RootElement.GetProperty("status").GetString()=="ok";}catch(Exception ex)when(ex is JsonException or KeyNotFoundException){}}
                lines.Add(valid?$"{url}: LMC respondeu com certificado válido.":$"{url}: resposta inesperada (HTTP {(int)response.StatusCode}).");
            }
            catch(HttpRequestException ex){lines.Add($"{url}: "+(ex.InnerException is System.Security.Authentication.AuthenticationException?"certificado HTTPS não validado. Use o botão de certificado neste computador.":"não foi possível conectar."));}
            catch(TaskCanceledException){lines.Add($"{url}: não respondeu em 5 segundos.");}
        }
        if(!await SetupOptions.IsLocalAddress(uri.Host))lines.Add("O endereço de acesso não corresponde aos IPs atuais deste computador. Confira se o IP fixo mudou.");
        lines.Add(await NetworkProfiles());lines.Add("Estes testes são locais. Confirme o acesso pelo endereço da rede também em outro computador.");
        return string.Join(Environment.NewLine,lines);
    }
    public static async Task StartService(){await Task.Run(()=>{using var service=new ServiceController("LMC");service.Refresh();if(service.Status!=ServiceControllerStatus.Running){service.Start();service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(30));}});}
    public void Dispose()
    {
        if(work is null)return;
        var root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"LMC-setup"))+Path.DirectorySeparatorChar;
        var target=Path.GetFullPath(work);var marker=Path.Combine(target,".lmc-setup");
        // Só remove a pasta privada criada por esta instância; nunca o programa instalado.
        if(!target.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!File.Exists(marker)||File.ReadAllText(marker)!=Path.GetFileName(target))return;
        try{if(!Directory.EnumerateFileSystemEntries(target,"*",SearchOption.AllDirectories).Any(p=>File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)))Directory.Delete(target,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        finally{work=null;prepared=false;}
    }
}
