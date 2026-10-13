using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;

namespace Lmc.Web;

public sealed record LoginInput(string? Username,string? Password);
public sealed record OperatorAccount(string Username,string PasswordHash);
public sealed record PasswordChangeInput(string? CurrentPassword,string? NewPassword,string? ConfirmPassword);

public sealed class DeploymentSecurity
{
    public bool NetworkAccess {get;}
    public string Label {get;}
    public const string Version="1.0.0";
    private OperatorAccount[] accounts;
    private readonly OperatorPasswordStore? passwordStore;
    private readonly SemaphoreSlim passwordGate=new(1,1);
    private OperatorAccount[] Accounts=>Volatile.Read(ref accounts);
    private readonly IPNetwork[] networks;
    private const string CookieScheme="LmcOperator";

    public DeploymentSecurity(IConfiguration configuration)
    {
        NetworkAccess=configuration.GetValue<bool>("Deployment:NetworkAccess");
        Label=configuration["Deployment:Label"]??"Desenvolvimento local";
        accounts=configuration.GetSection("Access:Users").Get<OperatorAccount[]>()??[];
        networks=(configuration.GetSection("Access:Networks").Get<string[]>()??[]).Select(IPNetwork.Parse).ToArray();
        if(!NetworkAccess)return;
        if(OperatorPasswordStore.PathFor(configuration) is {} path)
        {passwordStore=new OperatorPasswordStore(path);accounts=passwordStore.Read();}
        OperatorPasswordStore.Validate(accounts);
        if(networks.Length==0)throw new InvalidOperationException("Configure as redes autorizadas.");
        var hosts=configuration["AllowedHosts"];
        if(string.IsNullOrWhiteSpace(hosts)||hosts.Contains('*'))throw new InvalidOperationException("Informe os nomes e IPs autorizados em AllowedHosts.");
        if(string.IsNullOrWhiteSpace(configuration["Deployment:KeyDirectory"]))throw new InvalidOperationException("Configure a pasta protegida das chaves de sessão.");
    }
    public bool Allows(IPAddress? address)
    {
        if(address is null)return false;
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();
        return IPAddress.IsLoopback(address)||(NetworkAccess&&networks.Any(n=>n.Contains(address)));
    }
    public static string HashPassword(string password)
    {
        var salt=RandomNumberGenerator.GetBytes(16);
        var hash=Rfc2898DeriveBytes.Pbkdf2(password,salt,600000,HashAlgorithmName.SHA256,32);
        return $"pbkdf2-sha256:600000:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }
    public static bool ValidHash(string? hash)
    {
        try {var p=(hash??"").Split(':');return p.Length==4&&p[0]=="pbkdf2-sha256"&&int.TryParse(p[1],out var iterations)&&iterations is >=600000 and <=2000000&&Convert.FromBase64String(p[2]).Length==16&&Convert.FromBase64String(p[3]).Length==32;}
        catch(FormatException){return false;}
    }
    public static bool VerifyPassword(string hash,string password)
    {
        if(!ValidHash(hash)||password.Length>256)return false;
        var p=hash.Split(':');var actual=Rfc2898DeriveBytes.Pbkdf2(password,Convert.FromBase64String(p[2]),int.Parse(p[1]),HashAlgorithmName.SHA256,32);
        return CryptographicOperations.FixedTimeEquals(actual,Convert.FromBase64String(p[3]));
    }
    public static void LoadProtectedConfiguration(ConfigurationManager configuration)
    {
        var path=configuration["ProtectedConfig"];
        if(string.IsNullOrWhiteSpace(path))return;
        if(!OperatingSystem.IsWindows())throw new InvalidOperationException("A configuração protegida desta instalação requer Windows.");
        var bytes=ProtectedData.Unprotect(File.ReadAllBytes(path),Encoding.UTF8.GetBytes("LMC-DEPLOY-V1"),DataProtectionScope.LocalMachine);
        try {using var stream=new MemoryStream(bytes);configuration.AddJsonStream(stream);}
        finally {CryptographicOperations.ZeroMemory(bytes);}
    }
    private static string Stamp(OperatorAccount account)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.PasswordHash)));
    public void ConfigureServices(IServiceCollection services,IConfiguration configuration)
    {
        services.AddSingleton(this);services.AddHttpContextAccessor();
        var keys=services.AddDataProtection().SetApplicationName("LMC-1");
        if(NetworkAccess)
        {
            keys.PersistKeysToFileSystem(new DirectoryInfo(configuration["Deployment:KeyDirectory"]!));
            if(OperatingSystem.IsWindows())keys.ProtectKeysWithDpapi(protectToLocalMachine:true);
        }
        services.AddAuthentication(CookieScheme).AddCookie(CookieScheme,options=>
        {
            options.Cookie.Name="__Host-LMC-Session";options.Cookie.HttpOnly=true;options.Cookie.SecurePolicy=CookieSecurePolicy.Always;options.Cookie.SameSite=SameSiteMode.Strict;
            options.ExpireTimeSpan=TimeSpan.FromHours(8);options.SlidingExpiration=true;
            options.Events.OnValidatePrincipal=async context=>
            {
                var account=Accounts.SingleOrDefault(a=>a.Username==context.Principal?.Identity?.Name);
                if(account is null||context.Principal?.FindFirstValue("stamp")!=Stamp(account)){context.RejectPrincipal();await context.HttpContext.SignOutAsync(CookieScheme);}
            };
        });
        services.AddRateLimiter(options=>
        {
            options.RejectionStatusCode=429;
            options.AddPolicy("login",context=>RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions {PermitLimit=8,Window=TimeSpan.FromMinutes(1),QueueLimit=0,AutoReplenishment=true}));
            options.AddPolicy("password-change",context=>RateLimitPartition.GetFixedWindowLimiter(context.User.Identity?.Name??context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions {PermitLimit=8,Window=TimeSpan.FromMinutes(1),QueueLimit=0,AutoReplenishment=true}));
        });
    }
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/session",(HttpContext context,IAntiforgery csrf)=>Results.Ok(new {
            requiresLogin=NetworkAccess,authenticated=!NetworkAccess||context.User.Identity?.IsAuthenticated==true,
            username=context.User.Identity?.Name,canChangePassword=passwordStore is not null&&context.User.Identity?.IsAuthenticated==true,
            token=csrf.GetAndStoreTokens(context).RequestToken,label=Label,version=Version}));
        app.MapPost("/api/login",async(LoginInput input,HttpContext context,IAntiforgery csrf)=>
        {
            if(!NetworkAccess)return Results.BadRequest(new {message="O acesso local não exige login."});
            try {await csrf.ValidateRequestAsync(context);}catch(AntiforgeryValidationException){return Results.Json(new {message="Atualize a tela de acesso e tente novamente."},statusCode:403);}
            var username=input.Username?.Trim()??"";var password=input.Password??"";
            var snapshot=Accounts;
            var account=snapshot.SingleOrDefault(a=>string.Equals(a.Username,username,StringComparison.OrdinalIgnoreCase));
            var valid=VerifyPassword(account?.PasswordHash??snapshot[0].PasswordHash,password);
            if(account is null||!valid)return Results.Json(new {message="Usuário ou senha inválidos."},statusCode:401);
            await SignIn(context,account);
            return Results.Ok(new {username=account.Username});
        }).RequireRateLimiting("login");
        app.MapPost("/api/account/password",ChangePassword).RequireRateLimiting("password-change")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(16384));
        app.MapPost("/api/logout",async(HttpContext context,IAntiforgery csrf)=>
        {
            try {await csrf.ValidateRequestAsync(context);}catch(AntiforgeryValidationException){return Results.StatusCode(403);}
            await context.SignOutAsync(CookieScheme);return Results.Ok();
        });
        app.MapGet("/health/live",()=>Results.Ok(new {status="ok",version=Version}));
    }
    private static Task SignIn(HttpContext context,OperatorAccount account)=>context.SignInAsync(CookieScheme,
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name,account.Username),new Claim("stamp",Stamp(account))],CookieScheme)),
        new AuthenticationProperties {IsPersistent=false});

    private async Task<IResult> ChangePassword(PasswordChangeInput input,HttpContext context,IAntiforgery csrf,ILogger<DeploymentSecurity> logger)
    {
        if(context.User.Identity?.IsAuthenticated!=true)return Results.Json(new{message="Entre novamente para trocar a senha."},statusCode:401);
        if(passwordStore is null)return Results.Json(new{message="A troca de senha está disponível na instalação de produção."},statusCode:409);
        try{await csrf.ValidateRequestAsync(context);}catch(AntiforgeryValidationException){return Results.Json(new{message="A sessão mudou. Feche esta janela e tente novamente."},statusCode:403);}
        if(string.IsNullOrEmpty(input.CurrentPassword)||input.CurrentPassword.Length>256)return Results.BadRequest(new{message="Informe a senha atual."});
        if(input.NewPassword is null||input.NewPassword.Length is <10 or >256)return Results.BadRequest(new{message="A nova senha deve ter de 10 a 256 caracteres."});
        if(input.NewPassword!=input.ConfirmPassword)return Results.BadRequest(new{message="A confirmação da nova senha não confere."});
        await passwordGate.WaitAsync(context.RequestAborted);
        try
        {
            var snapshot=Accounts;var account=snapshot.SingleOrDefault(a=>a.Username==context.User.Identity.Name);
            if(account is null||context.User.FindFirstValue("stamp")!=Stamp(account))return Results.Json(new{message="Sua sessão expirou. Entre novamente."},statusCode:401);
            if(!VerifyPassword(account.PasswordHash,input.CurrentPassword))return Results.BadRequest(new{message="A senha atual não confere."});
            var updated=account with{PasswordHash=HashPassword(input.NewPassword)};
            var next=snapshot.Select(a=>a.Username==account.Username?updated:a).ToArray();
            try{passwordStore.Write(next);}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or CryptographicException)
            {logger.LogError(ex,"Falha ao gravar a configuração protegida dos operadores");return Results.Json(new{message="Não foi possível salvar a senha. Confira a instalação com o responsável pelo sistema."},statusCode:503);}
            Volatile.Write(ref accounts,next);
            await SignIn(context,updated);
            return Results.Ok(new{message="Senha alterada. Use a nova senha nos próximos acessos.",username=updated.Username});
        }
        finally{passwordGate.Release();}
    }
    public static string InstallationSqlFailure(int number) => number == -2146893019
        ? "O Windows não confia na autoridade que emitiu o certificado do SQL Server (código -2146893019). "
          + "Configure um certificado SQL confiável. Para uma instalação na rede interna que usa certificado próprio, "
          + "a opção explícita .\\Instalar-LMC.ps1 -TrustSqlCertificate mantém a criptografia, mas dispensa a validação da identidade do certificado SQL. "
          + "O acesso ao banco ainda não foi validado."
        : $"A verificação do SQL Server falhou (código {number}). Confira servidor, autenticação e estrutura da base.";

    public static async Task<int> CheckInstallation(IConfiguration configuration)
    {
        Console.OutputEncoding=new UTF8Encoding(false);
        try
        {
            var settings=new SqlConnectionStringBuilder(configuration.GetConnectionString("AtxDados"));
            if(string.IsNullOrWhiteSpace(settings.InitialCatalog))throw new InvalidOperationException("Informe o nome do banco existente no instalador.");
            await using var c=new SqlConnection(settings.ConnectionString);await c.OpenAsync();
            using var cmd=new SqlCommand("""
                SELECT TOP(0) l.ID_FILIAL,l.ID_LMC,l.ID_PRODUTOS,l.DTACONTA,l.OBS,l.NROCONTROLE,l.VLRVENDASDIA FROM dbo.LMC l;
                SELECT TOP(0) ID_LMCBICOS,ID_LMC,ID_BICOS,ID_TANQUES,ENCERRANTEABERTURA,ENCERRANTEFECHAMENTO,AFERICAO,PPL,VENDAS FROM dbo.LMCBICOS;
                SELECT TOP(0) ID_LMCTANQUES,ID_LMC,ID_TANQUES,QTDETANQUE,QTDECONCILIACAO FROM dbo.LMCTANQUES;
                SELECT TOP(0) ID_LMCCOMPROVANTES,ID_LMC,VOLUMERECEBIDO,NROCOMPROVANTE,DTAENTRADA,CNPJ_FORNECEDOR FROM dbo.LMCCOMPROVANTES;
                SELECT TOP(0) ID_LMCENTRADATANQUES,ID_COMPROVANTE,ID_TANQUES,QTDE,DTACONTA FROM dbo.LMCENTRADATANQUES;
                SELECT TOP(0) ID_FILIAL,ID_BICOS,DATA FROM dbo.CONSOLEARQUIVO;
                SELECT TOP(0) ID_FILIAL,ID_ENCERRANTESTURNOS,ID_BICOS,ID_TURNOS FROM dbo.ENCERRANTESTURNOS;
                """,c){CommandTimeout=30};await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"Conexão com {c.Database} e colunas principais verificadas. Nenhum dado alterado.");return 0;
        }
        // Saída padrão permite ao instalador incluir a causa na mensagem final
        // também no Windows PowerShell 5.1. O código de saída continua indicando falha.
        catch(SqlException ex){Console.WriteLine(InstallationSqlFailure(ex.Number));return 1;}
        catch(InvalidOperationException ex){Console.WriteLine(ex.Message);return 1;}
    }
}
