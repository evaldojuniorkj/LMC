using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Lmc.Web;

public sealed record BookBatchSelection(short Branch,IReadOnlyList<int> BookIds);
public sealed record BookBatchItem(int BookId,string Version);
public sealed record BookBatchDeleteRequest(short Branch,IReadOnlyList<BookBatchItem> Books,Guid OperationId);
public sealed record BookBatchDeletePreparation(short Branch,IReadOnlyList<BookDeletePreparation> Books,IReadOnlyList<Book> FollowingBooks,IReadOnlyList<string> Blockers);
public sealed record BookBatchDeleted(short Branch,Guid OperationId,bool AlreadyDeleted,IReadOnlyList<BookDeleted> Books,IReadOnlyList<Book> FollowingBooks,string? LastBookDate);

public static class BookBatchDeletion
{
    public static void Validate(short branch,IReadOnlyList<int>? ids)
    {
        if(branch<=0||ids is null||ids.Count is <1 or >200||ids.Any(x=>x<=0)||ids.Distinct().Count()!=ids.Count)
            throw new CorrectionException(400,"Selecione de 1 a 200 LMCs diferentes da mesma filial.");
    }
    public static string Fingerprint(BookBatchDeleteRequest input)=>BookWriting.Hash(new {input.Branch,Books=input.Books.OrderBy(x=>x.BookId)});
    public static Book[] Following(IEnumerable<BookDeletePreparation> books)
    {
        var chosen=books.ToArray();var ids=chosen.Select(x=>x.BookId).ToHashSet();
        return chosen.SelectMany(x=>x.FollowingBooks).Where(x=>!ids.Contains(x.Id)).DistinctBy(x=>x.Id).OrderBy(x=>x.Date).ThenBy(x=>x.ProductId).ToArray();
    }
}

