using System.Data;

namespace Lmc.Web;

public sealed record NextBook(int ProductId,string Date,string? LastBookDate);
public sealed partial class Repository
{
    public async Task<NextBook> NextBook(short branch,int? product,DateOnly fallback,CancellationToken ct)
    {
        if(branch<=0 || product is <=0)throw new CorrectionException(400,"Escolha uma filial e um combustível válidos.");
        await using var c=await Open(ct);
        using var cmd=Command(c,"""
            SELECT p.ID_PRODUTOS,CONVERT(char(10),MAX(l.DTACONTA),23)
            FROM dbo.PRODUTOS p LEFT JOIN dbo.LMC l ON l.ID_FILIAL=p.ID_FILIAL AND l.ID_PRODUTOS=p.ID_PRODUTOS
            WHERE p.ID_FILIAL=@branch AND p.ID_PRODUTOS=COALESCE(@product,
              (SELECT MIN(t.ID_PRODUTOS) FROM dbo.TANQUES t WHERE t.ID_FILIAL=@branch AND t.ATIVO=1))
            GROUP BY p.ID_PRODUTOS;
            """,branch);
        cmd.Parameters.Add("@product",SqlDbType.Int).Value=(object?)product??DBNull.Value;
        await using var r=await cmd.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct))throw new CorrectionException(404,"Nenhum combustível disponível para inserir o LMC.");
        var last=r.IsDBNull(1)?null:r.GetString(1);
        var next=last is null?fallback:Rules.Date(last)!.Value.AddDays(1);
        if(next.Year is <1900 or >2078)throw new CorrectionException(422,"A próxima data está fora do intervalo suportado.");
        return new(r.GetInt32(0),next.ToString("yyyy-MM-dd"),last);
    }
}
