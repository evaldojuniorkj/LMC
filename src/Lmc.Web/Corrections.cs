using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Lmc.Web;

public sealed record ApplyRequest(string OperationalDate, int ShiftId, string? Reason, string Version,
    Guid OperationId, bool AcceptBookReview,IReadOnlyList<AllocationInput>? Allocations=null);
public sealed record CorrectionReceipt(Guid OperationId, int HistoryId, string AppliedAt, string Actor,
    int DocumentId, string SourceDate, int? SourceShiftId, string TargetDate, int TargetShiftId,
    string Reason, int[] ProductIds, bool AlreadyApplied, string ReviewFrom,AllocationRevision? Allocation=null);
public sealed record BookReceipt(int Id, int BookId, string BookDate, int ProductId, short TankId,
    decimal? Volume, bool SupplierMatches);
public sealed record CorrectionAssessment(IReadOnlyList<string> Blockers, IReadOnlyList<BookReceipt> BookReceipts);
public sealed class CorrectionException(int status, string message) : Exception(message)
{ public int Status { get; } = status; }

public static class CorrectionAudit
{
    public static string NormalizeReason(string? reason) => reason?.Trim() ?? "";

    public static string Fingerprint(short branch, short db, int id, ApplyRequest request)
    {
        var original=new {
            branch, db, id, request.OperationalDate, request.ShiftId, reason=NormalizeReason(request.Reason),
            request.Version, request.AcceptBookReview
        };
        // Mantém a assinatura das operações antigas, que não continham distribuição.
        return BookWriting.Hash(request.Allocations is null ? (object)original : new {Original=original,
            Allocations=request.Allocations.OrderBy(x=>x.ParcelId).ThenBy(x=>x.ProductId).ThenBy(x=>x.TankId).ThenBy(x=>x.Volume)});
    }

    public static string Encode(Detail detail, ApplyRequest request, string actor, string? reviewFrom=null)
    {
        var products = string.Join(',', detail.Parcels.Select(p=>p.ProductId).Distinct().Order());
        var source = detail.Note.OperationalDate!.Replace("-", "");
        reviewFrom ??= new[]{detail.Note.OperationalDate!,request.OperationalDate}.Min()!;
        var line = $"LMC1|{request.OperationId:N}|{detail.Note.Id}|{source},{detail.Note.ShiftId?.ToString() ?? "_"},{reviewFrom.Replace("-", "")}|{Fingerprint(detail.Note.BranchId,detail.Note.DatabaseId,detail.Note.Id,request)}|{actor}|{products}|{NormalizeReason(request.Reason)}";
        if (actor.Contains('|') || line.Length > 255)
            throw new CorrectionException(400,"Reduza a observação: o histórico existente comporta até 255 caracteres incluindo a identificação da operação.");
        return line;
    }

    public static CorrectionReceipt Decode(int historyId, DateTime appliedAt, string targetDate, int targetShift, string line, bool repeated)
    {
        var fields=line.Split('|',8);
        if (fields.Length!=8 || fields[0]!="LMC1") throw new InvalidOperationException("Histórico de correção inválido.");
        var source=fields[3].Split(',');
        return new(Guid.ParseExact(fields[1],"N"),historyId,appliedAt.ToString("yyyy-MM-ddTHH:mm:ss.fff",CultureInfo.InvariantCulture),
            fields[5],int.Parse(fields[2],CultureInfo.InvariantCulture),
            DateOnly.ParseExact(source[0],"yyyyMMdd",CultureInfo.InvariantCulture).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
            source[1]=="_" ? null : int.Parse(source[1],CultureInfo.InvariantCulture),targetDate,targetShift,fields[7],
            fields[6].Split(',').Select(x=>int.Parse(x,CultureInfo.InvariantCulture)).ToArray(),repeated,
            DateOnly.ParseExact(source[2],"yyyyMMdd",CultureInfo.InvariantCulture).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
    }
}

public sealed partial class Repository
{
    internal static string Actor => $"{Environment.UserDomainName}\\{Environment.UserName}";

    public async Task<CorrectionAssessment> Assess(short branch,short db,int id,DateOnly target,int shift,CancellationToken ct)
    {
        await using var c=await Open(ct);
        using var tx=c.BeginTransaction(IsolationLevel.Serializable);
        var detail=await ReadDetail(c,tx,branch,db,id,ct) ?? throw new CorrectionException(404,"Nota não localizada.");
        var result=await Assess(c,tx,detail,target,shift,ct);
        tx.Commit();
        return result;
    }

