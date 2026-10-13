using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Lmc.Web;

public sealed record AllocationRevision(IReadOnlyList<Parcel> Before,IReadOnlyList<Parcel> After);
internal sealed record AllocationStock(int Id,short Branch,short Tank,float? Volume);

public static class AllocationRules
{
    public static decimal StoredVolume(decimal value)=>decimal.Round((decimal)(double)(float)value,4,MidpointRounding.AwayFromZero);

    public static IReadOnlyList<AllocationInput>? Validate(Detail detail,string targetDate,IReadOnlyList<AllocationInput>? input)
    {
        if(input is null) return null;
        if(input.Count is <1 or >100 || input.Any(x=>x is null || x.Volume is null or <0 or >1000000000
            || decimal.Round(x.Volume.Value,4)!=x.Volume || x.TankId<=0 || x.ProductId<=0 || x.ParcelId is <=0))
            throw new CorrectionException(400,"Informe tanques e volumes válidos, com até quatro casas decimais.");
        if(detail.Parcels.Any(x=>x.Volume is null or <0)) throw new CorrectionException(422,"Confira os volumes originais antes de redistribuir.");
        var ids=input.Where(x=>x.ParcelId.HasValue).Select(x=>x.ParcelId!.Value).Order().ToArray();
        if(!ids.SequenceEqual(detail.Parcels.Select(x=>x.Id).Order()))
            throw new CorrectionException(400,"A distribuição deve incluir cada parcela original uma vez. Use zero para retirar o volume de uma parcela.");
        foreach(var row in input)
        {
            var original=detail.Parcels.SingleOrDefault(x=>x.Id==row.ParcelId);
            if(!detail.Parcels.Any(x=>x.ProductId==row.ProductId) || original is not null && original.ProductId!=row.ProductId)
                throw new CorrectionException(400,"A redistribuição não pode trocar o combustível da nota.");
            if(row.Volume>0)
            {
                var tank=detail.AllocationTanks?.SingleOrDefault(x=>x.TankId==row.TankId && x.ProductId==row.ProductId);
                // Uma parcela histórica inalterada não depende do cadastro atual de tanques.
                var unchanged=original is not null && original.TankId==row.TankId && original.Volume==row.Volume;
                if(!unchanged && (tank is null || !tank.Active || tank.ActivatedAt is not null && string.CompareOrdinal(tank.ActivatedAt,targetDate)>0))
                    throw new CorrectionException(422,"Escolha um tanque ativo da filial, do mesmo combustível e disponível na data do LMC.");
            }
        }
        foreach(var product in detail.Parcels.GroupBy(x=>x.ProductId))
        {
            var total=product.Sum(x=>x.Volume!.Value);
            var rows=input.Where(x=>x.ProductId==product.Key).ToArray();
            if(rows.Sum(x=>x.Volume!.Value)!=total)
                throw new CorrectionException(422,$"{product.First().Product}: distribua exatamente {total.ToString("N4",CultureInfo.GetCultureInfo("pt-BR"))} L. O total da nota deve ser mantido por combustível.");
            if(rows.Sum(x=>StoredVolume(x.Volume!.Value))!=total)
                throw new CorrectionException(422,"Essas casas decimais alterariam o total na precisão do banco. Ajuste a distribuição mantendo o total original.");
        }
        var changed=input.Any(x=>x.ParcelId is null ? x.Volume>0 : detail.Parcels.Any(p=>p.Id==x.ParcelId&&(p.TankId!=x.TankId||p.Volume!=x.Volume)));
        return changed?input.Where(x=>x.ParcelId.HasValue||x.Volume>0).ToArray():null;
    }

    public static IReadOnlyList<VolumeEffect> Effects(Detail detail,string date,IReadOnlyList<AllocationInput>? allocations)
    {
        if(allocations is null) return Rules.Effects(detail,date);
        _=Rules.Effects(detail,date);
        var changes=detail.Parcels.Select(x=>new VolumeEffect(detail.Note.OperationalDate!,x.ProductId,x.Product,x.TankId,x.Tank,-x.Volume!.Value))
            .Concat(allocations.Where(x=>x.Volume>0).Select(x=>new VolumeEffect(date,x.ProductId,
                detail.Parcels.First(p=>p.ProductId==x.ProductId).Product,x.TankId,
                detail.AllocationTanks?.FirstOrDefault(t=>t.TankId==x.TankId)?.Tank??x.TankId.ToString(),x.Volume!.Value)));
        return changes.GroupBy(x=>new{x.Date,x.ProductId,x.TankId}).Select(g=>g.First() with {Liters=g.Sum(x=>x.Liters)})
            .Where(x=>x.Liters!=0).OrderBy(x=>x.Date).ThenBy(x=>x.ProductId).ThenBy(x=>x.TankId).ToArray();
    }

