using System.Data;
using System.Reflection;
using System.Text.Json;
using Lmc.Web;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

internal static class BookSourceChecks
{
    public static async Task Rollback(short branch,DateOnly date,int product,string? inputFile,Action<bool,string> check)
    {
        const string connection="Server=localhost;Database=ATXDADOS;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Application Name=LMC-Sources-Rollback";
        var repo=new Repository(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:AtxDados",connection}}).Build());
        await using var c=new SqlConnection(connection);await c.OpenAsync();
        var original=await BookWriteChecks.Snapshot(c,null);
        using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
        {
            try
            {
                var initial=await ReadContext(c,tx,branch,date,product);
                var p=initial.Preparation;
                for(var i=0;i<4;i++)
                {
                    var repeated=await Repository.PrepareBookInTransaction(c,tx,branch,date,product,default);
                    check(repeated.Version==p.Version,$"consulta repetida {i+1} conserva a versão das mesmas fontes");
                }
                var request=inputFile is null?new BookSaveRequest(branch,date.ToString("yyyy-MM-dd"),product,p.Version,p.SavedMeasurements!,p.Observation,p.MeasurementMethod??"manual",Guid.NewGuid(),p.Pumps.Select(x=>new BookPumpInput(x.PumpId,x.Closing,x.Calibration,x.Cutoff is null?null:"automation")).ToArray()):
                    JsonSerializer.Deserialize<BookSaveRequest>(await File.ReadAllTextAsync(inputFile),new JsonSerializerOptions {PropertyNameCaseInsensitive=true})!;
                request=request with {Branch=branch,Date=date.ToString("yyyy-MM-dd"),ProductId=product,Version=p.Version,OperationId=Guid.NewGuid()};
                var before=initial.Day;
                try
                {
                    var saved=await repo.SaveBookInTransaction(c,tx,request,default);
                    check(saved.BookId>0,"tentativa com as medições da tela conclui dentro da transação");
                }
                catch(CorrectionException ex)
                {
                    Console.WriteLine("DIAGNÓSTICO: "+ex.Message);
                    var after=await ReadDay(c,tx,branch,date);
                    Compare("Automação",before.Archive,after.Archive);
                    Compare("Entradas",before.Receipts,after.Receipts);
                    Compare("Medições",before.Readings,after.Readings);
                    var final=await ReadContext(c,tx,branch,date,product);
                    Compare("Preparação",new[]{initial.Preparation.Version},new[]{final.Preparation.Version});
                    throw;
                }
            }
            finally {await tx.RollbackAsync(CancellationToken.None);}
        }
        check(original==await BookWriteChecks.Snapshot(c,null),"ensaio desfeito sem alterar os registros originais");
    }
    private static Task<LmcDay> ReadDay(SqlConnection c,SqlTransaction tx,short branch,DateOnly date)
        =>(Task<LmcDay>)typeof(Repository).GetMethod("ReadDay",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[c,tx,branch,date,CancellationToken.None])!;
    private static async Task<(BookPreparation Preparation,LmcDay Day)> ReadContext(SqlConnection c,SqlTransaction tx,short branch,DateOnly date,int product)
    {
        var task=(Task)typeof(Repository).GetMethod("ReadWriteContext",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[c,tx,branch,date,product,CancellationToken.None])!;
        await task;var result=task.GetType().GetProperty("Result")!.GetValue(task)!;
        return ((BookPreparation)result.GetType().GetProperty("Preparation")!.GetValue(result)!, (LmcDay)result.GetType().GetProperty("Day")!.GetValue(result)!);
    }
    private static void Compare<T>(string source,IReadOnlyList<T> before,IReadOnlyList<T> after)
    {
        var a=before.Select(x=>JsonSerializer.Serialize(x)).ToArray();var b=after.Select(x=>JsonSerializer.Serialize(x)).ToArray();
        Console.WriteLine($"DIAGNÓSTICO {source}: antes={a.Length}; depois={b.Length}; mesma ordem={a.SequenceEqual(b)}; mesmo conteúdo={a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal))}");
        foreach(var line in a.Except(b).Take(5))Console.WriteLine("ANTES: "+line);
        foreach(var line in b.Except(a).Take(5))Console.WriteLine("DEPOIS: "+line);
    }
}
