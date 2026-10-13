using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Lmc.Web;

public sealed record BookDeletePreparation(short Branch,int BookId,int ProductId,string Product,string Date,int? Number,
    int TankCount,int PumpCount,int ReceiptCount,string Version,IReadOnlyList<Book> FollowingBooks,IReadOnlyList<string> Blockers);
public sealed record BookDeleteRequest(short Branch,int BookId,string Version,Guid OperationId);
public sealed record BookDeleted(short Branch,int BookId,int ProductId,string Product,string Date,int? Number,Guid OperationId,
    bool AlreadyDeleted,IReadOnlyList<Book> FollowingBooks,string? LastBookDate);
public sealed record BookDeletionAudit(string Fingerprint,string AppliedAt,string Actor,string BeforeRows,BookDeleted Receipt,BookBatchDeleted? Batch=null);

public static class BookDeletion
{
    public const string Prefix="LMCDEL1|";
    public static void Validate(BookDeleteRequest input)
    {
        if(input.Branch<=0 || input.BookId<=0 || input.OperationId==Guid.Empty || input.Version?.Length!=64 || !input.Version.All(Uri.IsHexDigit))
            throw new CorrectionException(400,"Confira o livro e atualize a confirmação da exclusão.");
    }
    public static string Fingerprint(BookDeleteRequest input)=>BookWriting.Hash(new {input.Branch,input.BookId,input.Version});
    public static string[] Encode(BookDeletionAudit audit)
    {
        var data=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(audit));
        var count=(data.Length+179)/180;
        if(count>9999)throw new CorrectionException(422,"O histórico deste livro é muito grande para a exclusão pela interface. O livro foi preservado.");
        return Enumerable.Range(0,count).Select(i=>$"{Prefix}{audit.Receipt.OperationId:N}|{i:D4}/{count:D4}|{data.Substring(i*180,Math.Min(180,data.Length-i*180))}").ToArray();
    }
    public static BookDeletionAudit Decode(Guid operation,IEnumerable<string> lines)
    {
        try
        {
            var parts=lines.Select(x=>x.Split('|',4)).OrderBy(x=>x[2],StringComparer.Ordinal).ToArray();
            var count=int.Parse(parts[0][2].Split('/')[1],CultureInfo.InvariantCulture);
            if(count!=parts.Length || parts.Where((p,i)=>p[0]!="LMCDEL1" || p[1]!=operation.ToString("N") || p[2]!=$"{i:D4}/{count:D4}").Any())throw new FormatException();
            var result=JsonSerializer.Deserialize<BookDeletionAudit>(Convert.FromBase64String(string.Concat(parts.Select(p=>p[3]))))??throw new FormatException();
            if(result.Receipt.OperationId!=operation || string.IsNullOrEmpty(result.BeforeRows))throw new FormatException();
            return result;
        }
        catch(Exception ex) when(ex is FormatException or JsonException or IndexOutOfRangeException or NullReferenceException)
        {throw new CorrectionException(422,"O histórico da exclusão está incompleto. Confira os registros antes de continuar.");}
    }
}

public sealed partial class Repository
{
    public async Task<BookDeletePreparation> PrepareBookDeletion(short branch,int id,CancellationToken ct)
    {
        if(branch<=0 || id<=0)throw new CorrectionException(400,"Informe a filial e o livro.");
        await using var c=await Open(ct);using var tx=c.BeginTransaction(IsolationLevel.Serializable);
        var result=await ReadBookDeletion(c,tx,branch,id,ct);await tx.CommitAsync(ct);return result.Preview;
    }
    internal static async Task<BookDeletePreparation> PrepareBookDeletionInTransaction(SqlConnection c,SqlTransaction tx,short branch,int id,CancellationToken ct)
        =>(await ReadBookDeletion(c,tx,branch,id,ct)).Preview;