    public static IReadOnlyList<string> EncodeHistory(Guid operation,AllocationRevision revision)
    {
        var data=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(revision));
        const int size=180;var count=(data.Length+size-1)/size;
        return Enumerable.Range(0,count).Select(i=>$"LMCV1|{operation:N}|{i:D4}/{count:D4}|{data.Substring(i*size,Math.Min(size,data.Length-i*size))}").ToArray();
    }
    public static AllocationRevision DecodeHistory(IEnumerable<string> lines)
    {
        try
        {
            var parts=lines.Select(x=>x.Split('|',4)).OrderBy(x=>x[2],StringComparer.Ordinal).ToArray();
            var count=int.Parse(parts[0][2].Split('/')[1],CultureInfo.InvariantCulture);
            if(parts.Length!=count || parts.Where((p,i)=>p[2]!=$"{i:D4}/{count:D4}").Any()) throw new FormatException();
            return JsonSerializer.Deserialize<AllocationRevision>(Convert.FromBase64String(string.Concat(parts.Select(x=>x[3]))))??throw new FormatException();
        }
        catch(Exception ex) when(ex is FormatException or JsonException or IndexOutOfRangeException)
        {throw new CorrectionException(422,"O histórico da distribuição está incompleto. Confira os registros antes de continuar.");}
    }
}

public sealed partial class Repository
{
    private static async Task WriteAllocations(SqlConnection c,SqlTransaction tx,Detail before,string date,IReadOnlyList<AllocationInput> allocations,CancellationToken ct)
    {
        var n=before.Note;var expected=new List<Parcel>();
        foreach(var row in allocations)
        {
            var original=before.Parcels.SingleOrDefault(x=>x.Id==row.ParcelId);
            if(original is not null && original.TankId==row.TankId && original.Volume==row.Volume && row.Volume>0)
            {
                expected.Add(original with {OperationalDate=date});
                continue;
            }
            // As triggers da base ajustam o saldo calculado em DELETE/INSERT, mas não em UPDATE de QTDE.
            // Recriar só a parcela modificada conserva o efeito líquido por tanque; o histórico conserva seu ID anterior.
            if(original is not null)
            {
                using var delete=Command(c,"DELETE dbo.LMCENTRADATANQUES WHERE ID_FILIAL=@branch AND ID_DB=@db AND ID_COMPROVANTE=@id AND ID_LMCENTRADATANQUES=@parcel;",n.BranchId,n.DatabaseId,n.Id,tx);
                delete.Parameters.Add("@parcel",SqlDbType.Int).Value=original.Id;await delete.ExecuteNonQueryAsync(ct);
            }
            if(row.Volume==0)continue;
            using var cmd=Command(c,"""
                INSERT dbo.LMCENTRADATANQUES(ID_FILIAL,ID_DB,ID_COMPROVANTE,ID_PRODUTOS,ID_TANQUES,DTACONTA,QTDE,SELOFISCAL)
                VALUES(@branch,@db,@id,@product,@tank,@date,@volume,@seal);
                SELECT CONVERT(int,SCOPE_IDENTITY());
                """,n.BranchId,n.DatabaseId,n.Id,tx);
            cmd.Parameters.Add("@seal",SqlDbType.VarChar,20).Value=(object?)original?.FiscalSeal??DBNull.Value;
            cmd.Parameters.Add("@product",SqlDbType.Int).Value=row.ProductId;cmd.Parameters.Add("@tank",SqlDbType.SmallInt).Value=row.TankId;
            cmd.Parameters.Add("@date",SqlDbType.SmallDateTime).Value=Rules.Date(date)!.Value.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@volume",SqlDbType.Real).Value=(float)row.Volume!.Value;
            var id=Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            expected.Add(new(id,row.ProductId,"",row.TankId,"",AllocationRules.StoredVolume(row.Volume.Value),date,null,original?.FiscalSeal));
        }
        var after=await ReadDetail(c,tx,n.BranchId,n.DatabaseId,n.Id,ct)??throw new CorrectionException(422,"A distribuição deixou de ser localizada. Operação desfeita.");
        if(after.Parcels.Count!=expected.Count || expected.Any(e=>!after.Parcels.Any(p=>p.Id==e.Id&&p.ProductId==e.ProductId&&p.TankId==e.TankId&&p.Volume==e.Volume&&p.OperationalDate==date&&p.FiscalSeal==e.FiscalSeal))
            || before.Parcels.GroupBy(p=>p.ProductId).Any(g=>g.Sum(p=>p.Volume)!=after.Parcels.Where(p=>p.ProductId==g.Key).Sum(p=>p.Volume)))
            throw new CorrectionException(422,"A distribuição gravada não conservou os valores esperados. Operação desfeita.");
    }

    private static async Task<List<AllocationStock>> ReadAllocationStocks(SqlConnection c,SqlTransaction tx,CancellationToken ct)
    {
        using var cmd=Command(c,"SELECT ID_QTDESTANQUES,ID_FILIAL,ID_TANQUES,QTDEATUAL FROM dbo.QTDESTANQUES ORDER BY ID_QTDESTANQUES;",transaction:tx);
        await using var r=await cmd.ExecuteReaderAsync(ct);var stocks=new List<AllocationStock>();
        while(await r.ReadAsync(ct))stocks.Add(new(r.GetInt16(0),r.GetInt16(1),r.GetInt16(2),r.IsDBNull(3)?null:r.GetFloat(3)));
        return stocks;
    }

