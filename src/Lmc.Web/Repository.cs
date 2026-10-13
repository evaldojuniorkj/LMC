using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;

namespace Lmc.Web;

public sealed partial class Repository(IConfiguration configuration,IHttpContextAccessor? httpContext=null,BookAuditLog? auditLog=null,ILogger<Repository>? logger=null)
{
    private readonly BookAuditLog bookAuditLog=auditLog??new(BookAuditLog.DirectoryFor(configuration,Directory.GetCurrentDirectory()));
    internal string CurrentActor=>httpContext?.HttpContext?.User.Identity?.Name??Actor;
    private static readonly string NotesSql = Resource("Notes.sql");
    private static readonly string DetailSql = Resource("Detail.sql");
    private readonly string connectionString = configuration.GetConnectionString("AtxDados")
        ?? throw new InvalidOperationException("Configure ConnectionStrings:AtxDados.");

    private static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Lmc.Web.Queries.{name}")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private async Task<SqlConnection> Open(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static SqlCommand Command(SqlConnection connection, string sql, short branch = 0,
        short? db = null, int? id = null, SqlTransaction? transaction = null)
    {
        var command = new SqlCommand("SET NOCOUNT ON; SET LOCK_TIMEOUT 5000; SET QUOTED_IDENTIFIER ON;\n" + sql, connection, transaction)
        { CommandTimeout = 25 };
        command.Parameters.Add("@branch", SqlDbType.SmallInt).Value = branch;
        command.Parameters.Add("@db", SqlDbType.SmallInt).Value = (object?)db ?? DBNull.Value;
        command.Parameters.Add("@id", SqlDbType.Int).Value = (object?)id ?? DBNull.Value;
        return command;
    }

    private static T Value<T>(SqlDataReader reader, string name) => (T)reader[name];
    private static T? Optional<T>(SqlDataReader reader, string name) where T : struct => reader[name] is DBNull ? null : (T)reader[name];
    private static string? Text(SqlDataReader reader, string name) => reader[name] is DBNull ? null : (string)reader[name];
    private static Note ReadNote(SqlDataReader r) => new(
        Value<int>(r,"Id"), Value<short>(r,"BranchId"), Value<short>(r,"DatabaseId"), Value<int>(r,"Number"),
        Value<string>(r,"Supplier"), Value<string>(r,"Products"), Text(r,"DocumentDate"), Text(r,"IssueFieldDate"),
        Text(r,"OperationalDate"), Optional<int>(r,"ShiftId"), Optional<int>(r,"ShiftNumber"), Text(r,"ShiftDate"),
        Optional<decimal>(r,"Volume"), Value<int>(r,"Parcels"), Value<int>(r,"Movements"), Value<bool>(r,"Inconsistent"), Text(r,"ReplicationDate"));

    public async Task<object> Bootstrap(CancellationToken ct)
    {
        await using var c = await Open(ct);
        using var cmd = Command(c, """
            SELECT DISTINCT e.ID_FILIAL, COALESCE(RTRIM(f.NOMEFILIAL), 'Filial ' + CONVERT(varchar(10),e.ID_FILIAL)) AS Name,
                (SELECT CONVERT(char(10),MAX(l.DTACONTA),23) FROM dbo.LMC l WHERE l.ID_FILIAL=e.ID_FILIAL) AS LastBookDate
            FROM dbo.LMCENTRADATANQUES e
            LEFT JOIN dbo.FILIAIS f ON f.ID_FILIAL=e.ID_FILIAL
            ORDER BY e.ID_FILIAL;
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var branches = new List<object>();
        while (await r.ReadAsync(ct)) branches.Add(new { id = r.GetInt16(0), name = r.GetString(1), lastBookDate = r.IsDBNull(2) ? null : r.GetString(2) });
        return new { branches, database = c.Database, canApply = true, mode = "correcao_de_entradas",
            actor = CurrentActor, reasonLimit = 80,version=DeploymentSecurity.Version,
            deploymentLabel=configuration["Deployment:Label"]??"Desenvolvimento local",requiresLogin=configuration.GetValue<bool>("Deployment:NetworkAccess") };
    }

    public async Task<List<Note>> Notes(short branch, CancellationToken ct)
    {
        await using var c = await Open(ct);
        using var cmd = Command(c, NotesSql, branch);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var notes = new List<Note>();
        while (await r.ReadAsync(ct)) notes.Add(ReadNote(r));
        return notes;
    }

    public async Task<Detail?> Detail(short branch, short db, int id, CancellationToken ct)
    {
        await using var c = await Open(ct);
        // Somente SELECT: a transação mantém cabeçalho e detalhes estáveis durante a leitura.
        using var tx = c.BeginTransaction(IsolationLevel.Serializable);
        var result = await ReadDetail(c, tx, branch, db, id, ct);
        tx.Commit();
        return result;
    }

    internal static async Task<Detail?> ReadDetail(SqlConnection c, SqlTransaction tx, short branch, short db, int id, CancellationToken ct)
    {
        Note? note = null;
        using (var cmd = Command(c, NotesSql, branch, db, id, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) note = ReadNote(r);
        if (note is null) return null;
        var parcels = new List<Parcel>();
        var movements = new List<Movement>();
        var tanks = new List<AllocationTank>();
        using (var cmd = Command(c, DetailSql, branch, db, id, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) parcels.Add(new(Value<int>(r,"Id"),Value<int>(r,"ProductId"),Value<string>(r,"Product"),
                Value<short>(r,"TankId"),Value<string>(r,"Tank"),Optional<decimal>(r,"Volume"),Text(r,"OperationalDate"),Text(r,"ReplicationDate"),Text(r,"FiscalSeal")));
            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct)) movements.Add(new(Value<int>(r,"Id"),Optional<int>(r,"ShiftId"),Text(r,"OperationalDate"),Text(r,"ReplicationDate")));
            await r.NextResultAsync(ct);
            while(await r.ReadAsync(ct)) tanks.Add(new(Value<short>(r,"TankId"),Value<int>(r,"ProductId"),Value<string>(r,"Tank"),Value<bool>(r,"Active"),Text(r,"ActivatedAt")));
        }
        return new(note, parcels, movements,tanks);
    }

    public async Task<List<Shift>> Shifts(short branch, DateOnly date, CancellationToken ct)
    {
        await using var c = await Open(ct);
        using var cmd = Command(c, """
            SELECT ID_TURNOS, ID_FILIAL, CONVERT(char(10), DATATURNO, 23), CONVERT(int,TURNO)
            FROM dbo.TURNOS WHERE ID_FILIAL=@branch
              AND (TURNO=0 OR (DATATURNO>=@date AND DATATURNO<DATEADD(day,1,@date)))
            ORDER BY TURNO, ID_TURNOS;
            """, branch);
        cmd.Parameters.Add("@date", SqlDbType.Date).Value = date.ToDateTime(TimeOnly.MinValue);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<Shift>();
        while (await r.ReadAsync(ct)) rows.Add(new(r.GetInt32(0), r.GetInt16(1), r.GetString(2), r.GetInt32(3)));
        return rows;
    }

    public async Task<List<Book>> Books(short branch, DateOnly from, DateOnly? until, CancellationToken ct)
    {
        await using var c = await Open(ct);
        using var cmd = Command(c, """
            SELECT l.ID_LMC, l.ID_PRODUTOS, COALESCE(RTRIM(p.NOMEPRODUTO),'Produto sem cadastro'),
                   CONVERT(char(10),l.DTACONTA,23), l.NROCONTROLE
            FROM dbo.LMC l
            LEFT JOIN dbo.PRODUTOS p ON p.ID_FILIAL=l.ID_FILIAL AND p.ID_PRODUTOS=l.ID_PRODUTOS
            WHERE l.ID_FILIAL=@branch AND l.DTACONTA>=@date
              AND (@until IS NULL OR l.DTACONTA<DATEADD(day,1,@until))
            ORDER BY l.DTACONTA, l.ID_PRODUTOS, l.ID_LMC;
            """, branch);
        cmd.Parameters.Add("@date", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@until", SqlDbType.Date).Value = until is { } d ? d.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<Book>();
        while (await r.ReadAsync(ct)) rows.Add(new(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt32(4)));
        return rows;
    }
}
