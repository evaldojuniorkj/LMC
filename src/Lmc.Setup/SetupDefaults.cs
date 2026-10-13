using System.ComponentModel;
using System.Security.Cryptography;
using System.ServiceProcess;

namespace Lmc.Setup;

public sealed record LocalSqlInstance(string Address,bool Running);
public sealed record SetupDefaults(SetupOptions Options,string[] SqlServers)
{
    public static SetupDefaults Discover()
    {
        var instances=new List<LocalSqlInstance>();
        try
        {
            foreach(var service in ServiceController.GetServices())
            {
                using(service)
                {
                    var name=service.ServiceName;
                    if(name.Equals("MSSQLSERVER",StringComparison.OrdinalIgnoreCase))instances.Add(new("localhost",service.Status==ServiceControllerStatus.Running));
                    else if(name.StartsWith("MSSQL$",StringComparison.OrdinalIgnoreCase))instances.Add(new("localhost\\"+name[6..],service.Status==ServiceControllerStatus.Running));
                }
            }
        }
        catch(Exception ex)when(ex is Win32Exception or InvalidOperationException){/* O campo continua editável se a descoberta não estiver disponível. */}
        return Create(instances,SetupOptions.LocalAddresses());
    }
    public static SetupDefaults Create(IEnumerable<LocalSqlInstance> source,IEnumerable<string> addresses)
    {
        var instances=source.DistinctBy(i=>i.Address,StringComparer.OrdinalIgnoreCase).ToArray();
        var running=instances.Where(i=>i.Running).ToArray();
        var sql=running.FirstOrDefault(i=>i.Address=="localhost")?.Address
            ??(running.Length==1?running[0].Address:instances.FirstOrDefault(i=>i.Address=="localhost")?.Address)
            ??(instances.Length==1?instances[0].Address:"localhost");
        var password=NewOperatorPassword();
        var options=new SetupOptions(sql,"sa","",true,addresses.FirstOrDefault()??Environment.MachineName,
            "operador",password,password,5443,true,true);
        return new(options,new[]{sql}.Concat(instances.Select(i=>i.Address).Order()).Append("localhost").Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
    public static string NewOperatorPassword()=>RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789",10);
}
