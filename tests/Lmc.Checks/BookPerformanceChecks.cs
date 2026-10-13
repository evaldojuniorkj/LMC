using System.Data;
using System.Diagnostics;
using Lmc.Web;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

internal static class BookPerformanceChecks
{
    // Opt-in, somente na cópia local de teste. Não há caminho de commit.
    public static async Task Rollback(short branch,DateOnly date,int product,Action<bool,string> check)
    {
        const string connection="Server=localhost;Database=ATXDADOS;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=5;Application Name=LMC-Performance-Rollback";
        var repo=new Repository(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:AtxDados",connection}}).Build());
        await using var c=new SqlConnection(connection);await c.OpenAsync();
        var before=await BookWriteChecks.Snapshot(c,null);
        using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
        {
            try
            {
                var watch=Stopwatch.StartNew();
                var p=await Repository.PrepareBookInTransaction(c,tx,branch,date,product,default);
                Console.WriteLine($"TEMPO: preparar = {watch.ElapsedMilliseconds} ms");
                check(p.Blockers.Count==0,"fontes aptas para ensaio: "+string.Join("; ",p.Blockers));
                var tanks=p.Tanks.Select(t=>
                {
                    var opening=t.PreviousClosing??t.Capacity/2??throw new Exception("Ensaio exige abertura ou capacidade existente.");
                    var closing=Math.Max(0,opening+p.Documents.Where(x=>x.TankId==t.TankId).Sum(x=>x.Volume)-p.Pumps.Where(x=>x.TankId==t.TankId).Sum(x=>x.Sales));
                    return new PhysicalInput(t.TankId,opening,decimal.Floor(closing));
                }).ToArray();
                var input=new BookSaveRequest(branch,date.ToString("yyyy-MM-dd"),product,p.Version,tanks,
                    "ENSAIO DE DESEMPENHO COM ROLLBACK - valores simulados, sem persistência.","manual",Guid.NewGuid(),
                    p.Pumps.Select(x=>new BookPumpInput(x.PumpId,x.Closing,x.Calibration,x.Cutoff is null?null:"automation")).ToArray());
                var protectedBefore=await Repository.BookProtected(c,tx,branch,p.BookId??0,default);
                var legacy=await Repository.ReadBookProtection(c,tx,branch,p.BookId??0,false,default);
                check(protectedBefore==legacy,"resumos no SQL e leitura integral preservam exatamente as mesmas tabelas e campos");
                // Alteração de mesmo tamanho depois do byte 8.000: não basta contar linhas ou truncar o texto.
                tx.Save("protection_probe");
                try
                {
                    using var probe=new SqlCommand("""
                        SELECT TOP(1) ID_LMC FROM dbo.LMC WHERE ID_FILIAL=@branch AND ID_LMC<>@id ORDER BY ID_LMC;
                        """,c,tx);
                    probe.Parameters.Add("@branch",SqlDbType.SmallInt).Value=branch;
                    probe.Parameters.Add("@id",SqlDbType.Int).Value=p.BookId??0;
                    var other=Convert.ToInt32(await probe.ExecuteScalarAsync());
                    var excludedBefore=await Repository.BookProtected(c,tx,branch,other,default);
                    probe.CommandText="UPDATE dbo.LMC SET OBS=@text WHERE ID_FILIAL=@branch AND ID_LMC=@id";
                    probe.Parameters["@id"].Value=other;
                    probe.Parameters.Add("@text",SqlDbType.VarChar,-1).Value=new string('a',10000)+"A";
                    await probe.ExecuteNonQueryAsync();
                    var longText=await Repository.BookProtected(c,tx,branch,p.BookId??0,default);
                    check(longText!=protectedBefore,"alteração em outro livro continua detectada");
                    probe.Parameters["@text"].Value=new string('a',10000)+"B";
                    await probe.ExecuteNonQueryAsync();
                    check(longText!=await Repository.BookProtected(c,tx,branch,p.BookId??0,default),"mudança no fim de texto longo não escapa da conferência");
                    check(excludedBefore==await Repository.BookProtected(c,tx,branch,other,default),"somente o livro em edição fica excluído da proteção externa");
                }
                finally {tx.Rollback("protection_probe");}
                // Mede a mesma proteção utilizada no salvamento, sem expor os registros.
                watch.Restart();
                await Repository.BookProtected(c,tx,branch,p.BookId??0,default);
                Console.WriteLine($"TEMPO: proteção = {watch.ElapsedMilliseconds} ms");
                watch.Restart();
                var saved=await repo.SaveBookInTransaction(c,tx,input,default);
                Console.WriteLine($"TEMPO: salvar ({saved.Mode}) = {watch.ElapsedMilliseconds} ms");
                check(!saved.AlreadyApplied,"salvamento completo validado dentro da transação");
                watch.Restart();
                try {await repo.SaveBookInTransaction(c,tx,input,default);throw new Exception("Repetição com versão antiga aceita");}
                catch(CorrectionException ex){check(ex.Status==409,"repetição exige atualizar o livro sem duplicação");}
                Console.WriteLine($"TEMPO: repetir operação = {watch.ElapsedMilliseconds} ms");
                try {await repo.SaveBookInTransaction(c,tx,input with {Observation="Outro conteúdo"},default);throw new Exception("UUID com outro conteúdo foi aceito.");}
                catch(CorrectionException ex){check(ex.Status==409,"versão antiga com outro conteúdo continua rejeitada");}
                try {await repo.SaveBookInTransaction(c,tx,input with {OperationId=Guid.NewGuid()},default);throw new Exception("Versão antiga foi aceita.");}
                catch(CorrectionException ex){check(ex.Status==409,"versão antiga continua rejeitada");}
                var reopened=await Repository.PrepareBookInTransaction(c,tx,branch,date,product,default);
                check(reopened.SavedMeasurements is not null&&reopened.SavedMeasurements.OrderBy(x=>x.TankId).SequenceEqual(tanks.OrderBy(x=>x.TankId)),"reabertura conserva abertura e fechamento informados");
                watch.Restart();
                var rebuilt=await repo.SaveBookInTransaction(c,tx,input with {Version=reopened.Version,OperationId=Guid.NewGuid(),
                    Pumps=reopened.Pumps.Select(x=>new BookPumpInput(x.PumpId,x.Closing,x.Calibration)).ToArray()},default);
                Console.WriteLine($"TEMPO: refazer = {watch.ElapsedMilliseconds} ms");
                check(rebuilt.Mode=="refeito"&&rebuilt.BookId==saved.BookId,"reconstrução preserva o mesmo livro");
            }
            finally {await tx.RollbackAsync(CancellationToken.None);}
        }
        check(before==await BookWriteChecks.Snapshot(c,null),"rollback conserva livros, fontes e histórico originais");
    }
}