public sealed partial class Repository
{
    public async Task<BookBatchDeletePreparation> PrepareBookBatchDeletion(BookBatchSelection input,CancellationToken ct)
    {
        BookBatchDeletion.Validate(input.Branch,input.BookIds);
        await using var c=await Open(ct);using var tx=c.BeginTransaction(IsolationLevel.Serializable);
        var result=await PrepareBookBatchDeletionInTransaction(c,tx,input,ct);await tx.CommitAsync(ct);return result;
    }
    internal static async Task<BookBatchDeletePreparation> PrepareBookBatchDeletionInTransaction(SqlConnection c,SqlTransaction tx,BookBatchSelection input,CancellationToken ct)
    {
        BookBatchDeletion.Validate(input.Branch,input.BookIds);
        var books=new List<BookDeletePreparation>();
        foreach(var id in input.BookIds.Order())books.Add((await ReadBookDeletion(c,tx,input.Branch,id,ct)).Preview);
        return new(input.Branch,books.OrderBy(x=>x.Date).ThenBy(x=>x.ProductId).ThenBy(x=>x.BookId).ToArray(),BookBatchDeletion.Following(books),
            books.SelectMany(x=>x.Blockers.Select(b=>$"{x.Date} · {x.Product}: {b}")).Distinct().ToArray());
    }
    public async Task<BookBatchDeleted> DeleteBookBatch(BookBatchDeleteRequest input,CancellationToken ct)
    {
        await using var c=await Open(ct);using var tx=c.BeginTransaction(IsolationLevel.Serializable);
        try{var result=await DeleteBookBatchInTransaction(c,tx,input,ct);await tx.CommitAsync(ct);return result;}
        catch{try{await tx.RollbackAsync(CancellationToken.None);}catch(Exception ex) when(ex is SqlException or InvalidOperationException){}throw;}
    }
    internal async Task<BookBatchDeleted> DeleteBookBatchInTransaction(SqlConnection c,SqlTransaction tx,BookBatchDeleteRequest input,CancellationToken ct)
    {
        if(input.Books is null||input.Books.Any(x=>x is null))throw new CorrectionException(400,"Confira os livros selecionados.");
        BookBatchDeletion.Validate(input.Branch,input.Books.Select(x=>x.BookId).ToArray());
        foreach(var b in input.Books)BookDeletion.Validate(new(input.Branch,b.BookId,b.Version,input.OperationId));
        using(var cmd=Command(c,"""
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=N'LMCWEB-WRITE',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000;
            SELECT @result;
            """,transaction:tx))
            if(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))<0)throw new CorrectionException(409,"Outro LMC está sendo alterado. Tente novamente após concluir essa operação.");
        var fingerprint=BookBatchDeletion.Fingerprint(input);
        var previous=await ReadBookDeletionAudit(c,tx,input.OperationId,ct);
        if(previous is not null)
        {
            if(previous.Batch is null||previous.Fingerprint!=fingerprint||previous.Batch.Branch!=input.Branch)
                throw new CorrectionException(409,"O identificador do lote já foi usado com outro conteúdo.");
            return previous.Batch with {AlreadyDeleted=true,LastBookDate=await LastBookDate(c,tx,input.Branch,ct)};
        }
        var contexts=new List<(BookDeletePreparation Preview,string Rows)>();
        foreach(var b in input.Books.OrderBy(x=>x.BookId))
        {
            var context=await ReadBookDeletion(c,tx,input.Branch,b.BookId,ct);
            if(context.Preview.Version!=b.Version)throw new CorrectionException(409,$"O LMC {b.BookId} mudou. Atualize a confirmação do lote; nenhum livro foi excluído.");
            if(context.Preview.Blockers.Count>0)throw new CorrectionException(422,$"LMC {b.BookId}: {string.Join(" ",context.Preview.Blockers)} Nenhum livro do lote foi excluído.");
            contexts.Add(context);
        }
        var protectedBefore=await BookBatchProtected(c,tx,input.Branch,input.Books.Select(x=>x.BookId).ToArray(),ct);
        var fiscalBefore=await BookDeleteFiscalState(c,tx,ct);
        var following=BookBatchDeletion.Following(contexts.Select(x=>x.Preview));
        var receipts=contexts.Select((x,i)=>new BookDeleted(input.Branch,x.Preview.BookId,x.Preview.ProductId,x.Preview.Product,x.Preview.Date,x.Preview.Number,
            i==0?input.OperationId:Guid.NewGuid(),false,following.Where(b=>b.ProductId==x.Preview.ProductId&&string.CompareOrdinal(b.Date,x.Preview.Date)>0).ToArray(),null)).ToArray();
        var result=new BookBatchDeleted(input.Branch,input.OperationId,false,receipts,following,null);
        var audits=contexts.Select((x,i)=>new BookDeletionAudit(i==0?fingerprint:BookDeletion.Fingerprint(new(input.Branch,x.Preview.BookId,x.Preview.Version,receipts[i].OperationId)),
            DateTimeOffset.Now.ToString("O",CultureInfo.InvariantCulture),CurrentActor,x.Rows,receipts[i],i==0?result:null)).ToArray();
        foreach(var audit in audits)_=BookDeletion.Encode(audit);
        foreach(var b in input.Books)await RemoveBookRows(c,tx,input.Branch,b.BookId,ct);
        var last=await LastBookDate(c,tx,input.Branch,ct);
        receipts=receipts.Select(x=>x with {LastBookDate=last}).ToArray();result=result with {Books=receipts,LastBookDate=last};
        for(var i=0;i<audits.Length;i++)await WriteBookDeletionAudit(c,tx,audits[i] with {Receipt=receipts[i],Batch=i==0?result:null},ct);
        if(protectedBefore!=await BookBatchProtected(c,tx,input.Branch,input.Books.Select(x=>x.BookId).ToArray(),ct)||fiscalBefore!=await BookDeleteFiscalState(c,tx,ct))
            throw new CorrectionException(422,"Uma rotina do banco alterou dados fora do lote. Toda a exclusão foi desfeita.");
        return result;
    }
    private static async Task<string> BookBatchProtected(SqlConnection c,SqlTransaction tx,short branch,IReadOnlyList<int> ids,CancellationToken ct)
    {
        var parameters=ids.Select((_,i)=>$"@book{i}").ToArray();
        var sql=Resource("LmcProtected.sql").Replace("ID_LMC<>@id",$"ID_LMC NOT IN ({string.Join(',',parameters)})",StringComparison.Ordinal);
        using var cmd=Command(c,sql,branch,transaction:tx);
        for(var i=0;i<ids.Count;i++)cmd.Parameters.Add(parameters[i],SqlDbType.Int).Value=ids[i];
        await using var r=await cmd.ExecuteReaderAsync(ct);await r.ReadAsync(ct);
        return BookWriting.Hash(Enumerable.Range(0,r.FieldCount).Select(i=>r.IsDBNull(i)?"":r.GetString(i)).ToArray());
    }
}
