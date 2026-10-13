using System.Data;
using Microsoft.Data.SqlClient;

namespace Lmc.Web;

public sealed record DayTank(int ProductId,string Product,short TankId,int? BookId,decimal? Capacity,decimal? LegacyOpening,decimal? LegacyClosing);
public sealed record DayPump(int ProductId,short PumpId,short? TankId,int BookId,decimal? Opening,decimal? Closing,decimal? Calibration,decimal? Sales);
public sealed record ArchivePump(short PumpId,short? TankId,int? ProductId,int Events,string FirstAt,string LastAt,decimal? Opening,decimal? Closing,bool Ambiguous);
public sealed record DayReceipt(int Id,int ProductId,short TankId,short DatabaseId,int? DocumentId,int? Number,string Supplier,
    string? DocumentDate,string? EntryDate,string? OperationalDate,string? ParcelDate,decimal? Volume,bool Canceled,bool Usable);
public sealed record SavedReceipt(int ProductId,int BookId,short TankId,int? Number,decimal? Volume,string? Date);
public sealed record TankReading(string Source,int Id,short TankId,string At,decimal? Volume)
{
    public string? InvalidVolume {get;init;}
    public bool CanUse=>InvalidVolume is null&&Volume is >=0 and <=1000000000m;
}
public sealed record DayProduct(int Id,string Name,IReadOnlyList<string> Alerts,decimal? Receipts,decimal? Sales,bool CanCalculate);
public sealed record LmcDay(short Branch,string Date,IReadOnlyList<Book> Books,IReadOnlyList<DayTank> Tanks,
    IReadOnlyList<DayPump> Pumps,IReadOnlyList<ArchivePump> Archive,IReadOnlyList<DayReceipt> Receipts,
    IReadOnlyList<SavedReceipt> SavedReceipts,IReadOnlyList<TankReading> Readings,IReadOnlyList<DayProduct> Products,IReadOnlyList<string> Alerts);
public sealed record PhysicalInput(short TankId,decimal? PreviousClosing,decimal? Closing);
public sealed record BalanceRequest(short Branch,string Date,int ProductId,IReadOnlyList<PhysicalInput> Tanks);
public sealed record Balance(decimal? Opening,decimal? Receipts,decimal? Sales,decimal? BookStock,decimal? Physical,
    decimal? Difference,decimal? Percent,bool? AboveReference,decimal? ReferenceMin,decimal? ReferenceMax,IReadOnlyList<string> Alerts);

public static class LmcConference
{
    // UNION ALL e planos paralelos podem devolver as mesmas medições em outra ordem.
    // A ordenação cobre todos os campos, inclusive IDs repetidos entre bases legadas,
    // e mantém duplicatas e valores inválidos para a conferência de integridade.
    internal static List<TankReading> OrderReadings(IEnumerable<TankReading> readings)
        =>readings.OrderBy(x=>x.Source,StringComparer.Ordinal).ThenBy(x=>x.Id).ThenBy(x=>x.TankId)
            .ThenBy(x=>x.At,StringComparer.Ordinal).ThenBy(x=>x.Volume).ThenBy(x=>x.InvalidVolume,StringComparer.Ordinal).ToList();
    // Tolerância de representação das fontes real/float; não é o limite de perda/sobra.
    public const decimal NumericTolerance=0.02m;
    public static bool Differs(decimal? a,decimal? b) => a is null || b is null || Math.Abs(a.Value-b.Value)>NumericTolerance;

