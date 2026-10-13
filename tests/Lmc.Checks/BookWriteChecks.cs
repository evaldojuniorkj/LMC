using System.Data;
using Lmc.Web;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

internal static class BookWriteChecks
{
    public static void Rules(Action<bool,string> check)
    {
        var p=new BookPreparation(17581,"2026-09-29",1,"Gasolina",null,30,new string('A',64),
            [new(1,30000,10000,true)],[new(1,1,1000,2000,0,7,1000,"teste")],[],[],[],[],"",[]);
        var input=new BookPreviewRequest(17581,p.Date,1,p.Version,[new(1,10000,9000)],"","manual");
        check(BookWriting.Preview(p,input).CanSave,"gravação permite medições completas com balanço coerente");
        check(!BookWriting.Preview(p,input with {Tanks=[new(1,9999,9000)]}).CanSave,"abertura herdada não pode divergir do fechamento anterior");
        check(!BookWriting.Preview(p,input with {Tanks=[new(1,10000,null)]}).CanSave,"fechamento ausente não pode ser gravado");
        check(!BookWriting.Preview(p,input with {Tanks=[new(1,10000,8500)]}).CanSave,"variação acima da referência exige observação no LMC");
        check(BookWriting.Preview(p,input with {Tanks=[new(1,10000,8500)],Observation="Diferença conferida para apuração."}).CanSave,"justificativa não substitui nem modifica a medição real");
        check(!BookWriting.Preview(p,input with {Tanks=[new(1,10000,31000)],Observation="Teste"}).CanSave,"capacidade excedida bloqueia gravação");
        check(!BookWriting.Preview(p,input with {Tanks=[new(2,10000,9000)]}).CanSave,"tanque estranho não entra na gravação");
        var pair=p with {Tanks=[new(1,30000,null,false),new(2,30000,null,false)]};
        var negativeTank=BookWriting.Preview(pair,input with {Tanks=[new(1,500,0),new(2,10000,9500)]});
        check(!negativeTank.CanSave && negativeTank.Blockers.Any(x=>x.Contains("Tanque 1: o estoque escritural")),"tanque negativo não é compensado pelo saldo positivo de outro tanque");
        try {BookWriting.Preview(p,input with {Version=new string('B',64)});throw new Exception("Conflito ignorado");}
        catch(CorrectionException ex) {check(ex.Status==409,"fonte alterada invalida a gravação");}
        var raw="Observação original com acentos.";
        check(BookWriting.ObservationForSave(raw)==raw,"observação salva contém somente o texto do operador, sem auditoria");
        check(BookWriting.ObservationForSave(null)=="" && BookWriting.ObservationForSave("  ")=="","livro sem observação não recebe texto automático");
        check(BookWriting.ObservationForSave("  Conferência\nMedição manual.  ")=="Conferência\nMedição manual.","observação conserva acentos e quebras de linha");
        var legacy=raw+BookWriting.AuditMarker+"{\"Changes\":[]}";
        check(BookWriting.ReadAudit(legacy).Observation==raw && BookWriting.ObservationForSave(BookWriting.ReadAudit(legacy).Observation)==raw,
            "reabrir e refazer livro antigo usa somente a observação humana");
        check(BookWriting.ReadAudit(raw).Observation==raw,"livro sem auditoria continua abrindo normalmente");
        foreach(var invalid in new[]{new string('A',2001),legacy})
        {
            try {BookWriting.ValidateInput(input with {Observation=invalid});throw new Exception("Observação inválida aceita");}
            catch(CorrectionException ex){check(ex.Status==400,"prévia não permite devolver o bloco técnico às observações");}
        }
    }

