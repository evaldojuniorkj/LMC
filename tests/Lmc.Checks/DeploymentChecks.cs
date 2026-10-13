using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Lmc.Web;
using Microsoft.Extensions.Configuration;

static class DeploymentChecks
{
    public static void Rules(Action<bool,string> check)
    {
        var certificateFailure=DeploymentSecurity.InstallationSqlFailure(-2146893019);
        check(certificateFailure.Contains("certificado SQL confiável")&&certificateFailure.Contains("-TrustSqlCertificate")
            &&certificateFailure.Contains("mantém a criptografia")&&certificateFailure.Contains("dispensa a validação")
            &&certificateFailure.Contains("ainda não foi validado"),"certificado SQL não confiável apresenta causa e alternativas sem afirmar acesso ao banco");
        check(DeploymentSecurity.InstallationSqlFailure(18456).Contains("18456")
            &&!DeploymentSecurity.InstallationSqlFailure(18456).Contains("-TrustSqlCertificate"),"falha de autenticação não é confundida com certificado");
        var hash=DeploymentSecurity.HashPassword("Senha somente para testes 123!");
        check(DeploymentSecurity.VerifyPassword(hash,"Senha somente para testes 123!")&&!DeploymentSecurity.VerifyPassword(hash,"incorreta"),"senha da interface validada por hash com sal");
        check(!DeploymentSecurity.ValidHash("texto")&&!DeploymentSecurity.VerifyPassword(hash,new string('x',257)),"hash ou tamanho inválido não é aceito");
        var local=new DeploymentSecurity(new ConfigurationBuilder().Build());
        check(local.Allows(IPAddress.Loopback)&&!local.Allows(IPAddress.Parse("192.168.1.2")),"modo local permanece restrito ao computador");
        var values=new Dictionary<string,string?>{{"Deployment:NetworkAccess","true"},{"Deployment:KeyDirectory","keys"},{"AllowedHosts","localhost"},
            {"Access:Networks:0","192.168.0.0/16"},{"Access:Users:0:Username","operador"},{"Access:Users:0:PasswordHash",hash}};
        var access=new DeploymentSecurity(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        check(access.Allows(IPAddress.Parse("192.168.1.2"))&&access.Allows(IPAddress.Parse("::ffff:192.168.1.2"))&&!access.Allows(IPAddress.Parse("8.8.8.8")),"acesso de rede respeita faixas e IPv4 mapeado");
        values["AllowedHosts"]="*";
        try {new DeploymentSecurity(new ConfigurationBuilder().AddInMemoryCollection(values).Build());throw new Exception("Host aberto aceito");}
        catch(InvalidOperationException){check(true,"modo de rede exige hosts explícitos");}
    }
    public static async Task Http(string appPath,Action<bool,string> check)
    {
        if(!OperatingSystem.IsWindows())throw new InvalidOperationException("Ensaio do pacote Windows requer Windows.");
        var folder=Path.Combine(Path.GetFullPath("local"),"deployment-qa-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(IPAddress.Loopback);request.CertificateExtensions.Add(san.Build());
        using var cert=request.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-1),DateTimeOffset.Now.AddDays(1));
        var certPassword=Guid.NewGuid().ToString("N");var certPath=Path.Combine(folder,"test.pfx");await File.WriteAllBytesAsync(certPath,cert.Export(X509ContentType.Pfx,certPassword));
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var password=Guid.NewGuid().ToString("N");
        var config=new Dictionary<string,object>{["Urls"]=$"https://127.0.0.1:{port}",["AllowedHosts"]="localhost;127.0.0.1",
            ["Deployment"]=new {NetworkAccess=true,Label="Teste isolado",KeyDirectory=Path.Combine(folder,"keys")},
            ["Access"]=new {Networks=new[]{"192.168.0.0/16"},Users=new[]{new {Username="teste",PasswordHash=DeploymentSecurity.HashPassword(password)}}},
            ["Kestrel"]=new {Certificates=new {Default=new {Path=certPath,Password=certPassword}}},
            ["ConnectionStrings"]=new {AtxDados="Server=localhost;Database=ATXDADOS;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=5"}};
        var configPath=Path.Combine(folder,"configuration.bin");await File.WriteAllBytesAsync(configPath,ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(config),Encoding.UTF8.GetBytes("LMC-DEPLOY-V1"),DataProtectionScope.LocalMachine));
        var configuration=new ConfigurationManager();configuration["ProtectedConfig"]=configPath;DeploymentSecurity.LoadProtectedConfiguration(configuration);
        if(OperatorPasswordStore.Prepare(configuration)!=0)throw new Exception("Falha ao preparar operadores do teste.");
        var start=new ProcessStartInfo(appPath.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)?"dotnet":Path.GetFullPath(appPath)){WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(appPath)),UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(appPath.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(Path.GetFullPath(appPath));
        start.ArgumentList.Add("--ProtectedConfig");start.ArgumentList.Add(configPath);
        using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            using var handler=new HttpClientHandler {CookieContainer=new CookieContainer(),ServerCertificateCustomValidationCallback=(_,server,_,_)=>server?.Thumbprint==cert.Thumbprint};
            using var client=new HttpClient(handler){BaseAddress=new Uri($"https://127.0.0.1:{port}"),Timeout=TimeSpan.FromSeconds(10)};
            for(var i=0;i<40;i++){try {if((await client.GetAsync("/health/live")).IsSuccessStatusCode)break;}catch(HttpRequestException){} if(process.HasExited)throw new Exception(await stderr);await Task.Delay(150);}
            check((await client.GetAsync("/health/live")).IsSuccessStatusCode,"pacote publicado inicia em HTTPS com configuração protegida");
            check((await client.GetAsync("/api/context")).StatusCode==HttpStatusCode.Unauthorized,"consultas exigem login na rede");
            check((await client.PostAsJsonAsync("/api/lmc/save",new{})).StatusCode==HttpStatusCode.Unauthorized,"gravações anônimas são rejeitadas antes de consultar o banco");
            var session=(await client.GetFromJsonAsync<JsonElement>("/api/session"));
            check(session.GetProperty("requiresLogin").GetBoolean()&&!session.GetProperty("authenticated").GetBoolean(),"sessão identifica login obrigatório");
            check((await client.PostAsJsonAsync("/api/login",new {Username="teste",Password=password})).StatusCode==HttpStatusCode.Forbidden,"login também exige token antiforgery");
            client.DefaultRequestHeaders.Add("X-LMC-CSRF",session.GetProperty("token").GetString());
            check((await client.PostAsJsonAsync("/api/login",new {Username="teste",Password="errada"})).StatusCode==HttpStatusCode.Unauthorized,"senha incorreta não inicia sessão");
            var signed=await client.PostAsJsonAsync("/api/login",new {Username="teste",Password=password});
            check(signed.IsSuccessStatusCode&&signed.Headers.GetValues("Set-Cookie").Any(c=>c.Contains("__Host-LMC-Session")&&c.Contains("secure",StringComparison.OrdinalIgnoreCase)&&c.Contains("httponly",StringComparison.OrdinalIgnoreCase)),"login emite cookie protegido");
            var context=await client.GetFromJsonAsync<JsonElement>("/api/context");
            check(context.GetProperty("actor").GetString()=="teste"&&context.GetProperty("deploymentLabel").GetString()=="Teste isolado","operador autenticado identifica o histórico e o ambiente");
            var csrf=await client.GetFromJsonAsync<JsonElement>("/api/security");client.DefaultRequestHeaders.Remove("X-LMC-CSRF");client.DefaultRequestHeaders.Add("X-LMC-CSRF",csrf.GetProperty("token").GetString());
            check((await client.PostAsync("/api/logout",null)).IsSuccessStatusCode&&(await client.GetAsync("/api/context")).StatusCode==HttpStatusCode.Unauthorized,"sair encerra acesso aos dados");
            session=await client.GetFromJsonAsync<JsonElement>("/api/session");client.DefaultRequestHeaders.Remove("X-LMC-CSRF");client.DefaultRequestHeaders.Add("X-LMC-CSRF",session.GetProperty("token").GetString());
            HttpResponseMessage? limited=null;for(var i=0;i<9;i++)limited=await client.PostAsJsonAsync("/api/login",new {Username="teste",Password="errada"});
            check(limited?.StatusCode==HttpStatusCode.TooManyRequests,"tentativas de senha são limitadas por endereço");
        }
        finally {if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync();await stdout;await stderr;}
    }
}
