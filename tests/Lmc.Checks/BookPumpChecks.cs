using System.Data;
using Lmc.Web;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

static class BookPumpChecks
{
    public static void Rules(Action<bool,string> check)
    {
        var p=new BookPreparation(17581,"2026-09-01",1,"Gasolina",null,2,new string('A',64),
            [new(1,30000,10000,true)],[new(1,1,1000,2000,0,7,1000,"Automação")],[],[],[],[],"",[]);
        var input=new BookPreviewRequest(p.Branch,p.Date,p.ProductId,p.Version,[new(1,10000,8990)],"","manual",[new(1,2020,10)]);
        var preview=BookWriting.Preview(p,input);
        check(preview.CanSave && preview.Balance?.Sales==1010 && preview.Balance.BookStock==8990 && preview.SalesValue==7070,"final e aferição editados recalculam vendas, estoque e valor");
        foreach(var invalid in new IReadOnlyList<BookPumpInput>[] {[],[new(2,2020,10)],[new(1,2020,10),new(1,2020,10)],
            [new(1,null,0)],[new(1,2020,null)],[new(1,999,0)],[new(1,2020,1021)],[new(1,2020,-1)],[new(1,2020.12345m,0)],[new(1,2020,.00001m)]})
            check(!BookWriting.Preview(p,input with {Pumps=invalid}).CanSave,"conjunto ou valores inválidos de bicos impedem a gravação");
        check(!BookWriting.Preview(p,input with {Pumps=[new(1,12000,0)],Observation="Conferido"}).CanSave,"vendas editadas não podem causar estoque negativo");
        check(BookWriting.Preview(p,input with {Pumps=[new(1,1000,0)],Tanks=[new(1,10000,10000)]}).CanSave,"movimento e aferição zero são permitidos");
        var cutoff=new BookPumpCutoff(1000,2000,990,2020,"2026-09-01T00:05:00","2026-09-01T23:55:00");
        var divergent=p with {Pumps=[p.Pumps[0] with {Cutoff=cutoff}]};
        foreach(var missing in new IReadOnlyList<BookPumpInput>?[] {null,[new(1,2000,0)],[new(1,2000,0,"ignorar")]})
            check(!BookWriting.Preview(divergent,input with {Pumps=missing}).CanSave,"diferença de corte exige origem explícita também no servidor");
        var automation=input with {Pumps=[new(1,2000,0,"automation")],Tanks=[new(1,10000,9000)]};
        var shifts=input with {Pumps=[new(1,2020,0,"shifts")],Tanks=[new(1,10000,8980)]};
        var ambiguous=divergent with {Pumps=[divergent.Pumps[0] with {Cutoff=cutoff with {AutomationAmbiguous=true}}]};
        check(!BookWriting.Preview(ambiguous,automation).CanSave,"automação ambígua não pode ser confirmada como fonte");
        var candidates=ambiguous with {Pumps=[ambiguous.Pumps[0] with {Cutoff=cutoff with {AutomationAmbiguous=true,AutomationCandidates=[2000,2001]}}]};
        check(BookWriting.Preview(candidates,automation).CanSave,"automação permite escolha explícita de um fechamento registrado");
        check(!BookWriting.Preview(candidates,automation with {Pumps=[new(1,2002,0,"automation")]}).CanSave,"automação conflitante rejeita fechamento fora dos candidatos");
        check(BookWriting.Preview(ambiguous,shifts).CanSave,"turno confirmado libera automação ambígua mantendo abertura fixa");
        check(!BookWriting.Preview(ambiguous,shifts with {Pumps=[new(1,2020,0)]}).CanSave,"automação ambígua exige confirmação explícita do turno");
        check(BookWriting.Preview(divergent,automation) is {CanSave:true,Balance.Sales:1000},"confirmar automação permite continuar com seus encerrantes");
        check(BookWriting.Preview(divergent,shifts) is {CanSave:true,Balance.Sales:1020,SalesValue:7140},"escolher fechamento dos turnos conserva a abertura fixa e recalcula vendas e valor");
        foreach(var choice in new[]{"automation","shifts"})
            check(BookWriting.ApplyPumpInputs(divergent,[new(1,2020,0,choice)],[])[0].Opening==1000,"nenhuma escolha de fechamento pode substituir a abertura pela fonte alternativa");
        var forged=System.Text.Json.JsonSerializer.Deserialize<BookPumpInput>("{\"PumpId\":1,\"Opening\":10,\"Closing\":2020,\"Calibration\":0,\"CounterSource\":\"shifts\"}")!;
        check(BookWriting.ApplyPumpInputs(divergent,[forged],[])[0].Opening==1000,"abertura enviada por fora da interface não altera a abertura calculada no servidor");
        check(BookWriting.Preview(divergent,shifts with {Pumps=[new(1,2021,1,"shifts")]}).CanSave,"final e aferição continuam editáveis após escolher a origem");
        check(!BookWriting.Preview(divergent,shifts with {Pumps=[new(1,995,0,"shifts")]}).CanSave,"final abaixo da abertura fixa é rejeitado mesmo se exceder a abertura dos turnos");
        check(!BookWriting.Preview(divergent with {Blockers=["Fonte ambígua"]},automation).CanSave,"conferência do corte não remove os demais impedimentos");
        check(!BookWriting.Preview(p,automation).CanSave,"origem enviada para bico sem divergência é rejeitada");
        try {BookWriting.Preview(divergent,automation with {Version=new string('B',64)});check(false,"versão antiga rejeitada");}
        catch(CorrectionException ex){check(ex.Status==409,"fontes alteradas exigem nova conferência mesmo após escolher a origem");}
    }