    private static async Task<(BookDeletePreparation Preview,string Rows)> ReadBookDeletion(SqlConnection c,SqlTransaction tx,short branch,int id,CancellationToken ct)
    {
        int product;int? number;string date,name;int tanks,pumps,receipts;
        using(var cmd=Command(c,"""
            SELECT l.ID_PRODUTOS,CONVERT(char(10),l.DTACONTA,23),RTRIM(p.NOMEPRODUTO),l.NROCONTROLE,
             (SELECT COUNT(*) FROM dbo.LMCTANQUES WHERE ID_FILIAL=@branch AND ID_LMC=@id),
             (SELECT COUNT(*) FROM dbo.LMCBICOS WHERE ID_FILIAL=@branch AND ID_LMC=@id),
             (SELECT COUNT(*) FROM dbo.LMCCOMPROVANTES WHERE ID_FILIAL=@branch AND ID_LMC=@id)
            FROM dbo.LMC l WITH(UPDLOCK,HOLDLOCK) LEFT JOIN dbo.PRODUTOS p ON p.ID_FILIAL=l.ID_FILIAL AND p.ID_PRODUTOS=l.ID_PRODUTOS
            WHERE l.ID_FILIAL=@branch AND l.ID_LMC=@id;
            """,branch,id:id,transaction:tx))
        {
            await using var r=await cmd.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct))throw new CorrectionException(404,"Este LMC não foi localizado. Atualize a lista.");
            product=r.GetInt32(0);date=r.GetString(1);name=r.IsDBNull(2)?"Produto sem cadastro":r.GetString(2);number=r.IsDBNull(3)?null:r.GetInt32(3);
            tanks=r.GetInt32(4);pumps=r.GetInt32(5);receipts=r.GetInt32(6);
        }
        var following=new List<Book>();var links=new List<int>();
        using(var cmd=Command(c,"""
            SELECT ID_LMC,CONVERT(char(10),DTACONTA,23),NROCONTROLE FROM dbo.LMC
            WHERE ID_FILIAL=@branch AND ID_PRODUTOS=@product AND DTACONTA>@date ORDER BY DTACONTA,ID_LMC;
            SELECT w.ID_LMC FROM dbo.LMC_WS_VINCULADO w JOIN dbo.LMC l ON l.ID_FILIAL=w.ID_FILIAL AND l.ID_LMC=w.ID_LMC
            WHERE l.ID_FILIAL=@branch AND l.ID_PRODUTOS=@product AND l.DTACONTA>=@date ORDER BY w.ID_LMC;
            """,branch,transaction:tx))
        {
            cmd.Parameters.Add("@product",SqlDbType.Int).Value=product;
            cmd.Parameters.Add("@date",SqlDbType.Date).Value=Rules.Date(date)!.Value.ToDateTime(TimeOnly.MinValue);
            await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))following.Add(new(r.GetInt32(0),product,name,r.GetString(1),r.IsDBNull(2)?null:r.GetInt32(2)));
            await r.NextResultAsync(ct);while(await r.ReadAsync(ct))links.Add(r.GetInt32(0));
        }
        var rows=await BookDeletionRows(c,tx,branch,id,ct);
        var version=BookWriting.Hash(new {branch,id,rows,following,links});
        string[] blockers=links.Count>0?["Há LMC vinculado a envio externo neste dia ou no período posterior. Confira o envio antes de excluir."]:[];
        return (new(branch,id,product,name,date,number,tanks,pumps,receipts,version,following,blockers),rows);
    }

    internal static async Task<string> BookDeletionRows(SqlConnection c,SqlTransaction tx,short branch,int id,CancellationToken ct)
    {
        using var cmd=Command(c,"""
            SELECT (SELECT * FROM dbo.LMC WHERE ID_FILIAL=@branch AND ID_LMC=@id FOR XML RAW,BINARY BASE64),
             (SELECT * FROM dbo.LMCTANQUES WHERE ID_FILIAL=@branch AND ID_LMC=@id ORDER BY ID_LMCTANQUES FOR XML RAW,BINARY BASE64),
             (SELECT * FROM dbo.LMCBICOS WHERE ID_FILIAL=@branch AND ID_LMC=@id ORDER BY ID_LMCBICOS FOR XML RAW,BINARY BASE64),
             (SELECT * FROM dbo.LMCCOMPROVANTES WHERE ID_FILIAL=@branch AND ID_LMC=@id ORDER BY ID_LMCCOMPROVANTES FOR XML RAW,BINARY BASE64);
            """,branch,id:id,transaction:tx);
        await using var r=await cmd.ExecuteReaderAsync(ct);await r.ReadAsync(ct);
        return JsonSerializer.Serialize(Enumerable.Range(0,r.FieldCount).Select(i=>r.IsDBNull(i)?"":r.GetString(i)).ToArray());
    }

    public async Task<BookDeleted> DeleteBook(BookDeleteRequest input,CancellationToken ct)
    {
        await using var c=await Open(ct);using var tx=c.BeginTransaction(IsolationLevel.Serializable);
        try {var result=await DeleteBookInTransaction(c,tx,input,ct);await tx.CommitAsync(ct);return result;}
        catch {try {await tx.RollbackAsync(CancellationToken.None);}catch(Exception ex) when(ex is SqlException or InvalidOperationException){}throw;}
    }

    internal async Task<BookDeleted> DeleteBookInTransaction(SqlConnection c,SqlTransaction tx,BookDeleteRequest input,CancellationToken ct)
    {
        BookDeletion.Validate(input);
        using(var cmd=Command(c,"""
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=N'LMCWEB-WRITE',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000;
            SELECT @result;
            """,transaction:tx))
            if(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))<0)throw new CorrectionException(409,"Outro LMC está sendo alterado. Tente novamente após concluir essa operação.");
        var fingerprint=BookDeletion.Fingerprint(input);
        var prior=await ReadBookDeletionAudit(c,tx,input.OperationId,ct);
        if(prior is not null)
        {
            if(prior.Fingerprint!=fingerprint || prior.Receipt.Branch!=input.Branch || prior.Receipt.BookId!=input.BookId)
                throw new CorrectionException(409,"O identificador da exclusão já foi usado com outro conteúdo.");
            return prior.Receipt with {AlreadyDeleted=true,LastBookDate=await LastBookDate(c,tx,input.Branch,ct)};
        }
        var context=await ReadBookDeletion(c,tx,input.Branch,input.BookId,ct);var p=context.Preview;
        if(p.Version!=input.Version)throw new CorrectionException(409,"O livro ou seus vínculos mudaram. Atualize a confirmação antes de excluir.");
        if(p.Blockers.Count>0)throw new CorrectionException(422,string.Join(" ",p.Blockers));
        var protectedBefore=await BookProtected(c,tx,input.Branch,input.BookId,ct);
        var fiscalBefore=await BookDeleteFiscalState(c,tx,ct);
        var receipt=new BookDeleted(input.Branch,p.BookId,p.ProductId,p.Product,p.Date,p.Number,input.OperationId,false,p.FollowingBooks,null);
        var audit=new BookDeletionAudit(fingerprint,DateTimeOffset.Now.ToString("O",CultureInfo.InvariantCulture),CurrentActor,context.Rows,receipt);
        _=BookDeletion.Encode(audit); // Confere o tamanho antes de remover qualquer linha.
        await RemoveBookRows(c,tx,input.Branch,input.BookId,ct);
        receipt=receipt with {LastBookDate=await LastBookDate(c,tx,input.Branch,ct)};audit=audit with {Receipt=receipt};
        await WriteBookDeletionAudit(c,tx,audit,ct);
        if(protectedBefore!=await BookProtected(c,tx,input.Branch,input.BookId,ct) || fiscalBefore!=await BookDeleteFiscalState(c,tx,ct))
            throw new CorrectionException(422,"As rotinas do banco alteraram dados fora deste LMC. A exclusão foi desfeita.");
        return receipt;
    }

    private static async Task RemoveBookRows(SqlConnection c,SqlTransaction tx,short branch,int id,CancellationToken ct)
    {
        using(var cmd=Command(c,"""
            DELETE FROM dbo.LMCCOMPROVANTES WHERE ID_FILIAL=@branch AND ID_LMC=@id;
            DELETE FROM dbo.LMCBICOS WHERE ID_FILIAL=@branch AND ID_LMC=@id;
            DELETE FROM dbo.LMCTANQUES WHERE ID_FILIAL=@branch AND ID_LMC=@id;
            DELETE FROM dbo.LMC WHERE ID_FILIAL=@branch AND ID_LMC=@id;
            SELECT (SELECT COUNT(*) FROM dbo.LMC WHERE ID_FILIAL=@branch AND ID_LMC=@id)
             +(SELECT COUNT(*) FROM dbo.LMCTANQUES WHERE ID_FILIAL=@branch AND ID_LMC=@id)
             +(SELECT COUNT(*) FROM dbo.LMCBICOS WHERE ID_FILIAL=@branch AND ID_LMC=@id)
             +(SELECT COUNT(*) FROM dbo.LMCCOMPROVANTES WHERE ID_FILIAL=@branch AND ID_LMC=@id);
            """,branch,id:id,transaction:tx))
            if(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))!=0)throw new CorrectionException(422,"A exclusão não removeu todos os detalhes. A operação foi desfeita.");
    }

    private static async Task<string?> LastBookDate(SqlConnection c,SqlTransaction tx,short branch,CancellationToken ct)
    {
        using var cmd=Command(c,"SELECT CONVERT(char(10),MAX(DTACONTA),23) FROM dbo.LMC WHERE ID_FILIAL=@branch;",branch,transaction:tx);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<string> BookDeleteFiscalState(SqlConnection c,SqlTransaction tx,CancellationToken ct)
    {
        using var cmd=Command(c,"""
            SELECT (SELECT * FROM dbo.COMPROVANTES c WHERE EXISTS(SELECT 1 FROM dbo.LMCENTRADATANQUES e WHERE e.ID_FILIAL=c.ID_FILIAL AND e.ID_DB=c.ID_DB AND e.ID_COMPROVANTE=c.ID_COMPROVANTE) ORDER BY ID_FILIAL,ID_DB,ID_COMPROVANTE FOR XML RAW,BINARY BASE64),
             (SELECT * FROM dbo.ITENSCOMPROVANTE c WHERE EXISTS(SELECT 1 FROM dbo.LMCENTRADATANQUES e WHERE e.ID_FILIAL=c.ID_FILIAL AND e.ID_DB=c.ID_DB AND e.ID_COMPROVANTE=c.ID_COMPROVANTE) ORDER BY ID_FILIAL,ID_DB,ID_ITENSCOMPROVANTE FOR XML RAW,BINARY BASE64),
             (SELECT * FROM dbo.MOVPRODUTOS m WHERE EXISTS(SELECT 1 FROM dbo.LMCENTRADATANQUES e WHERE e.ID_FILIAL=m.ID_FILIAL AND e.ID_DB=m.ID_DB AND e.ID_COMPROVANTE=m.ID_COMPROVANTE) ORDER BY ID_FILIAL,ID_DB,ID_MOVPRODUTOS FOR XML RAW,BINARY BASE64),
             (SELECT i.* FROM dbo.ITENSMOVPRODUTOS i JOIN dbo.MOVPRODUTOS m ON m.ID_FILIAL=i.ID_FILIAL AND m.ID_DB=i.ID_DB AND m.ID_MOVPRODUTOS=i.ID_MOVPRODUTOS WHERE EXISTS(SELECT 1 FROM dbo.LMCENTRADATANQUES e WHERE e.ID_FILIAL=m.ID_FILIAL AND e.ID_DB=m.ID_DB AND e.ID_COMPROVANTE=m.ID_COMPROVANTE) ORDER BY i.ID_FILIAL,i.ID_DB,i.ID_ITENSMOVPRODUTOS FOR XML RAW,BINARY BASE64);
            """,transaction:tx);
        await using var r=await cmd.ExecuteReaderAsync(ct);await r.ReadAsync(ct);
        return BookWriting.Hash(Enumerable.Range(0,r.FieldCount).Select(i=>r.IsDBNull(i)?"":r.GetString(i)).ToArray());
    }

    internal static async Task<BookDeletionAudit?> ReadBookDeletionAudit(SqlConnection c,SqlTransaction tx,Guid operation,CancellationToken ct)
    {
        using var cmd=Command(c,"SELECT HISTORICO FROM dbo.HISTORICOTURNORETRO WITH(UPDLOCK,HOLDLOCK) WHERE HISTORICO LIKE @prefix ORDER BY ID_HISTORICOTURNORETRO;",transaction:tx);
        cmd.Parameters.Add("@prefix",SqlDbType.VarChar,60).Value=$"{BookDeletion.Prefix}{operation:N}|%";
        await using var r=await cmd.ExecuteReaderAsync(ct);var lines=new List<string>();while(await r.ReadAsync(ct))lines.Add(r.GetString(0));
        return lines.Count==0?null:BookDeletion.Decode(operation,lines);
    }

    private static async Task WriteBookDeletionAudit(SqlConnection c,SqlTransaction tx,BookDeletionAudit audit,CancellationToken ct)
    {
        foreach(var line in BookDeletion.Encode(audit))
        {
            using var cmd=Command(c,"""
                INSERT dbo.HISTORICOTURNORETRO(ID_FILIAL,ID_DB,DATAATUAL,ID_TURNO,DTACONTA,HISTORICO)
                VALUES(@branch,@branch,GETDATE(),NULL,@date,@line);
                SELECT HISTORICO FROM dbo.HISTORICOTURNORETRO WHERE ID_HISTORICOTURNORETRO=CONVERT(int,SCOPE_IDENTITY());
                """,audit.Receipt.Branch,transaction:tx);
            cmd.Parameters.Add("@date",SqlDbType.Date).Value=Rules.Date(audit.Receipt.Date)!.Value.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@line",SqlDbType.VarChar,255).Value=line;
            if((string?)await cmd.ExecuteScalarAsync(ct)!=line)throw new CorrectionException(422,"O histórico da exclusão não foi preservado. Operação desfeita.");
        }
        var recorded=await ReadBookDeletionAudit(c,tx,audit.Receipt.OperationId,ct);
        if(recorded is null || BookWriting.Hash(recorded)!=BookWriting.Hash(audit))throw new CorrectionException(422,"Não foi possível conferir o histórico da exclusão. Operação desfeita.");
    }
}