    private static async Task<CorrectionAssessment> Assess(SqlConnection c,SqlTransaction tx,Detail detail,DateOnly target,int shift,CancellationToken ct)
    {
        var n=detail.Note;
        var blockers=new List<string>();
        using (var cmd=Command(c,"""
            SELECT CANCELADO,SAIDAS_ENTRADAS FROM dbo.COMPROVANTES
            WHERE ID_FILIAL=@branch AND ID_DB=@db AND ID_COMPROVANTE=@id;
            SELECT t.ID_TURNOS,CONVERT(char(10),t.DATATURNO,23) AS Dia,t.TURNO,t.DEFINITIVO,t.STATUSTURNO,t.DATAFECHAMENTO
            FROM dbo.TURNOS t WHERE t.ID_FILIAL=@branch AND (t.ID_TURNOS=@targetShift OR t.ID_TURNOS=@sourceShift);
            SELECT COUNT(*) FROM dbo.LMC_WS_VINCULADO w
            JOIN dbo.LMC l ON l.ID_FILIAL=w.ID_FILIAL AND l.ID_LMC=w.ID_LMC
            WHERE l.ID_FILIAL=@branch AND l.DTACONTA>=@firstDate
              AND EXISTS(SELECT 1 FROM dbo.LMCENTRADATANQUES e WHERE e.ID_FILIAL=@branch AND e.ID_DB=@db
                  AND e.ID_COMPROVANTE=@id AND e.ID_PRODUTOS=l.ID_PRODUTOS);
            """,n.BranchId,n.DatabaseId,n.Id,tx))
        {
            cmd.Parameters.Add("@targetShift",SqlDbType.Int).Value=shift;
            cmd.Parameters.Add("@sourceShift",SqlDbType.Int).Value=(object?)n.ShiftId??DBNull.Value;
            var source=Rules.Date(n.OperationalDate) ?? target;
            cmd.Parameters.Add("@firstDate",SqlDbType.Date).Value=(source<target?source:target).ToDateTime(TimeOnly.MinValue);
            await using var r=await cmd.ExecuteReaderAsync(ct);
            if(await r.ReadAsync(ct))
            {
                if(r.GetBoolean(0)) blockers.Add("A nota está cancelada. A troca operacional não está disponível.");
                if(r.IsDBNull(1)||r.GetByte(1)!=1) blockers.Add("O documento não está identificado como entrada de mercadoria.");
            }
            await r.NextResultAsync(ct);
            var found=false;
            while(await r.ReadAsync(ct))
            {
                var isTarget=r.GetInt32(0)==shift;
                // O turno geral é compartilhado entre datas; a data operacional está no lançamento.
                if(isTarget) found=r.GetByte(2)==0 || r.GetString(1)==target.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
                if((!r.IsDBNull(3)&&r.GetBoolean(3)) || (!r.IsDBNull(4)&&r.GetByte(4)!=0) || !r.IsDBNull(5))
                    blockers.Add($"O turno {(isTarget ? "de destino" : "de origem")} tem indicação de fechamento. A reabertura precisa ser tratada antes da correção.");
            }
            if(!found) blockers.Add("Escolha o turno geral da filial ou um turno da data operacional selecionada.");
            await r.NextResultAsync(ct);
            if(await r.ReadAsync(ct) && r.GetInt32(0)>0)
                blockers.Add("Há LMC vinculado a envio externo no período afetado. É necessário conferir a situação do envio antes de alterar a entrada.");
        }
        if(n.Inconsistent || n.OperationalDate is null) blockers.Add("Existem datas ou vínculos divergentes na nota, nos movimentos ou nos tanques.");
        var receipts=new List<BookReceipt>();
        using(var cmd=Command(c,"""
            SELECT lc.ID_LMCCOMPROVANTES,lc.ID_LMC,CONVERT(char(10),l.DTACONTA,23),l.ID_PRODUTOS,
                lc.ID_TANQUES,CONVERT(decimal(19,4),lc.VOLUMERECEBIDO),
                CONVERT(bit,CASE WHEN NULLIF(REPLACE(REPLACE(REPLACE(RTRIM(lc.CNPJ_FORNECEDOR),'.',''),'/',''),'-',''),'') =
                    NULLIF(REPLACE(REPLACE(REPLACE(RTRIM(s.CNPJCPF),'.',''),'/',''),'-',''),'') THEN 1 ELSE 0 END)
            FROM dbo.COMPROVANTES c
            JOIN dbo.LMCCOMPROVANTES lc ON lc.ID_FILIAL=c.ID_FILIAL AND lc.NROCOMPROVANTE=c.NROCOMPROVANTE
            JOIN dbo.LMC l ON l.ID_FILIAL=lc.ID_FILIAL AND l.ID_LMC=lc.ID_LMC
            LEFT JOIN dbo.ENTIDADES s ON s.ID_FILIAL=c.ID_FILIAL AND s.ID_ENTIDADE=c.ID_ENTIDADE
            WHERE c.ID_FILIAL=@branch AND c.ID_DB=@db AND c.ID_COMPROVANTE=@id
              AND EXISTS(SELECT 1 FROM dbo.LMCENTRADATANQUES e WHERE e.ID_FILIAL=c.ID_FILIAL AND e.ID_DB=c.ID_DB
                  AND e.ID_COMPROVANTE=c.ID_COMPROVANTE AND e.ID_TANQUES=lc.ID_TANQUES AND e.ID_PRODUTOS=l.ID_PRODUTOS)
            ORDER BY l.DTACONTA,lc.ID_LMCCOMPROVANTES;
            """,n.BranchId,n.DatabaseId,n.Id,tx))
        {
            await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) receipts.Add(new(r.GetInt32(0),r.GetInt32(1),r.GetString(2),r.GetInt32(3),
                r.GetInt16(4),r.IsDBNull(5)?null:r.GetDecimal(5),r.GetBoolean(6)));
        }
        return new(blockers,receipts);
    }