    public static async Task Rollback(Action<bool,string> check)
    {
        const string connection="Server=localhost;Database=ATXDADOS;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=5";
        var repo=new Repository(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:AtxDados",connection}}).Build());
        foreach(var date in new[]{"2026-09-29","2026-09-24"})
        {
            var p=await repo.PrepareBook(17581,DateOnly.Parse(date),1,default);
            check(p.Blockers.Count==0,$"{date}: fontes aptas para ensaio de gravação");
            var input=new BookSaveRequest(17581,date,1,p.Version,p.Tanks.Select(t=>new PhysicalInput(t.TankId,t.Inherited?t.PreviousClosing:10000,8000)).ToArray(),
                "ENSAIO TRANSACIONAL COM ROLLBACK - volumes de teste, sem persistência.","manual",Guid.NewGuid());
            await using var c=new SqlConnection(connection);await c.OpenAsync();
            var before=await Snapshot(c,null);
            using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
            {
                try
                {
                    var result=await repo.SaveBookInTransaction(c,tx,input,default);
                    check(!result.AlreadyApplied && result.Mode==(p.BookId is null?"inserido":"refeito"),$"{date}: livro completo {result.Mode} dentro da transação");
                    if(p.BookId.HasValue) check(result.BookId==p.BookId,"refazer preserva identificação do livro");
                    try {await repo.SaveBookInTransaction(c,tx,input,default);throw new Exception("Repetição com versão antiga aceita");}
                    catch(CorrectionException ex) {check(ex.Status==409,$"{date}: repetição exige atualizar o livro, sem duplicação");}
                    try {await repo.SaveBookInTransaction(c,tx,input with {Observation="Outro conteúdo"},default);throw new Exception("Reutilização indevida aceita");}
                    catch(CorrectionException ex) {check(ex.Status==409,"conteúdo diferente com versão antiga é rejeitado");}
                    try {await repo.SaveBookInTransaction(c,tx,input with {OperationId=Guid.NewGuid()},default);throw new Exception("Versão antiga aceita");}
                    catch(CorrectionException ex) {check(ex.Status==409,"uma segunda operação com versão antiga é rejeitada");}
                    using var count=new SqlCommand("SELECT COUNT(*) FROM dbo.LMC WHERE ID_FILIAL=17581 AND ID_PRODUTOS=1 AND DTACONTA=@date",c,tx);
                    count.Parameters.Add("@date",SqlDbType.Date).Value=DateTime.Parse(date);
                    check(Convert.ToInt32(await count.ExecuteScalarAsync())==1,"gravação não duplica livro por dia/produto");
                    using var note=new SqlCommand("SELECT CONVERT(varchar(max),OBS) FROM dbo.LMC WHERE ID_FILIAL=17581 AND ID_LMC=@id",c,tx);
                    note.Parameters.Add("@id",SqlDbType.Int).Value=result.BookId;
                    check((string?)await note.ExecuteScalarAsync()==input.Observation,"OBS contém somente a observação digitada, sem marcador nem JSON");
                    var reopened=await Repository.PrepareBookInTransaction(c,tx,17581,DateOnly.Parse(date),1,default);
                    check(reopened.SavedMeasurements is not null && reopened.SavedMeasurements.OrderBy(t=>t.TankId).SequenceEqual(input.Tanks.OrderBy(t=>t.TankId))
                        && reopened.MeasurementMethod is null,"reabertura carrega os volumes gravados sem inventar a origem da medição");
                    check(reopened.Observation==input.Observation && reopened.History.Count==0,"reabertura não depende de auditoria para mostrar os dados salvos");
                    var next=await Repository.PrepareBookInTransaction(c,tx,17581,DateOnly.Parse(date).AddDays(1),1,default);
                    check(next.Tanks.All(t=>t.Inherited && t.PreviousClosing==8000),"dia seguinte herda o fechamento físico salvo nesta interface");
                    using var receipts=new SqlCommand("SELECT COUNT(*) FROM dbo.LMCCOMPROVANTES WHERE ID_FILIAL=17581 AND ID_LMC=@id",c,tx);
                    receipts.Parameters.Add("@id",SqlDbType.Int).Value=result.BookId;
                    check(Convert.ToInt32(await receipts.ExecuteScalarAsync())==p.Documents.Count,"livro grava exatamente as parcelas das entradas atuais");
                }
                finally {await tx.RollbackAsync();}
            }
            check(before==await Snapshot(c,null),$"{date}: rollback restaura integralmente livros, medições, notas e histórico");
        }
        var failure=await repo.PrepareBook(17581,new DateOnly(2026,9,24),1,default);
        await using(var c=new SqlConnection(connection))
        {
            await c.OpenAsync();var before=await Snapshot(c,null);
            using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
            {
                try
                {
                    var input=new BookSaveRequest(17581,"2026-09-24",1,failure.Version,failure.Tanks.Select(t=>new PhysicalInput(t.TankId,10000,8000)).ToArray(),"Ensaio de falha posterior às inserções 🚨","manual",Guid.NewGuid());
                    try {await repo.SaveBookInTransaction(c,tx,input,default);throw new Exception("Caractere não representável aceito");}
                    catch(CorrectionException ex) {check(ex.Status==422&&ex.Message.Contains("caracteres"),"falha após refazer detalhes impede a confirmação");}
                }
                finally {await tx.RollbackAsync();}
            }
            check(before==await Snapshot(c,null),"falha posterior à escrita não deixa livro parcialmente refeito");
        }
    }

    internal static async Task<string> Snapshot(SqlConnection c,SqlTransaction? tx)
    {
        using var cmd=new SqlCommand("""
            SET QUOTED_IDENTIFIER ON;
            SELECT (SELECT * FROM dbo.LMC ORDER BY ID_FILIAL,ID_LMC FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.LMCBICOS ORDER BY ID_FILIAL,ID_LMCBICOS FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.LMCTANQUES ORDER BY ID_FILIAL,ID_LMCTANQUES FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.LMCCOMPROVANTES ORDER BY ID_FILIAL,ID_LMCCOMPROVANTES FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.LEITURASTANQUES ORDER BY ID_FILIAL,ID_LEITURASTANQUES FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.ESTOQUETANQUELMC ORDER BY ID_FILIAL,ID_ESTOQUETANQUESLMC FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.MOVTANQUES ORDER BY ID_FILIAL,ID_DB,ID_MOVTANQUES FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.LMCENTRADATANQUES ORDER BY ID_FILIAL,ID_LMCENTRADATANQUES FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.COMPROVANTES WHERE ID_COMPROVANTE IN(SELECT ID_COMPROVANTE FROM dbo.LMCENTRADATANQUES) ORDER BY ID_FILIAL,ID_DB,ID_COMPROVANTE FOR XML RAW,BINARY BASE64),
              (SELECT * FROM dbo.CFGLMC FOR XML RAW,BINARY BASE64);
            """,c,tx){CommandTimeout=30};
        await using var r=await cmd.ExecuteReaderAsync();await r.ReadAsync();
        return BookWriting.Hash(Enumerable.Range(0,r.FieldCount).Select(i=>r.IsDBNull(i)?"":r.GetString(i)).ToArray());
    }
}
