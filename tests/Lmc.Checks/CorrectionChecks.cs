using System.Data;
using Lmc.Web;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

static class CorrectionChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        const string connection="Server=localhost;Database=ATXDADOS;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=5";
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:AtxDados",connection}}).Build();
        var repo=new Repository(config);
        foreach(var sample in new[]{
            (Id:560835,Date:"2026-09-30",Reason:(string?)null,General:false),
            (Id:541442,Date:"2026-09-29",Reason:"",General:false),
            (Id:196993,Date:"2026-09-30",Reason:"   ",General:false),
            (Id:560835,Date:"2026-09-29",Reason:"Ajuste",General:false),
            (Id:541442,Date:"2026-08-14",Reason:"",General:true),
            (Id:196993,Date:"2026-10-07",Reason:(string?)null,General:true)})
        {
            var before=await repo.Detail(17581,17581,sample.Id,default)??throw new Exception("Caso de referência ausente.");
            var shifts=await repo.Shifts(17581,DateOnly.Parse(sample.Date),default);
            var destination=sample.General ? shifts.Single(s=>s.IsGeneral && s.Id==before.Note.ShiftId) : shifts.Last(s=>!s.IsGeneral);
            var input=new ApplyRequest(sample.Date,destination.Id,sample.Reason,before.Version,Guid.NewGuid(),true);
            var stocksBefore=await Snapshot(connection);
            await using var c=new SqlConnection(connection);
            await c.OpenAsync();
            using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
            {
                try
                {
                    var receipt=await repo.ApplyInTransaction(c,tx,17581,17581,sample.Id,input,default);
                    check(!receipt.AlreadyApplied && receipt.TargetDate==sample.Date,$"{sample.Id}: gravação conjunta executada dentro da transação");
                    if(sample.General) check(receipt.SourceShiftId==receipt.TargetShiftId && receipt.TargetShiftId==before.Note.ShiftId,
                        $"{sample.Id}: data corrigida mantendo o turno geral, sem depender do turno diário");
                    check(receipt.Reason==(sample.Reason?.Trim()??""),$"{sample.Id}: histórico aceita observação ausente, vazia ou curta sem inventar motivo");
                    if(sample.Id==541442) check(receipt.ReviewFrom=="2026-08-14","fila abrange recebimento antigo, anterior à origem atual da nota");
                    check(stocksBefore==await Snapshot(c,tx),$"{sample.Id}: triggers não alteram livros ou medições durante a correção");
                    var repeated=await repo.ApplyInTransaction(c,tx,17581,17581,sample.Id,input with {Reason=receipt.Reason},default);
                    check(repeated.AlreadyApplied && repeated.HistoryId==receipt.HistoryId,$"{sample.Id}: repetição retorna o mesmo recibo");
                    try { await repo.ApplyInTransaction(c,tx,17581,17581,sample.Id,input with {Reason="Outra solicitação usando o mesmo identificador."},default); throw new Exception("Reutilização indevida aceita."); }
                    catch(CorrectionException ex) { check(ex.Status==409,$"{sample.Id}: identificador com outro conteúdo é rejeitado"); }
                    try { await repo.ApplyInTransaction(c,tx,17581,17581,sample.Id,input with {OperationId=Guid.NewGuid()},default); throw new Exception("Versão antiga aceita."); }
                    catch(CorrectionException ex) { check(ex.Status==409,$"{sample.Id}: nova operação com versão antiga é rejeitada"); }
                    var audit=CorrectionAudit.Encode(before,input,Repository.Actor);
                    check(audit.Length<=255,$"{sample.Id}: histórico cabe no campo existente");
                }
                finally { await tx.RollbackAsync(); }
            }
            var after=await repo.Detail(17581,17581,sample.Id,default);
            check(after!.Version==before.Version,$"{sample.Id}: rollback restaura nota e vínculos");
            check(stocksBefore==await Snapshot(connection),$"{sample.Id}: livros e medições permanecem iguais após rollback");
            check(!(await repo.History(17581,17581,sample.Id,default)).Any(h=>h.OperationId==input.OperationId),$"{sample.Id}: histórico também desfeito");
        }
        var closed=await repo.Assess(17581,17581,541442,new DateOnly(2026,8,14),8912,default);
        check(closed.Blockers.Any(b=>b.Contains("fechamento")),"turno com marca de fechamento bloqueia a aplicação");
        var wrongDay=await repo.Assess(17581,17581,541442,new DateOnly(2026,8,14),8933,default);
        check(wrongDay.Blockers.Count>0,"turno diário de outra data continua rejeitado na gravação");
        check((await repo.Shifts(17582,new DateOnly(2026,8,14),default)).Count==0,"turno geral da filial 17581 não é oferecido para outra filial");
        var failureBefore=await repo.Detail(17581,17581,560835,default)??throw new Exception("Caso de falha ausente.");
        await using(var c=new SqlConnection(connection))
        {
            await c.OpenAsync();
            using var tx=c.BeginTransaction(IsolationLevel.Serializable);
            try
            {
                var invalidText=new ApplyRequest("2026-09-30",8960,"Ensaio com símbolo incompatível no histórico: 🚨",failureBefore.Version,Guid.NewGuid(),true);
                try { await repo.ApplyInTransaction(c,tx,17581,17581,560835,invalidText,default);throw new Exception("Texto incompatível não foi rejeitado nesta cópia."); }
                catch(CorrectionException ex) {check(ex.Status==422 && ex.Message.Contains("caracteres"),"falha no histórico depois das atualizações impede conclusão");}
            }
            finally {await tx.RollbackAsync();}
        }
        check((await repo.Detail(17581,17581,560835,default))!.Version==failureBefore.Version,"falha posterior à atualização não deixa uma correção parcial");
    }

    private static async Task<string> Snapshot(string connection)
    {
        await using var c=new SqlConnection(connection);
        await c.OpenAsync();
        return await Snapshot(c,null);
    }

    private static async Task<string> Snapshot(SqlConnection c,SqlTransaction? tx)
    {
        using var cmd=new SqlCommand("""
            SELECT (SELECT * FROM dbo.LMC ORDER BY ID_FILIAL,ID_LMC FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.LMCCOMPROVANTES ORDER BY ID_FILIAL,ID_LMCCOMPROVANTES FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.LMCTANQUES ORDER BY ID_FILIAL,ID_LMCTANQUES FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.LEITURASTANQUES ORDER BY ID_FILIAL,ID_LEITURASTANQUES FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.ESTOQUETANQUELMC ORDER BY ID_FILIAL,ID_ESTOQUETANQUESLMC FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.MOVTANQUES ORDER BY ID_FILIAL,ID_DB,ID_MOVTANQUES FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.QTDESTANQUES ORDER BY ID_FILIAL,ID_TANQUES FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.TURNOS ORDER BY ID_FILIAL,ID_TURNOS FOR XML RAW,BINARY BASE64);
            """,c,tx);
        await using var r=await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return string.Join("\n",Enumerable.Range(0,r.FieldCount).Select(i=>r.IsDBNull(i)?"":r.GetString(i)));
    }
}
