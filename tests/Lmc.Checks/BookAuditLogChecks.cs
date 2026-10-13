using System.Text.Json;
using Lmc.Web;
using Microsoft.Extensions.Configuration;

static class BookAuditLogChecks
{
    public static void Run(Action<bool,string> check)
    {
        var temporaryRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var folder=Path.Combine(temporaryRoot,"lmc-audit-checks-"+Guid.NewGuid().ToString("N"));
        var config=new ConfigurationBuilder().AddInMemoryCollection().Build();
        check(BookAuditLog.DirectoryFor(config,folder)==Path.Combine(folder,"App_Data","audit-log"),"log local fica fora da pasta pública da aplicação");
        config["ProtectedConfig"]=Path.Combine(folder,"production","settings.bin");
        check(BookAuditLog.DirectoryFor(config,folder)==Path.Combine(folder,"production","audit-log"),"produção mantém os logs junto à configuração, fora da instalação");
        config["AuditLog:Directory"]=Path.Combine(folder,"custom");
        check(BookAuditLog.DirectoryFor(config,folder)==config["AuditLog:Directory"],"diretório do log pode ser configurado");
        BookAuditRecord Read(BookAuditAttempt attempt)=>JsonSerializer.Deserialize<BookAuditRecord>(File.ReadAllText(attempt.FilePath))!;
        try
        {
            var log=new BookAuditLog(folder);
            var change=new BookChange(Guid.NewGuid(),DateTimeOffset.Now.ToString("O"),"operador","fingerprint",42,"refeito","[\"antes\"]","Observação anterior",
                [new(1,10000,9000)],[],[43],"eletronica",[new(1,2000,0,2010,10)],"Medição conferida.\nSegunda linha.");
            var attempt=log.Begin("servidor","ATXDADOS",4500,"2026-09-01",1,change);
            var prepared=Read(attempt);
            check(prepared.Status=="prepared" && prepared.Change.OperationId==change.OperationId,"log antes do commit não afirma que o LMC foi salvo");
            check(prepared.Branch==4500 && prepared.Database=="ATXDADOS" && prepared.Date=="2026-09-01" && prepared.Change.Actor=="operador","log identifica base, filial, dia e operador sem armazenar conexão ou senha");
            check(prepared.Change.Measurements.Single().Closing==9000 && prepared.Change.PumpChanges!.Single().Calibration==10
                && prepared.Change.BeforeRows==change.BeforeRows && prepared.Change.Observation==change.Observation,"log preserva antes/depois, aferição e observação com quebras de linha");
            log.Complete(attempt,"committed");
            check(Read(attempt).Status=="committed" && Read(attempt).Change.MeasurementMethod=="eletronica","confirmação atualiza o resultado e conserva os dados de auditoria");
            var repeated=log.Begin("servidor","ATXDADOS",4500,"2026-09-01",1,change);
            check(repeated.FilePath!=attempt.FilePath && Read(attempt).Status=="committed","nova tentativa com mesmo UUID não sobrescreve uma gravação confirmada");
            log.Complete(repeated,"rolled_back");
            check(Read(repeated).Status=="rolled_back","tentativa desfeita fica identificada no arquivo");
            var uncertain=log.Begin("servidor","ATXDADOS",4500,"2026-09-01",1,change);
            log.Complete(uncertain,"unknown");
            check(Read(uncertain).Status=="unknown","falha sem confirmação preserva resultado incerto");
            var locked=log.Begin("servidor","ATXDADOS",4500,"2026-09-01",1,change);
            using(var lease=new FileStream(locked.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                try {log.Complete(locked,"committed");throw new Exception("Arquivo bloqueado foi substituído");}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){check(Read(locked).Status=="prepared","falha na confirmação conserva o registro preparado sem truncá-lo");}
            }
            check(!Directory.EnumerateFiles(folder,"*.tmp",SearchOption.AllDirectories).Any(),"gravações e falha não deixam arquivos temporários");
            var blocked=Path.Combine(folder,"not-a-directory");File.WriteAllText(blocked,"preservar");
            try {new BookAuditLog(blocked).Begin("servidor","ATXDADOS",4500,"2026-09-01",1,change);throw new Exception("Pasta inválida foi aceita");}
            catch(IOException){check(File.ReadAllText(blocked)=="preservar","falha ao preparar o log é propagada sem substituir arquivos existentes");}
        }
        finally
        {
            if(Path.GetDirectoryName(Path.GetFullPath(folder))!=temporaryRoot || !Path.GetFileName(folder).StartsWith("lmc-audit-checks-",StringComparison.Ordinal))
                throw new InvalidOperationException("Diretório temporário inesperado.");
            if(Directory.Exists(folder))Directory.Delete(folder,true);
        }
    }
}
