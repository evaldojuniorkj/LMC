using System.Data;
using Microsoft.Data.SqlClient;

namespace Lmc.Web;

public sealed record BookMissingPeriod(string From,string Until,int Days);
public sealed record BookPendingProduct(int ProductId,string Product,string LastBookDate,IReadOnlyList<BookMissingPeriod> MissingPeriods)
{
    public IReadOnlyList<BookInconsistency> Inconsistencies {get;init;}=[];
    public IReadOnlyList<BookTankReview> TankReviews {get;init;}=[];
}
public sealed record BookContinuityHeader(int Id,int ProductId,string Product,DateOnly Date);
public sealed record BookContinuityReading(int BookId,string Kind,short EquipmentId,decimal? Opening,decimal? Closing)
{
    public decimal? Received {get;init;}=0;
    public decimal? Sales {get;init;}=0;
}
public sealed record BookTankBalance(short TankId,decimal Opening,decimal Received,decimal Sales,decimal BookStock,decimal Closing,decimal Difference,decimal? Percent,bool AboveReference);
public sealed record BookTankReview(int BookId,string Date,bool PossibleMisallocation,bool TotalWithinReference,IReadOnlyList<BookTankBalance> Tanks);
public sealed record BookInconsistency(int BookId,string Date,string Kind,short? EquipmentId,string Code,
    string? PreviousDate,decimal? PreviousClosing,decimal? Opening,decimal? Closing)
{
    public decimal? Difference=>Code=="opening_mismatch"?Opening-PreviousClosing:null;
}

public static class BookContinuity
{
    public static IReadOnlyList<BookPendingProduct> Inspect(IReadOnlyList<BookContinuityHeader> books,IReadOnlyList<BookContinuityReading> readings)
    {
        var byBook=readings.ToLookup(x=>x.BookId);
        return books.GroupBy(x=>x.ProductId).OrderBy(g=>g.Key).Select(product=>
        {
            var days=product.GroupBy(x=>x.Date).ToDictionary(g=>g.Key,g=>g.ToArray());
            var issues=new List<BookInconsistency>();
            var tankReviews=new List<BookTankReview>();
            foreach(var book in product.OrderBy(x=>x.Date).ThenBy(x=>x.Id))
            {
                var date=book.Date.ToString("yyyy-MM-dd");
                if(days[book.Date].Length!=1)
                {
                    issues.Add(new(book.Id,date,"book",null,"duplicate_book",null,null,null,null));
                    continue;
                }
                var previousDay=book.Date.AddDays(-1);
                var hasPrevious=days.TryGetValue(previousDay,out var previousBooks);
                var previous=hasPrevious&&previousBooks!.Length==1
                    ?byBook[previousBooks[0].Id].ToLookup(x=>(x.Kind,x.EquipmentId)):null;
                foreach(var group in byBook[book.Id].GroupBy(x=>(x.Kind,x.EquipmentId)))
                {
                    var current=group.First();
                    void Add(string code,decimal? previousClosing=null)=>issues.Add(new(book.Id,date,current.Kind,current.EquipmentId,code,
                        hasPrevious?previousDay.ToString("yyyy-MM-dd"):null,previousClosing,current.Opening,current.Closing));
                    if(group.Count()!=1){Add("duplicate_reading");continue;}
                    if(current.Opening is null||current.Closing is null)Add("missing_value");
                    if(current.Kind=="tank"&&(current.Received is null or <0||current.Sales is null or <0))Add("incomplete_balance");
                    if(current.Kind=="pump"&&current.Closing<current.Opening)Add("counter_order");
                    if(hasPrevious&&previousBooks!.Length!=1){Add("ambiguous_previous");continue;}
                    var prior=previous?[group.Key].ToArray()??[];
                    if(prior.Length>1){Add("ambiguous_previous");continue;}
                    // Ausência de livro/equipamento anterior não inventa uma referência zero.
                    if(prior.Length==0)continue;
                    if(prior[0].Closing is null){Add("missing_previous_closing");continue;}
                    if(current.Opening.HasValue&&current.Opening!=prior[0].Closing)Add("opening_mismatch",prior[0].Closing);
                }
                var tanks=byBook[book.Id].Where(x=>x.Kind=="tank").ToArray();
                // O saldo usa somente entradas e vendas gravadas neste livro e neste tanque.
                // Valores ausentes/repetidos não podem parecer um balanço confirmado.
                if(tanks.Length>0&&tanks.Select(x=>x.EquipmentId).Distinct().Count()==tanks.Length
                    &&tanks.All(x=>x.Opening>=0&&x.Closing>=0&&x.Received>=0&&x.Sales>=0))
                {
                    var balances=tanks.Select(x=>
                    {
                        var stock=x.Opening!.Value+x.Received!.Value-x.Sales!.Value;
                        var delta=x.Closing!.Value-stock;
                        return new BookTankBalance(x.EquipmentId,x.Opening.Value,x.Received.Value,x.Sales.Value,stock,x.Closing.Value,delta,
                            stock>0?delta/stock*100:null,OutsideReference(delta,stock));
                    }).OrderBy(x=>x.TankId).ToArray();
                    if(balances.Any(x=>x.AboveReference))
                    {
                        var opposed=balances.Any(x=>x.AboveReference&&x.Difference<0)&&balances.Any(x=>x.AboveReference&&x.Difference>0);
                        tankReviews.Add(new(book.Id,date,opposed&&balances.Sum(x=>x.Received)>0,
                            !OutsideReference(balances.Sum(x=>x.Difference),balances.Sum(x=>x.BookStock)),balances));
                    }
                }
            }
            var last=product.Max(x=>x.Date);
            return new BookPendingProduct(product.Key,product.First().Product,last.ToString("yyyy-MM-dd"),MissingPeriods(product.Select(x=>x.Date),last))
                {Inconsistencies=issues,TankReviews=tankReviews};
        }).ToArray();
    }
    // Mesma referência de 0,6% do estoque escritural já usada na conferência do LMC.
    private static bool OutsideReference(decimal difference,decimal bookStock)=>bookStock>0?Math.Abs(difference)>bookStock*.006m:difference!=0;

