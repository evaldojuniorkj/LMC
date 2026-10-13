using System.Text.Json;

namespace Lmc.Web;

public sealed record BookAuditRecord(int Format,Guid AttemptId,string Status,string RecordedAt,
    string Server,string Database,short Branch,string Date,int ProductId,BookChange Change);
public sealed record BookAuditAttempt(string FilePath,BookAuditRecord Record);

// Arquivos locais, fora do conteúdo público e sem credenciais de conexão.
// Um arquivo por tentativa permite atualizar o resultado atomicamente, inclusive entre processos.
public sealed class BookAuditLog(string directory)
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};

    public static string DirectoryFor(IConfiguration config,string contentRoot)
    {
        if(!string.IsNullOrWhiteSpace(config["AuditLog:Directory"]))return Path.GetFullPath(config["AuditLog:Directory"]!);
        return !string.IsNullOrWhiteSpace(config["ProtectedConfig"])
            ?Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config["ProtectedConfig"]!))!,"audit-log")
            :Path.Combine(contentRoot,"App_Data","audit-log");
    }

    public BookAuditAttempt Begin(string server,string database,short branch,string date,int productId,BookChange change)
    {
        var now=DateTimeOffset.Now;
        var record=new BookAuditRecord(1,Guid.NewGuid(),"prepared",now.ToString("O"),server,database,branch,date,productId,change);
        var folder=Path.Combine(directory,now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,$"lmc-{change.OperationId:N}-{record.AttemptId:N}.log");
        Write(path,record,replace:false);
        return new(path,record);
    }

    public void Complete(BookAuditAttempt attempt,string status)
    {
        if(status is not ("committed" or "rolled_back" or "unknown"))throw new ArgumentException("Resultado de auditoria inválido.",nameof(status));
        Write(attempt.FilePath,attempt.Record with {Status=status,RecordedAt=DateTimeOffset.Now.ToString("O")},replace:true);
    }

    private static void Write(string path,BookAuditRecord record,bool replace)
    {
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(file,record,Json);file.Flush(flushToDisk:true);}
            File.Move(temporary,path,overwrite:replace);
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
}
