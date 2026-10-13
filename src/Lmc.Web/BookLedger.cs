using System.Data;
using Microsoft.Data.SqlClient;

namespace Lmc.Web;

public sealed record BookLedgerRow(int Id,int ProductId,string Product,string Date,int? Number,decimal? Opening,decimal? Received,decimal? Sales,
    decimal? Closing,decimal? SalesValue,bool Managed,string? LastSavedAt)
{
    public decimal? BookStock=>Opening+Received-Sales;
    public decimal? Difference=>Closing-BookStock;
    public decimal? Percent=>BookStock>0?Difference/BookStock*100:null;
}

public sealed partial class Repository
{
    public async Task<IReadOnlyList<BookLedgerRow>> Ledger(short branch,DateOnly from,DateOnly until,CancellationToken ct)
    {
        await using var c=await Open(ct);
        using var cmd=Command(c,"""
            SELECT l.ID_LMC,l.ID_PRODUTOS,RTRIM(p.NOMEPRODUTO),CONVERT(char(10),l.DTACONTA,23),l.NROCONTROLE,
              t.Opening,e.Received,b.Sales,t.Closing,CONVERT(decimal(19,4),l.VLRVENDASDIA),CONVERT(varchar(max),l.OBS)
            FROM dbo.LMC l LEFT JOIN dbo.PRODUTOS p ON p.ID_FILIAL=l.ID_FILIAL AND p.ID_PRODUTOS=l.ID_PRODUTOS
            OUTER APPLY(SELECT CASE WHEN COUNT(*)>0 AND COUNT(QTDETANQUE)=COUNT(*) THEN SUM(CONVERT(decimal(19,4),QTDETANQUE)) END Opening,
                CASE WHEN COUNT(*)>0 AND COUNT(QTDECONCILIACAO)=COUNT(*) THEN SUM(CONVERT(decimal(19,4),QTDECONCILIACAO)) END Closing
                FROM dbo.LMCTANQUES WHERE ID_FILIAL=l.ID_FILIAL AND ID_LMC=l.ID_LMC)t
            OUTER APPLY(SELECT CASE WHEN COUNT(*)=COUNT(VOLUMERECEBIDO) THEN ISNULL(SUM(CONVERT(decimal(19,4),VOLUMERECEBIDO)),0) END Received
                FROM dbo.LMCCOMPROVANTES WHERE ID_FILIAL=l.ID_FILIAL AND ID_LMC=l.ID_LMC)e
            OUTER APPLY(SELECT CASE WHEN COUNT(*)>0 AND COUNT(*)=COUNT(VENDAS) THEN SUM(VENDAS) END Sales
                FROM dbo.LMCBICOS WHERE ID_FILIAL=l.ID_FILIAL AND ID_LMC=l.ID_LMC)b
            WHERE l.ID_FILIAL=@branch AND l.DTACONTA>=@from AND l.DTACONTA<DATEADD(day,1,@until)
            ORDER BY l.DTACONTA,l.ID_PRODUTOS,l.ID_LMC;
            """,branch);
        cmd.Parameters.Add("@from",SqlDbType.Date).Value=from.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@until",SqlDbType.Date).Value=until.ToDateTime(TimeOnly.MinValue);
        var rows=new List<BookLedgerRow>();
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            var audit=BookWriting.ReadAudit(r.IsDBNull(10)?null:r.GetString(10)).Audit;
            rows.Add(new(r.GetInt32(0),r.GetInt32(1),r.IsDBNull(2)?"Produto sem cadastro":r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:r.GetInt32(4),
                r.IsDBNull(5)?null:r.GetDecimal(5),r.IsDBNull(6)?null:r.GetDecimal(6),r.IsDBNull(7)?null:r.GetDecimal(7),r.IsDBNull(8)?null:r.GetDecimal(8),
                r.IsDBNull(9)?null:r.GetDecimal(9),audit.Changes.Count>0,audit.Changes.LastOrDefault()?.AppliedAt));
        }
        return rows;
    }
}
