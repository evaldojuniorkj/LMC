using System.Data.Common;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Lmc.Setup;

public sealed record SetupOptions(string SqlServer,string SqlUser,string SqlPassword,bool TrustSqlCertificate,
    string ServerAddress,string Operator,string OperatorPassword,string OperatorPasswordConfirm,int Port,
    bool TrustLocalHttpsCertificate,bool CreateDesktopShortcut,string Database="ATXDADOS")
{
    public string Url=>$"https://{ServerAddress}:{Port}";
    public string? ValidateSql()=>string.IsNullOrWhiteSpace(SqlServer)?"Informe o servidor ou a instância do SQL Server."
        :string.IsNullOrWhiteSpace(Database)||Database.Length>128?"Informe o nome do banco existente, com até 128 caracteres."
        :string.IsNullOrWhiteSpace(SqlUser)?"Informe o usuário do banco.":string.IsNullOrEmpty(SqlPassword)?"Informe a senha do banco.":null;
    public string? ValidateAccess()=>!Regex.IsMatch(ServerAddress,@"^[a-zA-Z0-9][a-zA-Z0-9.-]*$")?"Informe um nome ou IPv4 deste computador, sem https:// ou porta."
        :IPAddress.TryParse(ServerAddress,out var ip)&&(ip.AddressFamily!=AddressFamily.InterNetwork||ip.Equals(IPAddress.Any)||IPAddress.IsLoopback(ip))?"Escolha o IPv4 da rede deste computador."
        :!Regex.IsMatch(Operator,@"^[a-zA-Z0-9._-]{3,64}$")?"O usuário da interface deve ter de 3 a 64 letras, números, pontos, sublinhados ou hífens."
        :OperatorPassword.Length is <10 or >256?"Crie uma senha da interface com 10 a 256 caracteres."
        :OperatorPassword!=OperatorPasswordConfirm?"A confirmação da senha da interface não confere."
        :Port is <1024 or >65535?"Informe uma porta entre 1024 e 65535.":null;
    public string ConnectionString()
    {
        var b=new DbConnectionStringBuilder { ["Data Source"]=SqlServer,["Initial Catalog"]=Database,
            ["User ID"]=SqlUser,["Password"]=SqlPassword,["Encrypt"]=true,["TrustServerCertificate"]=TrustSqlCertificate,
            ["Connect Timeout"]=10,["Application Name"]="LMC instalador visual"};
        return b.ConnectionString;
    }
    public string Redact(string message)
    {
        foreach(var secret in new[]{SqlPassword,OperatorPassword,OperatorPasswordConfirm}.Where(s=>!string.IsNullOrEmpty(s)).OrderByDescending(s=>s.Length))
            message=message.Replace(secret,"[oculto]",StringComparison.Ordinal);
        return message;
    }
    public static string ExplainSqlError(string output)=>output.Contains("-2146893019")
        ?"O Windows não confia no certificado do SQL Server. Configure um certificado confiável ou, se este é o SQL da rede interna do posto, marque a opção Aceitar o certificado próprio do SQL Server e teste novamente. Essa opção mantém a criptografia e dispensa a validação da identidade do certificado."
        :output.Contains("18456")?"O SQL Server recusou o login (18456). Confira o usuário e a senha do banco, e se a instância aceita autenticação SQL."
        :output.Trim();
    public static string[] LocalAddresses()=>NetworkInterface.GetAllNetworkInterfaces()
        .Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback)
        .OrderBy(n=>n.GetIPProperties().GatewayAddresses.Count==0)
        .SelectMany(n=>n.GetIPProperties().UnicastAddresses).Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&!a.Address.ToString().StartsWith("169.254."))
        .Select(a=>a.Address.ToString()).Distinct().ToArray();
    public static async Task<bool> IsLocalAddress(string host)
    {
        try {var addresses=await Dns.GetHostAddressesAsync(host);return addresses.Any(a=>LocalAddresses().Contains(a.ToString()));}
        catch(SocketException){return false;}
    }
}