    public static Balance Calculate(IReadOnlyList<PhysicalInput> inputs,IReadOnlyList<DayTank> tanks,decimal? receipts,decimal? sales,bool canCalculate)
    {
        var alerts=new List<string>();
        if(inputs.Count!=tanks.Count || inputs.Select(t=>t.TankId).Distinct().Count()!=inputs.Count
            || !inputs.Select(t=>t.TankId).Order().SequenceEqual(tanks.Select(t=>t.TankId).Order()))
            throw new ArgumentException("Informe uma leitura por tanque deste combustível.");
        foreach(var input in inputs)
        {
            var tank=tanks.Single(t=>t.TankId==input.TankId);
            if(input.PreviousClosing is <0 or >1000000000m || input.Closing is <0 or >1000000000m)
                throw new ArgumentException("As medições devem ser positivas ou zero, até 1 bilhão de litros.");
            if(tank.Capacity is >0 && (input.PreviousClosing>tank.Capacity || input.Closing>tank.Capacity))
                alerts.Add($"Tanque {tank.TankId}: medição acima da capacidade cadastrada. Confira também a capacidade histórica.");
        }
        decimal? opening=inputs.Count>0 && inputs.All(x=>x.PreviousClosing.HasValue)?inputs.Sum(x=>x.PreviousClosing!.Value):null;
        decimal? closing=inputs.Count>0 && inputs.All(x=>x.Closing.HasValue)?inputs.Sum(x=>x.Closing!.Value):null;
        decimal? book=canCalculate?opening+receipts-sales:null;
        decimal? difference=closing-book;
        // Perdas + ganhos (campo 8) sobre o estoque escritural, base confirmada pelo usuário.
        decimal? percent=book>0?difference/book*100:null;
        bool? above=book>0 && difference.HasValue?Math.Abs(difference.Value)>book.Value*0.006m:null;
        if(opening is null || closing is null) alerts.Add("Informe o fechamento físico anterior e o atual de todos os tanques; campo vazio não é zero.");
        if(!canCalculate) alerts.Add("Resolva as pendências de entradas, livros ou bicos antes de calcular o balanço.");
        if(book<0) alerts.Add("Estoque escritural negativo. Confira abertura, entradas e vendas.");
        if(book==0 && closing.HasValue) alerts.Add("Estoque escritural zero: o percentual não pode ser calculado. Confira a diferença em litros.");
        if(above==true) alerts.Add("Variação acima da referência de 0,6% do estoque escritural. Confira medições, notas, encerrantes e aferições; apure a causa.");
        return new(opening,receipts,sales,book,closing,difference,percent,above,book>0?book*0.994m:null,book>0?book*1.006m:null,alerts);
    }

