using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.SqlClient;

namespace Lmc.Web;

public sealed record CalibrationTank(short TankId,int ProductId,string Product,decimal? Capacity,bool Active);
public sealed partial class Repository
{
    // Do not persist the connection string or credentials in the catalog.
    public string TankTableScope
    {
        get
        {
            var sql=new SqlConnectionStringBuilder(connectionString);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.DataSource.ToLowerInvariant()+"\n"+sql.InitialCatalog.ToLowerInvariant())));
        }
    }
    public async Task<CalibrationTank[]> CalibrationTanks(short branch,CancellationToken ct)
    {
        await using var c=await Open(ct);
        using var cmd=Command(c,"""
            SELECT t.ID_TANQUES,t.ID_PRODUTOS,COALESCE(RTRIM(p.NOMEPRODUTO),'Produto sem cadastro'),
                CONVERT(decimal(19,4),t.CAPACIDADE),CONVERT(bit,CASE WHEN t.ATIVO=1 THEN 1 ELSE 0 END)
            FROM dbo.TANQUES t LEFT JOIN dbo.PRODUTOS p ON p.ID_FILIAL=t.ID_FILIAL AND p.ID_PRODUTOS=t.ID_PRODUTOS
            WHERE t.ID_FILIAL=@branch ORDER BY t.ID_TANQUES;
            """,branch);
        await using var r=await cmd.ExecuteReaderAsync(ct);var tanks=new List<CalibrationTank>();
        while(await r.ReadAsync(ct))tanks.Add(new(r.GetInt16(0),r.GetInt32(1),r.GetString(2),r.IsDBNull(3)?null:r.GetDecimal(3),r.GetBoolean(4)));
        return tanks.ToArray();
    }
}

public static class TankTableEndpoints
{
    public static void MapTankTables(this WebApplication app)
    {
        var group=app.MapGroup("/api/tank-tables");
        group.AddEndpointFilter(async(context,next)=>
        {
            try
            {
                if(context.HttpContext.Request.Method!="GET")
                    await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext);
                return await next(context);
            }
            catch(AntiforgeryValidationException){return Results.Json(new{message="A sessão expirou. Atualize a página antes de salvar."},statusCode:403);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException)
            {
                app.Logger.LogError(ex,"Falha no arquivo de tabelas dos tanques");
                return Results.Json(new{message="Não foi possível acessar o arquivo das tabelas. Confira as permissões e o arquivo de configuração no servidor."},statusCode:503);
            }
        });
        group.MapGet("",async(short branch,Repository repo,TankTableStore store,CancellationToken ct)=>
        {
            if(branch<=0)return Results.BadRequest(new{message="Selecione a unidade."});
            var tanks=await repo.CalibrationTanks(branch,ct);var catalog=store.Read();
            return Results.Ok(new{catalog.Revision,catalog.Tables,tanks=tanks.Select(t=>new{
                t.TankId,t.ProductId,t.Product,t.Capacity,t.Active,
                tableId=catalog.Bindings.FirstOrDefault(b=>b.Scope==repo.TankTableScope && b.Branch==branch && b.TankId==t.TankId && b.ProductId==t.ProductId)?.TableId
            })});
        });
        group.MapGet("/references",(short branch,int product,Repository repo,TankTableStore store)=>
        {
            if(branch<=0 || product<=0)return Results.BadRequest(new{message="Selecione unidade e combustível."});
            var catalog=store.Read();
            var bindings=catalog.Bindings.Where(b=>b.Scope==repo.TankTableScope && b.Branch==branch && b.ProductId==product).ToArray();
            return Results.Ok(new{catalog.Revision,bindings=bindings.Select(b=>new{b.TankId,b.TableId}),tables=catalog.Tables.Where(t=>bindings.Any(b=>b.TableId==t.Id))});
        });
        // Read the bounded JSON body manually, before binding, so uploads cannot allocate unlimited memory.
        group.MapPost("/preview",(Func<HttpContext,Task<IResult>>)(async context=>Results.Ok(TankCalibrationCsv.Parse(await ReadImport(context)))));
        group.MapPost("/import",async(HttpContext context,TankTableStore store)=>Results.Ok(store.Import(await ReadImport(context))));
        group.MapPost("/assign",async(short branch,TankTableSelection input,Repository repo,TankTableStore store,CancellationToken ct)=>
        {
            if(branch<=0)return Results.BadRequest(new{message="Selecione a unidade."});
            var tanks=await repo.CalibrationTanks(branch,ct);
            if(!tanks.Any(t=>t.TankId==input.TankId && t.ProductId==input.ProductId))
                return Results.Conflict(new{message="O cadastro do tanque mudou. Atualize esta tela antes de vincular."});
            store.Assign(repo.TankTableScope,branch,input);return Results.Ok(new{message="Tabela do tanque salva."});
        });
    }
    private static async Task<CalibrationImport> ReadImport(HttpContext context)
    {
        const int limit=TankCalibrationCsv.MaxBytes*6+4096; // JSON may escape each character as six bytes.
        var size=context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if(size is {IsReadOnly:false})size.MaxRequestBodySize=limit;
        if(context.Request.ContentLength>limit)throw new CorrectionException(413,"O CSV deve ter no máximo 512 KB.");
        try
        {
            return await context.Request.ReadFromJsonAsync<CalibrationImport>(context.RequestAborted)
                ?? throw new CorrectionException(400,"Escolha um CSV para importar.");
        }
        catch(JsonException){throw new CorrectionException(400,"Não foi possível ler os dados da importação.");}
    }
}
