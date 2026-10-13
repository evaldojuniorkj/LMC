using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Lmc.Web;

public static class ServiceCertificate
{
    private const string Prefix="Kestrel:Certificates:Default:";

    private static X509Certificate2 ReadPfx(IConfiguration configuration,X509KeyStorageFlags flags)
    {
        var path=configuration[Prefix+"Path"];
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("O certificado HTTPS precisa ter um caminho absoluto na configuração da instalação.");
        return X509CertificateLoader.LoadPkcs12FromFile(path,configuration[Prefix+"Password"],flags);
    }

    private static X509Certificate2? Find(X509Store store,string thumbprint)
    {
        X509Certificate2? selected=null;
        foreach(var cert in store.Certificates.Find(X509FindType.FindByThumbprint,thumbprint,validOnly:false))
        {
            if(selected is null&&cert.HasPrivateKey)selected=cert;
            else cert.Dispose();
        }
        return selected;
    }

    // Executado pelo instalador elevado. Apenas a chave deste certificado recebe
    // leitura para LocalService; nenhuma pasta de chaves recebe permissão ampla.
    public static int Prepare(IConfiguration configuration)
    {
        Console.OutputEncoding=new System.Text.UTF8Encoding(false);
        try
        {
            if(!OperatingSystem.IsWindows()||!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Abra o instalador como Administrador para preparar o certificado do serviço.");
            using var metadata=ReadPfx(configuration,X509KeyStorageFlags.EphemeralKeySet);
            using var store=new X509Store(StoreName.My,StoreLocation.LocalMachine);store.Open(OpenFlags.ReadWrite);
            using var cert=Find(store,metadata.Thumbprint)??ReadPfx(configuration,X509KeyStorageFlags.MachineKeySet|X509KeyStorageFlags.PersistKeySet);
            if(!cert.HasPrivateKey)throw new InvalidOperationException("O certificado HTTPS não contém a chave privada.");
            var path=PrivateKeyFile(cert);
            var file=new FileInfo(path);var acl=file.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid,null),FileSystemRights.Read,AccessControlType.Allow));
            file.SetAccessControl(acl);
            store.Add(cert);
            Console.WriteLine("Certificado HTTPS preparado: leitura da chave autorizada somente para a conta do serviço, além das permissões existentes.");
            return 0;
        }
        catch(Exception ex)when(ex is CryptographicException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {Console.WriteLine("Não foi possível preparar o certificado HTTPS do serviço: "+ex.Message);return 1;}
    }

    [SupportedOSPlatform("windows")]
    private static string PrivateKeyFile(X509Certificate2 cert)
    {
        using var rsa=cert.GetRSAPrivateKey();using var ec=rsa is null?cert.GetECDsaPrivateKey():null;
        string folder,name;
        var cng=(rsa as RSACng)?.Key??(ec as ECDsaCng)?.Key;
        if(cng is not null)
        {
            if(!cng.IsMachineKey||cng.IsEphemeral)throw new InvalidOperationException("A chave HTTPS precisa estar no armazenamento de certificados do computador.");
            folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Microsoft","Crypto","Keys");name=cng.UniqueName!;
        }
        else if(rsa is RSACryptoServiceProvider csp&&csp.CspKeyContainerInfo.MachineKeyStore)
        {folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Microsoft","Crypto","RSA","MachineKeys");name=csp.CspKeyContainerInfo.UniqueKeyContainerName;}
        else throw new InvalidOperationException("O provedor da chave privada precisa ser configurado pelo administrador do certificado.");
        if(string.IsNullOrWhiteSpace(name)||Path.GetFileName(name)!=name)throw new InvalidOperationException("Identificação de chave privada inválida.");
        return Path.Combine(folder,name);
    }

    public static X509Certificate2? Configure(WebApplicationBuilder builder)
    {
        if(!OperatingSystem.IsWindows()||!WindowsServiceHelpers.IsWindowsService())return null;
        // O PFX é lido em memória somente para identificar o certificado exato.
        // O serviço usa a chave já instalada: não importa nem grava chaves ao iniciar.
        using var metadata=ReadPfx(builder.Configuration,X509KeyStorageFlags.EphemeralKeySet);
        using var store=new X509Store(StoreName.My,StoreLocation.LocalMachine);store.Open(OpenFlags.ReadOnly);
        var cert=Find(store,metadata.Thumbprint)
            ??throw new InvalidOperationException("Certificado HTTPS do serviço não preparado. Execute a instalação ou atualização pelo instalador visual atual.");
        // Evita que o carregador padrão do Kestrel reimporte o PFX depois.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>
        { [Prefix+"Path"]=null,[Prefix+"Password"]=null,[Prefix+"Subject"]=null });
        var url=builder.Configuration["Urls"];
        // Pacotes anteriores usavam 0.0.0.0 (somente IPv4). Mantém a mesma
        // porta e os filtros de acesso, incluindo localhost resolvido como ::1.
        var allAddresses=Uri.TryCreate(url,UriKind.Absolute,out var address)&&address.Scheme=="https"&&address.Host=="0.0.0.0";
        builder.WebHost.ConfigureKestrel(options=>
        {
            options.ConfigureHttpsDefaults(https=>https.ServerCertificate=cert);
            if(allAddresses)options.ListenAnyIP(address!.Port,endpoint=>endpoint.UseHttps(cert));
        });
        return cert;
    }

    public static async Task<int> CheckHttps(IConfiguration configuration)
    {
        Console.OutputEncoding=new System.Text.UTF8Encoding(false);
        try
        {
            using var cert=ReadPfx(configuration,X509KeyStorageFlags.EphemeralKeySet);
            var url=new Uri(configuration["Urls"]??throw new InvalidOperationException("Endereço HTTPS não configurado."));
            if(url.Scheme!="https")throw new InvalidOperationException("O serviço precisa usar HTTPS.");
            // Teste local antes de importar a confiança no Windows: aceita apenas
            // o certificado exato desta instalação, sem desabilitar TLS no produto.
            using var handler=new HttpClientHandler{UseProxy=false,ServerCertificateCustomValidationCallback=(_,remote,_,_)=>remote?.Thumbprint==cert.Thumbprint};
            using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(10)};
            foreach(var host in new[]{"127.0.0.1","localhost"})
            {
                var response=await client.GetFromJsonAsync<JsonElement>($"https://{host}:{url.Port}/health/live");
                if(!response.TryGetProperty("status",out var status)||status.GetString()!="ok")throw new InvalidOperationException($"O endereço {host} respondeu, mas não confirmou o LMC.");
            }
            Console.WriteLine("HTTPS confirmado em localhost e IPv4, com o certificado desta instalação.");return 0;
        }
        catch(Exception ex)when(ex is HttpRequestException or TaskCanceledException or CryptographicException or IOException or InvalidOperationException or JsonException or UriFormatException)
        {Console.WriteLine("O serviço não confirmou o acesso HTTPS. Confira a permissão da chave do certificado e os eventos do Windows. Detalhe: "+ex.Message);return 1;}
    }
}