    // O histórico começa no primeiro livro disponível; não presume dias anteriores a ele.
    public static IReadOnlyList<BookMissingPeriod> MissingPeriods(IEnumerable<DateOnly> savedDates,DateOnly before)
    {
        var dates=savedDates.Where(d=>d<before).Distinct().Order().ToArray();
        var missing=new List<BookMissingPeriod>();
        for(var i=0;i<dates.Length;i++)
        {
            var next=i+1<dates.Length?dates[i+1]:before;
            var days=next.DayNumber-dates[i].DayNumber-1;
            if(days>0)missing.Add(new(dates[i].AddDays(1).ToString("yyyy-MM-dd"),next.AddDays(-1).ToString("yyyy-MM-dd"),days));
        }
        return missing;
    }
}

public sealed partial class Repository
{
    public async Task<IReadOnlyList<BookPendingProduct>> PendingBooks(short branch,CancellationToken ct)
    {
        if(branch<=0)throw new CorrectionException(400,"Escolha uma filial válida.");
        await using var c=await Open(ct);
        using var tx=c.BeginTransaction(IsolationLevel.RepeatableRead);
        var result=await ReadPendingBooks(c,tx,branch,ct);
        await tx.CommitAsync(ct);
        return result;
    }
    internal static async Task<IReadOnlyList<BookPendingProduct>> ReadPendingBooks(SqlConnection c,SqlTransaction tx,short branch,CancellationToken ct)
    {
        using var cmd=Command(c,Resource("LmcPending.sql"),branch,transaction:tx);
        var books=new List<BookContinuityHeader>();
        var readings=new List<BookContinuityReading>();
        await using(var r=await cmd.ExecuteReaderAsync(ct))
        {
            while(await r.ReadAsync(ct))books.Add(new(r.GetInt32(0),r.GetInt32(1),r.GetString(2),DateOnly.FromDateTime(r.GetDateTime(3))));
            await r.NextResultAsync(ct);
            while(await r.ReadAsync(ct))readings.Add(new(r.GetInt32(0),r.GetString(1),r.GetInt16(2),r.IsDBNull(3)?null:r.GetDecimal(3),r.IsDBNull(4)?null:r.GetDecimal(4))
                {Received=r.IsDBNull(5)?null:r.GetDecimal(5),Sales=r.IsDBNull(6)?null:r.GetDecimal(6)});
        }
        return BookContinuity.Inspect(books,readings);
    }
    private static async Task<IReadOnlyList<BookMissingPeriod>> ReadBookMissingPeriods(SqlConnection c,SqlTransaction tx,short branch,int product,DateOnly before,CancellationToken ct)
    {
        using var cmd=Command(c,"""
            SELECT DISTINCT CONVERT(date,DTACONTA)
            FROM dbo.LMC
            WHERE ID_FILIAL=@branch AND ID_PRODUTOS=@product AND DTACONTA<@before
            ORDER BY 1;
            """,branch,transaction:tx);
        cmd.Parameters.Add("@product",SqlDbType.Int).Value=product;
        cmd.Parameters.Add("@before",SqlDbType.Date).Value=before.ToDateTime(TimeOnly.MinValue);
        var dates=new List<DateOnly>();
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))dates.Add(DateOnly.FromDateTime(r.GetDateTime(0)));
        return BookContinuity.MissingPeriods(dates,before);
    }
}