    public async Task<CorrectionReceipt> Apply(short branch,short db,int id,ApplyRequest request,CancellationToken ct)
    {
        await using var c=await Open(ct);
        using var tx=c.BeginTransaction(IsolationLevel.Serializable);
        try
        {
            var receipt=await ApplyInTransaction(c,tx,branch,db,id,request,ct);
            await tx.CommitAsync(ct);
            return receipt;
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch(Exception ex) when(ex is SqlException or InvalidOperationException) { }
            throw;
        }
    }

    internal async Task<CorrectionReceipt> ApplyInTransaction(SqlConnection c,SqlTransaction tx,short branch,short db,int id,ApplyRequest request,CancellationToken ct)
    {
        if(request.OperationId==Guid.Empty || Rules.Date(request.OperationalDate) is not { } target || target.Year is <1900 or >2078)
            throw new CorrectionException(400,"Informe data e identificador da operação válidos.");
        if(CorrectionAudit.NormalizeReason(request.Reason).Length>80)
            throw new CorrectionException(400,"A observação opcional deve ter no máximo 80 caracteres.");
        if(request.Allocations is not null && (request.Allocations.Count is <1 or >100 || request.Allocations.Any(x=>x is null)))
            throw new CorrectionException(400,"Informe uma distribuição válida dos volumes.");
        if(!request.AcceptBookReview) throw new CorrectionException(400,"Confirme que os LMCs afetados serão revisados depois da correção da entrada.");
        // O histórico existente participa da mesma transação. A trava impede dois recibos para a mesma operação.
        CorrectionReceipt? repeated=null;
        using(var cmd=Command(c,"""
            SELECT ID_HISTORICOTURNORETRO,DATAATUAL,CONVERT(char(10),DTACONTA,23),ID_TURNO,HISTORICO,ID_FILIAL,ID_DB
            FROM dbo.HISTORICOTURNORETRO WITH (UPDLOCK,HOLDLOCK)
            WHERE HISTORICO LIKE @prefix;
            """,branch,db,id,tx))
        {
            cmd.Parameters.Add("@prefix",SqlDbType.VarChar,50).Value=$"LMC1|{request.OperationId:N}|%";
            await using var r=await cmd.ExecuteReaderAsync(ct);
            if(await r.ReadAsync(ct))
            {
                var line=r.GetString(4);
                if(r.GetInt32(5)!=branch || r.GetInt32(6)!=db || line.Split('|',8)[4]!=CorrectionAudit.Fingerprint(branch,db,id,request))
                    throw new CorrectionException(409,"O identificador da operação já foi usado com outros dados.");
                repeated=CorrectionAudit.Decode(r.GetInt32(0),r.GetDateTime(1),r.GetString(2),r.GetInt32(3),line,true);
            }
        }
        if(repeated is not null) return repeated with {Allocation=await ReadAllocationHistory(c,tx,branch,request.OperationId,ct)};
        using(var cmd=Command(c,"SELECT ID_COMPROVANTE FROM dbo.COMPROVANTES WITH (UPDLOCK,HOLDLOCK) WHERE ID_FILIAL=@branch AND ID_DB=@db AND ID_COMPROVANTE=@id;",branch,db,id,tx))
            if(await cmd.ExecuteScalarAsync(ct) is null) throw new CorrectionException(404,"Nota não localizada.");
        var detail=await ReadDetail(c,tx,branch,db,id,ct) ?? throw new CorrectionException(404,"Entrada de combustível não localizada.");
        if(request.Version!=detail.Version) throw new CorrectionException(409,"A nota ou seus movimentos mudaram. Reabra a revisão e gere outra prévia.");
        var assessment=await Assess(c,tx,detail,target,request.ShiftId,ct);
        if(assessment.Blockers.Count>0) throw new CorrectionException(422,string.Join(" ",assessment.Blockers));
        var allocations=AllocationRules.Validate(detail,request.OperationalDate,request.Allocations);
        if(detail.Note.OperationalDate==request.OperationalDate && detail.Note.ShiftId==request.ShiftId && allocations is null)
            throw new CorrectionException(409,"A entrada já está na data, no turno e na distribuição escolhidos.");
        _=AllocationRules.Effects(detail,request.OperationalDate,allocations);
        var reviewFrom=assessment.BookReceipts.Select(r=>r.BookDate).Append(detail.Note.OperationalDate!).Append(request.OperationalDate).Min()!;
        var audit=CorrectionAudit.Encode(detail,request,CurrentActor,reviewFrom);
        var protectedBefore=await ProtectedState(c,tx,branch,db,id,ct,allocations is not null);
        var allocationProtected=allocations is null?null:await AllocationProtectedState(c,tx,branch,db,id,ct);
        var stocksBefore=allocations is null?null:await ReadAllocationStocks(c,tx,ct);
        using(var cmd=Command(c,"""
            UPDATE dbo.COMPROVANTES SET DTACONTA=@date,ID_TURNOS=@shift
            WHERE ID_FILIAL=@branch AND ID_DB=@db AND ID_COMPROVANTE=@id
              AND (DTACONTA<>@date OR ID_TURNOS<>@shift OR DTACONTA IS NULL OR ID_TURNOS IS NULL);
            UPDATE dbo.MOVPRODUTOS SET DTACONTA=@date,ID_TURNOS=@shift
            WHERE ID_FILIAL=@branch AND ID_DB=@db AND ID_COMPROVANTE=@id
              AND (DTACONTA<>@date OR ID_TURNOS<>@shift OR DTACONTA IS NULL OR ID_TURNOS IS NULL);
            UPDATE dbo.LMCENTRADATANQUES SET DTACONTA=@date
            WHERE ID_FILIAL=@branch AND ID_DB=@db AND ID_COMPROVANTE=@id AND (DTACONTA<>@date OR DTACONTA IS NULL);
            """,branch,db,id,tx))
        {
            cmd.Parameters.Add("@date",SqlDbType.SmallDateTime).Value=target.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@shift",SqlDbType.Int).Value=request.ShiftId;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if(allocations is not null) await WriteAllocations(c,tx,detail,request.OperationalDate,allocations,ct);
        if(allocations is not null) VerifyAllocationStocks(detail,allocations,stocksBefore!,await ReadAllocationStocks(c,tx,ct));
        var after=await ReadDetail(c,tx,branch,db,id,ct);
        if(after is null || after.Note.Inconsistent || after.Note.OperationalDate!=request.OperationalDate || after.Note.ShiftId!=request.ShiftId
            || after.Parcels.Any(p=>p.OperationalDate!=request.OperationalDate) || after.Movements.Any(m=>m.OperationalDate!=request.OperationalDate||m.ShiftId!=request.ShiftId)
            || protectedBefore!=await ProtectedState(c,tx,branch,db,id,ct,allocations is not null)
            || allocationProtected is not null && allocationProtected!=await AllocationProtectedState(c,tx,branch,db,id,ct))
            throw new CorrectionException(422,"As rotinas do banco produziram um efeito diferente do esperado. A correção foi desfeita.");
        if(allocations is not null) await WriteAllocationHistory(c,tx,detail,after!,request,ct);
        using(var cmd=Command(c,"""
            INSERT INTO dbo.HISTORICOTURNORETRO(ID_FILIAL,ID_DB,DATAATUAL,ID_TURNO,DTACONTA,ID_USUARIOLIBERADO,ID_USUARIOLIBERACAO,HISTORICO)
            VALUES(@branch,@db,GETDATE(),@shift,@date,NULL,NULL,@audit);
            SELECT ID_HISTORICOTURNORETRO,DATAATUAL,CONVERT(char(10),DTACONTA,23),ID_TURNO,HISTORICO
            FROM dbo.HISTORICOTURNORETRO WHERE ID_HISTORICOTURNORETRO=CONVERT(int,SCOPE_IDENTITY());
            """,branch,db,id,tx))
        {
            cmd.Parameters.Add("@date",SqlDbType.Date).Value=target.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@shift",SqlDbType.Int).Value=request.ShiftId;
            cmd.Parameters.Add("@audit",SqlDbType.NVarChar,255).Value=audit;
            await using var r=await cmd.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct) || r.GetString(4)!=audit)
                throw new CorrectionException(422,"O histórico não preservou todos os caracteres da observação. A operação foi desfeita; use caracteres compatíveis com o banco.");
            return CorrectionAudit.Decode(r.GetInt32(0),r.GetDateTime(1),r.GetString(2),r.GetInt32(3),audit,false)
                with {Allocation=allocations is null?null:new(detail.Parcels,after!.Parcels)};
        }
    }

    private static async Task<string> ProtectedState(SqlConnection c,SqlTransaction tx,short branch,short db,int id,CancellationToken ct,bool changingAllocation=false)
    {
        using var cmd=Command(c,"""
            SELECT * FROM dbo.COMPROVANTES WHERE ID_FILIAL=@branch AND ID_DB=@db AND ID_COMPROVANTE=@id;
            SELECT * FROM dbo.ITENSCOMPROVANTE WHERE ID_FILIAL=@branch AND ID_DB=@db AND ID_COMPROVANTE=@id ORDER BY ID_ITENSCOMPROVANTE;
            SELECT * FROM dbo.MOVPRODUTOS WHERE ID_FILIAL=@branch AND ID_DB=@db AND ID_COMPROVANTE=@id ORDER BY ID_MOVPRODUTOS;
            SELECT i.* FROM dbo.ITENSMOVPRODUTOS i JOIN dbo.MOVPRODUTOS m ON m.ID_FILIAL=i.ID_FILIAL AND m.ID_DB=i.ID_DB AND m.ID_MOVPRODUTOS=i.ID_MOVPRODUTOS
            WHERE m.ID_FILIAL=@branch AND m.ID_DB=@db AND m.ID_COMPROVANTE=@id ORDER BY i.ID_ITENSMOVPRODUTOS;
            SELECT * FROM dbo.LMCENTRADATANQUES WHERE ID_FILIAL=@branch AND ID_DB=@db AND ID_COMPROVANTE=@id ORDER BY ID_LMCENTRADATANQUES;
            SELECT * FROM dbo.COMPENTRADAS WHERE ID_FILIAL=@branch AND ID_DB=@db AND ID_COMPROVANTE=@id ORDER BY ID_COMPENTRADAS;
            """,branch,db,id,tx);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        var sets=new List<List<Dictionary<string,object?>>>();
        var index=0;
        do
        {
            var rows=new List<Dictionary<string,object?>>();
            while(await r.ReadAsync(ct))
            {
                if(changingAllocation && index==4) continue;
                var row=new Dictionary<string,object?>();
                for(var i=0;i<r.FieldCount;i++)
                {
                    var name=r.GetName(i);
                    if(name=="DATAREPL" || (index is 0 or 2 or 4 && name=="DTACONTA") || (index is 0 or 2 && name=="ID_TURNOS")) continue;
                    row[name]=r.IsDBNull(i)?null:r.GetValue(i);
                }
                rows.Add(row);
            }
            sets.Add(rows);index++;
        }while(await r.NextResultAsync(ct));
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(sets)));
    }

    public async Task<List<CorrectionReceipt>> History(short branch,short? db,int? id,CancellationToken ct)
    {
        await using var c=await Open(ct);
        using var cmd=Command(c,"""
            SELECT ID_HISTORICOTURNORETRO,DATAATUAL,CONVERT(char(10),DTACONTA,23),ID_TURNO,HISTORICO
            FROM dbo.HISTORICOTURNORETRO WHERE ID_FILIAL=@branch AND (@db IS NULL OR ID_DB=@db)
              AND HISTORICO LIKE 'LMC1|%' ORDER BY ID_HISTORICOTURNORETRO DESC;
            """,branch,db,id);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        var rows=new List<CorrectionReceipt>();
        while(await r.ReadAsync(ct))
        {
            var item=CorrectionAudit.Decode(r.GetInt32(0),r.GetDateTime(1),r.GetString(2),r.GetInt32(3),r.GetString(4),false);
            if(id is null || item.DocumentId==id) rows.Add(item);
        }
        await r.DisposeAsync();
        if(id.HasValue) for(var i=0;i<rows.Count;i++) rows[i]=rows[i] with {Allocation=await ReadAllocationHistory(c,null,branch,rows[i].OperationId,ct)};
        return rows;
    }
}