    private static void VerifyAllocationStocks(Detail before,IReadOnlyList<AllocationInput> allocations,IReadOnlyList<AllocationStock> original,IReadOnlyList<AllocationStock> after)
    {
        var deltas=before.Parcels.Select(p=>(p.TankId,Volume:-p.Volume!.Value)).Concat(allocations.Select(p=>(p.TankId,Volume:p.Volume!.Value)))
            .GroupBy(x=>x.TankId).ToDictionary(g=>g.Key,g=>g.Sum(x=>x.Volume));
        if(deltas.Keys.Any(t=>original.Count(x=>x.Branch==before.Note.BranchId&&x.Tank==t&&x.Volume.HasValue)!=1) || original.Count!=after.Count)
            throw new CorrectionException(422,"Não foi possível conferir o saldo calculado dos tanques. A distribuição foi desfeita.");
        foreach(var old in original)
        {
            var current=after.SingleOrDefault(x=>x.Id==old.Id&&x.Branch==old.Branch&&x.Tank==old.Tank);
            if(current is null || current.Branch!=old.Branch || current.Tank!=old.Tank)
                throw new CorrectionException(422,"O cadastro de saldos mudou durante a distribuição. Operação desfeita.");
            if(old.Branch!=before.Note.BranchId || !deltas.TryGetValue(old.Tank,out var delta))
            {
                if(current.Volume!=old.Volume)throw new CorrectionException(422,"Um tanque fora da distribuição teve seu saldo alterado. Operação desfeita.");
            }
            else if(current.Volume is null || Math.Abs((double)current.Volume.Value-(double)old.Volume!-(double)delta)>0.02)
                throw new CorrectionException(422,"O saldo calculado do tanque não acompanhou os litros redistribuídos. Operação desfeita.");
        }
    }

    private static async Task WriteAllocationHistory(SqlConnection c,SqlTransaction tx,Detail before,Detail after,ApplyRequest request,CancellationToken ct)
    {
        foreach(var line in AllocationRules.EncodeHistory(request.OperationId,new(before.Parcels,after.Parcels)))
        {
            using var cmd=Command(c,"""
                INSERT dbo.HISTORICOTURNORETRO(ID_FILIAL,ID_DB,DATAATUAL,ID_TURNO,DTACONTA,ID_USUARIOLIBERADO,ID_USUARIOLIBERACAO,HISTORICO)
                VALUES(@branch,@db,GETDATE(),@shift,@date,NULL,NULL,@audit);
                SELECT HISTORICO FROM dbo.HISTORICOTURNORETRO WHERE ID_HISTORICOTURNORETRO=CONVERT(int,SCOPE_IDENTITY());
                """,before.Note.BranchId,before.Note.DatabaseId,before.Note.Id,tx);
            cmd.Parameters.Add("@shift",SqlDbType.Int).Value=request.ShiftId;
            cmd.Parameters.Add("@date",SqlDbType.Date).Value=Rules.Date(request.OperationalDate)!.Value.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@audit",SqlDbType.VarChar,255).Value=line;
            if((string?)await cmd.ExecuteScalarAsync(ct)!=line)throw new CorrectionException(422,"O histórico dos volumes não foi preservado. Operação desfeita.");
        }
    }

    private static async Task<AllocationRevision?> ReadAllocationHistory(SqlConnection c,SqlTransaction? tx,short branch,Guid operation,CancellationToken ct)
    {
        using var cmd=Command(c,"SELECT HISTORICO FROM dbo.HISTORICOTURNORETRO WHERE ID_FILIAL=@branch AND HISTORICO LIKE @prefix ORDER BY ID_HISTORICOTURNORETRO;",branch,transaction:tx);
        cmd.Parameters.Add("@prefix",SqlDbType.VarChar,50).Value=$"LMCV1|{operation:N}|%";
        await using var r=await cmd.ExecuteReaderAsync(ct);var lines=new List<string>();
        while(await r.ReadAsync(ct))lines.Add(r.GetString(0));
        return lines.Count==0?null:AllocationRules.DecodeHistory(lines);
    }

    internal static async Task<string> AllocationProtectedState(SqlConnection c,SqlTransaction tx,short branch,short db,int id,CancellationToken ct)
    {
        using var cmd=Command(c,Resource("AllocationProtected.sql"),branch,db,id,tx);
        await using var r=await cmd.ExecuteReaderAsync(ct);await r.ReadAsync(ct);
        return BookWriting.Hash(Enumerable.Range(0,r.FieldCount).Select(i=>r.IsDBNull(i)?"":r.GetString(i)).ToArray());
    }
}