    public static LmcDay Assess(short branch,string date,List<Book> books,List<DayTank> tanks,List<DayPump> pumps,List<ArchivePump> archive,
        List<DayReceipt> receipts,List<SavedReceipt> saved,List<TankReading> readings)
    {
        var products=new List<DayProduct>();
        var ids=books.Select(x=>x.ProductId).Concat(tanks.Select(x=>x.ProductId)).Concat(receipts.Select(x=>x.ProductId))
            .Concat(archive.Where(x=>x.ProductId.HasValue).Select(x=>x.ProductId!.Value)).Distinct().Order();
        foreach(var id in ids)
        {
            var alerts=new List<string>();
            var productBooks=books.Where(x=>x.ProductId==id).ToArray();
            var ts=tanks.Where(x=>x.ProductId==id).ToArray();
            var ps=pumps.Where(x=>x.ProductId==id).ToArray();
            var rs=receipts.Where(x=>x.ProductId==id).ToArray();
            var validReceipts=rs.Where(x=>!x.Canceled).ToArray();
            bool canCalculate=true;
            if(productBooks.Length!=1) {alerts.Add(productBooks.Length==0?"Sem LMC salvo nesta data. Encerrantes disponíveis para conferência; vendas e aferições ainda precisam ser validadas.":"Mais de um LMC para este combustível nesta data. Confira a duplicidade.");canCalculate=false;}
            if(ts.Length==0 || ts.GroupBy(x=>x.TankId).Any(g=>g.Count()>1)) {alerts.Add("Tanques ausentes ou repetidos no LMC.");canCalculate=false;}
            if(ts.Any(x=>x.BookId is null)) {alerts.Add("Há tanque do cadastro atual sem registro neste LMC. Confira o vínculo histórico.");canCalculate=false;}
            if(ts.Any(x=>x.LegacyOpening<0 || x.LegacyClosing<0)) alerts.Add("Há estoque negativo nos valores já gravados no LMC.");
            if(validReceipts.Any(x=>!x.Usable || !ts.Any(t=>t.TankId==x.TankId))) {alerts.Add("Há entrada com data, turno, vínculo, tanque ou volume pendente. Revise a nota.");canCalculate=false;}
            decimal? totalReceipts=validReceipts.All(x=>x.Usable)?validReceipts.Sum(x=>x.Volume!.Value):null;
            if(ps.Length==0 || ps.GroupBy(x=>x.PumpId).Any(g=>g.Count()>1)) {alerts.Add("Bicos ausentes ou repetidos no LMC.");canCalculate=false;}
            foreach(var p in ps)
            {
                var a=archive.SingleOrDefault(x=>x.PumpId==p.PumpId);
                if(p.TankId is null || !ts.Any(t=>t.TankId==p.TankId)) {alerts.Add($"Bico {p.PumpId}: tanque não localizado no livro.");canCalculate=false;}
                if(p.Opening is null || p.Closing is null || p.Calibration is null || p.Calibration<0 || p.Closing<p.Opening
                    || p.Sales is null || p.Sales<0 || Differs(p.Closing-p.Opening-p.Calibration,p.Sales))
                {alerts.Add($"Bico {p.PumpId}: conferir encerrantes, aferição e vendas gravados.");canCalculate=false;}
                if(a is null) {alerts.Add($"Bico {p.PumpId}: sem registro da automação no dia. Não foi assumida venda zero.");canCalculate=false;}
                else if(a.Ambiguous || Differs(a.Opening,p.Opening) || Differs(a.Closing,p.Closing))
                {alerts.Add($"Bico {p.PumpId}: encerrantes da automação divergem ou têm horário ambíguo.");canCalculate=false;}
            }
            var unlinked=archive.Where(x=>x.ProductId==id && !pumps.Any(p=>p.PumpId==x.PumpId)).ToArray();
            if(unlinked.Length>0) {alerts.Add($"{unlinked.Length} bico(s) com automação e sem vínculo em um LMC do dia.");canCalculate=false;}
            decimal? totalSales=ps.Length>0 && ps.All(x=>x.Sales.HasValue)?ps.Sum(x=>x.Sales!.Value):null;
            // Compara composição por tanque e número sem afirmar identidade documental: o livro não guarda a chave completa.
            var existing=saved.Where(x=>x.ProductId==id).ToArray();
            var keys=existing.Select(x=>(x.TankId,x.Number)).Concat(validReceipts.Select(x=>(x.TankId,x.Number))).Distinct();
            if(productBooks.Length>0 && (existing.Any(x=>x.Volume is null) || keys.Any(k=>Differs(
                existing.Where(x=>(x.TankId,x.Number)==k).Sum(x=>x.Volume??0),
                validReceipts.Where(x=>(x.TankId,x.Number)==k).Sum(x=>x.Volume??0)))))
                alerts.Add("As entradas atuais diferem dos recebimentos gravados no LMC. O livro precisa de revisão; o cálculo usa as entradas atuais.");
            products.Add(new(id,books.FirstOrDefault(x=>x.ProductId==id)?.Product??ts.FirstOrDefault()?.Product??$"Produto {id}",alerts,totalReceipts,totalSales,canCalculate));
        }
        var dayAlerts=new List<string>();
        if(archive.Any(x=>x.ProductId is null && !pumps.Any(p=>p.PumpId==x.PumpId)))
        {
            dayAlerts.Add("Há bico da automação sem produto/tanque identificado. Confira o cadastro antes de calcular o dia.");
            products=products.Select(x=>x with {CanCalculate=false}).ToList();
        }
        return new(branch,date,books,tanks,pumps,archive,receipts,saved,readings,products,dayAlerts);
    }
}

