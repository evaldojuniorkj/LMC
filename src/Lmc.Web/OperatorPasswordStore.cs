using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace Lmc.Web;

public sealed class OperatorPasswordStore(string path)
{
    private static readonly byte[] Entropy=Encoding.UTF8.GetBytes("LMC-OPERATORS-V1");
    public static string? PathFor(IConfiguration configuration)
    {
        var config=configuration["ProtectedConfig"];
        return !configuration.GetValue<bool>("Deployment:NetworkAccess")||string.IsNullOrWhiteSpace(config)?null
            :Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config))!,"operator-access","accounts.bin");
    }
    public static void Validate(OperatorAccount[] accounts)
    {
        if(accounts.Length==0||accounts.Any(a=>a is null||string.IsNullOrWhiteSpace(a.Username)||a.Username.Length>64||!DeploymentSecurity.ValidHash(a.PasswordHash))
            ||accounts.Select(a=>a.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=accounts.Length)
            throw new InvalidOperationException("Configure operadores distintos e senhas válidas para o acesso à rede.");
    }
    public OperatorAccount[] Read()
    {
        if(!OperatingSystem.IsWindows())throw new InvalidOperationException("A configuração de operadores requer Windows.");
        var bytes=ProtectedData.Unprotect(File.ReadAllBytes(path),Entropy,DataProtectionScope.LocalMachine);
        try
        {
            var accounts=JsonSerializer.Deserialize<OperatorAccount[]>(bytes)??[];
            Validate(accounts);return accounts;
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    public void Write(OperatorAccount[] accounts,bool create=false)
    {
        if(!OperatingSystem.IsWindows())throw new InvalidOperationException("A configuração de operadores requer Windows.");
        Validate(accounts);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(accounts);
        var temporary=Path.Combine(Path.GetDirectoryName(path)!,Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            var encrypted=ProtectedData.Protect(bytes,Entropy,DataProtectionScope.LocalMachine);
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {file.Write(encrypted);file.Flush(flushToDisk:true);}
            // Renomeação atômica no mesmo diretório: o arquivo contém somente
            // hashes criptografados, inclusive durante a gravação temporária.
            File.Move(temporary,path,overwrite:!create);
        }
        finally{CryptographicOperations.ZeroMemory(bytes);if(File.Exists(temporary))File.Delete(temporary);}
    }
    public static int Prepare(IConfiguration configuration)
    {
        Console.OutputEncoding=new UTF8Encoding(false);
        try
        {
            if(!OperatingSystem.IsWindows()||!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Abra o instalador como Administrador para preparar o acesso dos operadores.");
            var path=PathFor(configuration)??throw new InvalidOperationException("Configuração protegida de produção não localizada.");
            var folder=Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(folder);
            if(File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint))throw new InvalidOperationException("A pasta de operadores não pode ser um link.");
            SetPermissions(folder);
            // Imported CSVs and tank assignments survive application upgrades alongside protected configuration.
            var tablesFolder=TankTableStore.DirectoryFor(configuration,AppContext.BaseDirectory);
            Directory.CreateDirectory(tablesFolder);
            if(File.GetAttributes(tablesFolder).HasFlag(FileAttributes.ReparsePoint))throw new InvalidOperationException("A pasta de tabelas não pode ser um link.");
            SetPermissions(tablesFolder);
            var auditFolder=BookAuditLog.DirectoryFor(configuration,AppContext.BaseDirectory);
            Directory.CreateDirectory(auditFolder);
            if(File.GetAttributes(auditFolder).HasFlag(FileAttributes.ReparsePoint))throw new InvalidOperationException("A pasta de auditoria não pode ser um link.");
            SetPermissions(auditFolder);
            var store=new OperatorPasswordStore(path);
            if(File.Exists(path))store.Read();
            else store.Write(configuration.GetSection("Access:Users").Get<OperatorAccount[]>()??[],create:true);
            Console.WriteLine("Acesso dos operadores preparado. Senhas já alteradas são preservadas.");return 0;
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidOperationException)
        {Console.WriteLine("Não foi possível preparar o acesso dos operadores: "+ex.Message);return 1;}
    }
    [SupportedOSPlatform("windows")]
    private static void SetPermissions(string folder)
    {
        var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
        foreach(var pair in new[]{(WellKnownSidType.LocalSystemSid,FileSystemRights.FullControl),
            (WellKnownSidType.BuiltinAdministratorsSid,FileSystemRights.FullControl),(WellKnownSidType.LocalServiceSid,FileSystemRights.Modify)})
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(pair.Item1,null),pair.Item2,
                InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(acl);
    }
}