    public static async Task Rollback(Action<bool,string> check)
    {
        const string connection="Server=localhost;Database=ATXDADOS;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=5";
        var repo=new Repository(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:AtxDados",connection}}).Build());
        await using var c=new SqlConnection(connection);await c.OpenAsync();
        var before=await BookWriteChecks.Snapshot(c,null);
        using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
        {
            try
            {
                using var latest=new SqlCommand("SELECT MAX(DTACONTA) FROM dbo.LMC WHERE ID_FILIAL=17581 AND ID_PRODUTOS=1",c,tx);
                var day=DateOnly.FromDateTime((DateTime)(await latest.ExecuteScalarAsync())!);
                var p=await Repository.PrepareBookInTransaction(c,tx,17581,day,1,default);
                check(p.Blockers.Count==0 && p.BookId.HasValue && p.SavedMeasurements is not null,"livro existente disponível para ensaio transacional");
                var changed=p.Pumps.Select((b,i)=>new BookPumpInput(b.PumpId,b.Closing+(i==0?2:0),b.Calibration+(i==0?1:0))).ToArray();
                var tanks=p.Tanks.Select(t=>{var saved=p.SavedMeasurements!.Single(s=>s.TankId==t.TankId);return saved with {PreviousClosing=t.Inherited?t.PreviousClosing:saved.PreviousClosing};}).ToArray();
                var request=new BookSaveRequest(p.Branch,p.Date,p.ProductId,p.Version,tanks,"Ensaio de bicos com rollback obrigatório.","manual",Guid.NewGuid(),changed);
                var sourceBefore=await Sources(c,tx,day);
                var result=await repo.SaveBookInTransaction(c,tx,request,default);
                var reopened=await Repository.PrepareBookInTransaction(c,tx,p.Branch,day,p.ProductId,default);
                check(reopened.Blockers.Count==0,"reabertura não bloqueia por diferença entre ajuste salvo e automação");
                check(reopened.Pumps.All(b=>{var edited=changed.Single(x=>x.PumpId==b.PumpId);return b.Closing==edited.Closing&&b.Calibration==edited.Calibration&&b.Sales==b.Closing-b.Opening-b.Calibration;}),"reabertura conserva encerrantes, aferições e vendas gravados");
                check(reopened.Pumps.Sum(b=>b.Sales)==p.Pumps.Sum(b=>b.Sales)+1,"vendas gravadas refletem a diferença líquida de um litro");
                check(await Sources(c,tx,day)==sourceBefore,"CONSOLEARQUIVO e ENCERRANTESTURNOS do dia permanecem intactos");
                using var auditCmd=new SqlCommand("SELECT CONVERT(varchar(max),OBS) FROM dbo.LMC WHERE ID_FILIAL=17581 AND ID_LMC=@id",c,tx);
                auditCmd.Parameters.Add("@id",SqlDbType.Int).Value=result.BookId;
                check((string?)await auditCmd.ExecuteScalarAsync()==request.Observation,"ajustes dos bicos não adicionam auditoria às observações");
                try {await repo.SaveBookInTransaction(c,tx,request,default);throw new Exception("Repetição com versão antiga aceita");}
                catch(CorrectionException ex){check(ex.Status==409,"repetição com ajustes exige atualizar o livro");}
                try {await repo.SaveBookInTransaction(c,tx,request with {Pumps=changed.Select((b,i)=>i==0?b with {Calibration=b.Calibration+1}:b).ToArray()},default);throw new Exception("Reutilização aceita");}
                catch(CorrectionException ex){check(ex.Status==409,"versão antiga não aceita nova aferição");}
                var again=new BookPreviewRequest(reopened.Branch,reopened.Date,reopened.ProductId,reopened.Version,reopened.SavedMeasurements!,"Conferido","manual",changed);
                check(BookWriting.Preview(reopened,again).CanSave,"livro reaberto com ajustes permite nova gravação");
                var next=await Repository.PrepareBookInTransaction(c,tx,p.Branch,day.AddDays(1),p.ProductId,default);
                check(next.BookId is null && next.Blockers.Count==0,"dia seguinte disponível para ensaio de inserção");
                var nextPumps=next.Pumps.Select((b,i)=>new BookPumpInput(b.PumpId,b.Closing+(i==0?2:0),b.Calibration+(i==0?1:0))).ToArray();
                var nextRequest=new BookSaveRequest(next.Branch,next.Date,next.ProductId,next.Version,
                    next.Tanks.Select(t=>new PhysicalInput(t.TankId,t.PreviousClosing,t.PreviousClosing)).ToArray(),
                    "Ensaio de inserção com rollback obrigatório.","manual",Guid.NewGuid(),nextPumps);
                var inserted=await repo.SaveBookInTransaction(c,tx,nextRequest,default);
                var newBook=await Repository.PrepareBookInTransaction(c,tx,p.Branch,day.AddDays(1),p.ProductId,default);
                check(inserted.Mode=="inserido" && newBook.Pumps.All(b=>{var expected=nextPumps.Single(x=>x.PumpId==b.PumpId);return b.Closing==expected.Closing&&b.Calibration==expected.Calibration;}),"inserção também conserva os ajustes de encerrantes e aferição");
            }
            finally {await tx.RollbackAsync();}
        }
        check(before==await BookWriteChecks.Snapshot(c,null),"rollback restaura integralmente livros e fontes");
    }
    public static async Task CutoffRollback(Action<bool,string> check)
    {
        const string connection="Server=localhost;Database=ATXDADOS;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=5";
        var repo=new Repository(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:AtxDados",connection}}).Build());
        await using var c=new SqlConnection(connection);await c.OpenAsync();
        using var last=new SqlCommand("SELECT MAX(DTACONTA) FROM dbo.LMC WHERE ID_FILIAL=17581 AND ID_PRODUTOS=1",c);
        var day=DateOnly.FromDateTime((DateTime)(await last.ExecuteScalarAsync())!).AddDays(1);
        var before=await BookWriteChecks.Snapshot(c,null);var originalSources=await Sources(c,null,day);
        foreach(var source in new[]{"automation","shifts"})
        {
            using var tx=c.BeginTransaction(IsolationLevel.Serializable);
            try
            {
                var original=await Repository.PrepareBookInTransaction(c,tx,17581,day,1,default);
                check(original.BookId is null&&original.Blockers.Count==0,"dia de desenvolvimento disponível para ensaio de corte com rollback");
                var target=original.Pumps.First();
                using(var cmd=new SqlCommand("""
                    DECLARE @changed TABLE(Id int);
                    UPDATE dbo.ENCERRANTESTURNOS SET ENCERRANTEABERTURA=ENCERRANTEABERTURA-1
                    OUTPUT INSERTED.ID_ENCERRANTESTURNOS INTO @changed
                    WHERE ID_FILIAL=17581 AND ID_ENCERRANTESTURNOS=(
                        SELECT TOP(1) e.ID_ENCERRANTESTURNOS FROM dbo.ENCERRANTESTURNOS e
                        JOIN dbo.TURNOS t ON t.ID_FILIAL=e.ID_FILIAL AND t.ID_TURNOS=e.ID_TURNOS
                        WHERE e.ID_FILIAL=17581 AND e.ID_BICOS=@pump AND t.DATATURNO>=@day AND t.DATATURNO<DATEADD(day,1,@day) AND t.TURNO<>0
                        ORDER BY t.TURNO,e.ID_ENCERRANTESTURNOS);
                    SELECT COUNT(*) FROM @changed;
                    """,c,tx))
                {
                    cmd.Parameters.Add("@pump",SqlDbType.SmallInt).Value=target.PumpId;cmd.Parameters.Add("@day",SqlDbType.Date).Value=day.ToDateTime(TimeOnly.MinValue);
                    check(Convert.ToInt32(await cmd.ExecuteScalarAsync())==1,"diferença de abertura simulada apenas dentro da transação");
                }
                var openingOnly=await Repository.PrepareBookInTransaction(c,tx,17581,day,1,default);
                check(openingOnly.Pumps.Single(b=>b.PumpId==target.PumpId).Cutoff is null&&openingOnly.Pumps.Single(b=>b.PumpId==target.PumpId).Opening==target.Opening,
                    "diferença apenas na abertura dos turnos não pede ajuste nem troca a abertura herdada");
                using(var cmd=new SqlCommand("""
                    UPDATE dbo.ENCERRANTESTURNOS SET ENCERRANTEFECHAMENTO=ENCERRANTEFECHAMENTO+1
                    WHERE ID_FILIAL=17581 AND ID_ENCERRANTESTURNOS=(
                        SELECT TOP(1) e.ID_ENCERRANTESTURNOS FROM dbo.ENCERRANTESTURNOS e
                        JOIN dbo.TURNOS t ON t.ID_FILIAL=e.ID_FILIAL AND t.ID_TURNOS=e.ID_TURNOS
                        WHERE e.ID_FILIAL=17581 AND e.ID_BICOS=@pump AND t.DATATURNO>=@day AND t.DATATURNO<DATEADD(day,1,@day) AND t.TURNO<>0
                        ORDER BY t.TURNO DESC,e.ID_ENCERRANTESTURNOS DESC);
                    """,c,tx))
                {
                    cmd.Parameters.Add("@pump",SqlDbType.SmallInt).Value=target.PumpId;cmd.Parameters.Add("@day",SqlDbType.Date).Value=day.ToDateTime(TimeOnly.MinValue);
                    await cmd.ExecuteNonQueryAsync();
                }
                var p=await Repository.PrepareBookInTransaction(c,tx,17581,day,1,default);
                var divergent=p.Pumps.Single(b=>b.PumpId==target.PumpId);
                check(divergent.Cutoff is not null&&p.Blockers.Count==0,"preparação apresenta a diferença conferível e ambas as origens");
                var sourcesBeforeSave=await Sources(c,tx,day);
                var edits=p.Pumps.Select(b=>new BookPumpInput(b.PumpId,b.Cutoff is not null&&b.PumpId==target.PumpId&&source=="shifts"?b.Cutoff.ShiftClosing:b.Closing,b.Calibration,b.Cutoff is null?null:b.PumpId==target.PumpId?source:"automation")).ToArray();
                var request=new BookSaveRequest(p.Branch,p.Date,p.ProductId,p.Version,
                    p.Tanks.Select(t=>new PhysicalInput(t.TankId,t.PreviousClosing,t.PreviousClosing)).ToArray(),
                    "Ensaio de escolha de corte, integralmente desfeito.","manual",Guid.NewGuid(),edits);
                try {await repo.SaveBookInTransaction(c,tx,request with {Pumps=null},default);throw new Exception("Corte sem escolha aceito");}
                catch(CorrectionException ex){check(ex.Status==422,"salvamento direto sem escolha de origem é rejeitado");}
                try {await repo.SaveBookInTransaction(c,tx,request with {Version=original.Version},default);throw new Exception("Versão antiga aceita");}
                catch(CorrectionException ex){check(ex.Status==409,"mudança dos turnos invalida a versão já conferida");}
                var saved=await repo.SaveBookInTransaction(c,tx,request,default);
                var reopened=await Repository.PrepareBookInTransaction(c,tx,17581,day,1,default);
                var pump=reopened.Pumps.Single(b=>b.PumpId==target.PumpId);
                using var priorClosingCmd=new SqlCommand("""
                    SELECT CONVERT(decimal(19,4),b.ENCERRANTEFECHAMENTO) FROM dbo.LMCBICOS b
                    JOIN dbo.LMC l ON l.ID_FILIAL=b.ID_FILIAL AND l.ID_LMC=b.ID_LMC
                    WHERE l.ID_FILIAL=17581 AND l.ID_PRODUTOS=1 AND l.DTACONTA=@previous AND b.ID_BICOS=@pump;
                    """,c,tx);
                priorClosingCmd.Parameters.Add("@previous",SqlDbType.Date).Value=day.AddDays(-1).ToDateTime(TimeOnly.MinValue);
                priorClosingCmd.Parameters.Add("@pump",SqlDbType.SmallInt).Value=target.PumpId;
                var expectedOpening=Convert.ToDecimal(await priorClosingCmd.ExecuteScalarAsync());
                check(pump.Opening==expectedOpening&&divergent.Opening==expectedOpening,"novo livro e reabertura mantêm a abertura igual ao fechamento do LMC anterior");
                check(pump.Opening==expectedOpening&&pump.Closing==edits.Single(e=>e.PumpId==target.PumpId).Closing&&pump.Sales==pump.Closing-pump.Opening-pump.Calibration,$"{source}: escolha gravada e reaberta com vendas corretas");
                check(pump.Cutoff is null&&reopened.Blockers.Count==0,"livro salvo conserva a escolha sem pedir nova decisão ao reabrir");
                using var auditCmd=new SqlCommand("SELECT CONVERT(varchar(max),OBS) FROM dbo.LMC WHERE ID_FILIAL=17581 AND ID_LMC=@id",c,tx);
                auditCmd.Parameters.Add("@id",SqlDbType.Int).Value=saved.BookId;
                check((string?)await auditCmd.ExecuteScalarAsync()==request.Observation,"escolha do encerrante não adiciona auditoria às observações");
                try {await repo.SaveBookInTransaction(c,tx,request,default);throw new Exception("Repetição com versão antiga aceita");}
                catch(CorrectionException ex){check(ex.Status==409,"repetição da escolha exige atualizar o livro");}
                check(sourcesBeforeSave==await Sources(c,tx,day),"salvar o LMC não altera automação nem turnos");
                if(source=="shifts")
                {
                    using(var cmd=new SqlCommand("""
                        UPDATE b SET ENCERRANTEFECHAMENTO=ENCERRANTEFECHAMENTO+0.5 FROM dbo.LMCBICOS b
                        JOIN dbo.LMC l ON l.ID_FILIAL=b.ID_FILIAL AND l.ID_LMC=b.ID_LMC
                        WHERE l.ID_FILIAL=17581 AND l.ID_PRODUTOS=1 AND l.DTACONTA=@previous AND b.ID_BICOS=@pump;
                        """,c,tx))
                    {
                        cmd.Parameters.Add("@previous",SqlDbType.Date).Value=day.AddDays(-1).ToDateTime(TimeOnly.MinValue);
                        cmd.Parameters.Add("@pump",SqlDbType.SmallInt).Value=target.PumpId;await cmd.ExecuteNonQueryAsync();
                    }
                    var changedPrior=Convert.ToDecimal(await priorClosingCmd.ExecuteScalarAsync());
                    var rebuilding=await Repository.PrepareBookInTransaction(c,tx,17581,day,1,default);
                    var rebasedPump=rebuilding.Pumps.Single(b=>b.PumpId==target.PumpId);
                    check(rebasedPump.Opening==changedPrior&&rebasedPump.SavedOpening==expectedOpening&&rebasedPump.Closing==pump.Closing,
                        "ao refazer, abertura acompanha o fechamento anterior salvo e o fechamento do dia é preservado");
                    check(rebuilding.Warnings.Any(w=>w.Contains("ao refazer")),"ajuste de continuidade na reabertura é informado na tela");
                    var rebuiltRequest=request with {Version=rebuilding.Version,OperationId=Guid.NewGuid(),Pumps=rebuilding.Pumps.Select(b=>new BookPumpInput(b.PumpId,b.Closing,b.Calibration)).ToArray()};
                    try {await repo.SaveBookInTransaction(c,tx,rebuiltRequest with {Version=reopened.Version},default);throw new Exception("Fechamento anterior alterado não invalidou a versão");}
                    catch(CorrectionException ex){check(ex.Status==409,"alteração no fechamento anterior invalida a conferência atual");}
                    await repo.SaveBookInTransaction(c,tx,rebuiltRequest,default);
                    check((string?)await auditCmd.ExecuteScalarAsync()==rebuiltRequest.Observation,"reconstrução mantém apenas a observação informada");
                    var verified=await Repository.PrepareBookInTransaction(c,tx,17581,day,1,default);
                    check(verified.Pumps.Single(b=>b.PumpId==target.PumpId).SavedOpening==changedPrior,"abertura gravada após reconstrução é igual ao fechamento do dia anterior");
                }
            }
            finally {await tx.RollbackAsync();}
            check(before==await BookWriteChecks.Snapshot(c,null)&&originalSources==await Sources(c,null,day),"rollback restaura integralmente livros e fontes do ensaio");
        }
    }
    private static async Task<string> Sources(SqlConnection c,SqlTransaction? tx,DateOnly day)
    {
        using var cmd=new SqlCommand("""
            SELECT (SELECT * FROM dbo.CONSOLEARQUIVO WHERE ID_FILIAL=17581 AND DATA>=@date AND DATA<DATEADD(day,1,@date) FOR XML RAW,BINARY BASE64),
            (SELECT e.* FROM dbo.ENCERRANTESTURNOS e JOIN dbo.TURNOS t ON t.ID_FILIAL=e.ID_FILIAL AND t.ID_TURNOS=e.ID_TURNOS
            WHERE e.ID_FILIAL=17581 AND t.DATATURNO>=@date AND t.DATATURNO<DATEADD(day,1,@date) ORDER BY e.ID_ENCERRANTESTURNOS FOR XML RAW,BINARY BASE64)
            """,c,tx);
        cmd.Parameters.Add("@date",SqlDbType.Date).Value=day.ToDateTime(TimeOnly.MinValue);
        await using var r=await cmd.ExecuteReaderAsync();await r.ReadAsync();return BookWriting.Hash(new[]{r.GetString(0),r.GetString(1)});
    }
}