public sealed partial class Repository
{
    public async Task<LmcDay> Day(short branch,DateOnly date,CancellationToken ct)
    {
        await using var c=await Open(ct);
        using var tx=c.BeginTransaction(IsolationLevel.Serializable);
        var result=await ReadDay(c,tx,branch,date,ct);
        tx.Commit();
        return result;
    }

    private static async Task<LmcDay> ReadDay(SqlConnection c,SqlTransaction tx,short branch,DateOnly date,CancellationToken ct)
    {
        using var cmd=Command(c,Resource("LmcDay.sql"),branch,transaction:tx);
        cmd.Parameters.Add("@date",SqlDbType.Date).Value=date.ToDateTime(TimeOnly.MinValue);
        var books=new List<Book>();var tanks=new List<DayTank>();var pumps=new List<DayPump>();var archive=new List<ArchivePump>();
        var receipts=new List<DayReceipt>();var saved=new List<SavedReceipt>();var readings=new List<TankReading>();
        await using(var r=await cmd.ExecuteReaderAsync(ct))
        {
            while(await r.ReadAsync(ct)) books.Add(new(Value<int>(r,"Id"),Value<int>(r,"ProductId"),Value<string>(r,"Product"),Value<string>(r,"Date"),Optional<int>(r,"Number")));
            await r.NextResultAsync(ct);
            while(await r.ReadAsync(ct)) tanks.Add(new(Value<int>(r,"ProductId"),Value<string>(r,"Product"),Value<short>(r,"TankId"),Optional<int>(r,"BookId"),Optional<decimal>(r,"Capacity"),Optional<decimal>(r,"LegacyOpening"),Optional<decimal>(r,"LegacyClosing")));
            await r.NextResultAsync(ct);
            while(await r.ReadAsync(ct)) pumps.Add(new(Value<int>(r,"ProductId"),Value<short>(r,"PumpId"),Optional<short>(r,"TankId"),Value<int>(r,"BookId"),Optional<decimal>(r,"Opening"),Optional<decimal>(r,"Closing"),Optional<decimal>(r,"Calibration"),Optional<decimal>(r,"Sales")));
            await r.NextResultAsync(ct);
            while(await r.ReadAsync(ct)) archive.Add(new(Value<short>(r,"PumpId"),Optional<short>(r,"TankId"),Optional<int>(r,"ProductId"),Value<int>(r,"Events"),Value<string>(r,"FirstAt"),Value<string>(r,"LastAt"),Optional<decimal>(r,"Opening"),Optional<decimal>(r,"Closing"),Value<bool>(r,"Ambiguous")));
            await r.NextResultAsync(ct);
            while(await r.ReadAsync(ct)) receipts.Add(new(Value<int>(r,"Id"),Value<int>(r,"ProductId"),Value<short>(r,"TankId"),Value<short>(r,"DatabaseId"),Optional<int>(r,"DocumentId"),Optional<int>(r,"Number"),Value<string>(r,"Supplier"),Text(r,"DocumentDate"),Text(r,"EntryDate"),Text(r,"OperationalDate"),Text(r,"ParcelDate"),Optional<decimal>(r,"Volume"),Value<bool>(r,"Canceled"),Value<bool>(r,"Usable")));
            await r.NextResultAsync(ct);
            while(await r.ReadAsync(ct)) saved.Add(new(Value<int>(r,"ProductId"),Value<int>(r,"BookId"),Value<short>(r,"TankId"),Optional<int>(r,"Number"),Optional<decimal>(r,"Volume"),Text(r,"Date")));
            await r.NextResultAsync(ct);
            while(await r.ReadAsync(ct)) readings.Add(new(Value<string>(r,"Source"),Value<int>(r,"Id"),Value<short>(r,"TankId"),Value<string>(r,"At"),Optional<decimal>(r,"Volume"))
                {InvalidVolume=Text(r,"InvalidVolume")});
        }
        return LmcConference.Assess(branch,date.ToString("yyyy-MM-dd"),books,tanks,pumps,archive,receipts,saved,LmcConference.OrderReadings(readings));
    }
}
