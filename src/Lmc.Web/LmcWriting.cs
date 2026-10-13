using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Lmc.Web;

public sealed record BookHeader(int Id,int ProductId,string Date,int? Number,decimal? SalesValue,decimal? AccumulatedValue,string? CreatedAt,string? Observation,string? ReplicatedAt);
public sealed record StoredPump(int Id,int BookId,short PumpId,short? TankId,decimal? Opening,decimal? Closing,decimal? Calibration,decimal? Price,decimal? Sales);
public sealed record StoredTank(int Id,int BookId,short TankId,decimal? Opening,decimal? Closing);
public sealed record StoredReceipt(int Id,int BookId,short TankId,int? Number,decimal? Volume,string? DocumentDate,string? SupplierTaxId);
public sealed record PumpMapping(short PumpId,short TankId,bool TankActive,string? ActivatedAt);
public sealed record ShiftCounter(int Id,short PumpId,int ShiftId,int ShiftNumber,decimal? Opening,decimal? Closing,decimal? Calibration,decimal? Price);
public sealed record BookPumpCutoff(decimal AutomationOpening,decimal AutomationClosing,decimal ShiftOpening,decimal ShiftClosing,string FirstAt,string LastAt)
{
    public bool AutomationAmbiguous {get;init;}
    public decimal[] AutomationCandidates {get;init;}=[];
}
public sealed record BookPump(short PumpId,short TankId,decimal Opening,decimal Closing,decimal Calibration,decimal Price,decimal Sales,string Source)
{
    public BookPumpCutoff? Cutoff {get;init;}
    public string OpeningSource {get;init;}="Automação";
    public decimal? SavedOpening {get;init;}
}
public sealed record BookPumpInput(short PumpId,decimal? Closing,decimal? Calibration,string? CounterSource=null);
public sealed record BookPumpChange(short PumpId,decimal PreviousClosing,decimal PreviousCalibration,decimal Closing,decimal Calibration,
    decimal? PreviousOpening=null,decimal? Opening=null,string? CounterSource=null,BookPumpCutoff? Cutoff=null);
public sealed record BookTank(short TankId,decimal? Capacity,decimal? PreviousClosing,bool Inherited);
public sealed record BookDocument(int ParcelId,short DatabaseId,int DocumentId,int Number,short TankId,string DocumentDate,string SupplierTaxId,decimal Volume);
public sealed record BookChange(Guid OperationId,string AppliedAt,string Actor,string Fingerprint,int BookId,string Mode,string BeforeRows,string? BeforeObservation,
    IReadOnlyList<PhysicalInput> Measurements,IReadOnlyList<BookDocument> Documents,IReadOnlyList<int> FollowingBooks,string MeasurementMethod,IReadOnlyList<BookPumpChange>? PumpChanges=null,string? Observation=null);
public sealed record BookAudit(IReadOnlyList<BookChange> Changes);
public sealed record BookHistoryItem(string AppliedAt,string Actor,string Mode);
public sealed record BookPreparation(short Branch,string Date,int ProductId,string Product,int? BookId,int Number,string Version,
    IReadOnlyList<BookTank> Tanks,IReadOnlyList<BookPump> Pumps,IReadOnlyList<BookDocument> Documents,
    IReadOnlyList<string> Blockers,IReadOnlyList<string> Warnings,IReadOnlyList<Book> FollowingBooks,
    string Observation,IReadOnlyList<BookHistoryItem> History,IReadOnlyList<PhysicalInput>? SavedMeasurements=null,string? MeasurementMethod=null,
    decimal MonthSalesBefore=0,decimal MonthValueBefore=0)
{
    public IReadOnlyList<BookMissingPeriod> MissingPeriods {get;init;}=[];
}
public sealed record BookPreviewRequest(short Branch,string Date,int ProductId,string Version,IReadOnlyList<PhysicalInput> Tanks,string? Observation,string? MeasurementMethod,IReadOnlyList<BookPumpInput>? Pumps=null);
public sealed record BookSaveRequest(short Branch,string Date,int ProductId,string Version,IReadOnlyList<PhysicalInput> Tanks,string? Observation,string? MeasurementMethod,Guid OperationId,IReadOnlyList<BookPumpInput>? Pumps=null);
public sealed record BookPreview(bool CanSave,Balance? Balance,decimal SalesValue,IReadOnlyList<string> Blockers,IReadOnlyList<string> Warnings,string Version,int? BookId,int Number);
public sealed record BookSaved(int BookId,string Date,int ProductId,string Mode,bool AlreadyApplied,Guid OperationId,IReadOnlyList<Book> FollowingBooks)
{
    public string? AuditWarning {get;init;}
}

public static class BookWriting
{
    public const string AuditMarker="\n\n[LMCWEB-AUDIT-V1]\n";
    public static string Hash(object value)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    // Compatibilidade de leitura com livros antigos. Novas gravações não geram auditoria em OBS.
    public static (string Observation,BookAudit Audit) ReadAudit(string? value)
    {
        value??="";
        var index=value.IndexOf(AuditMarker,StringComparison.Ordinal);
        if(index<0) return (value,new BookAudit([]));
        try
        {
            var audit=JsonSerializer.Deserialize<BookAudit>(value[(index+AuditMarker.Length)..]);
            if(audit?.Changes is null || audit.Changes.Any(x=>x is null)) throw new JsonException();
            return (value[..index],audit);
        }
        catch(JsonException) {throw new CorrectionException(422,"O histórico deste LMC não pôde ser lido. Confira o registro antes de refazê-lo.");}
    }
    public static string ObservationForSave(string? observation)
    {
        if(observation?.Length>2000 || (observation??"").Contains("[LMCWEB-AUDIT-V1]",StringComparison.Ordinal))
            throw new CorrectionException(400,"A observação deve conter apenas o texto informado, com até 2.000 caracteres, sem o bloco antigo de auditoria.");
        return observation?.Trim()??"";
    }
    public static void ValidateInput(BookPreviewRequest request)
    {
        if(request.Branch<=0 || request.ProductId<=0 || Rules.Date(request.Date) is not {Year:>=1900 and <=2078}
            || request.Tanks is null || request.Tanks.Count is <1 or >100 || request.Tanks.Any(x=>x is null)
            || string.IsNullOrEmpty(request.Version) || request.Version.Length!=64)
            throw new CorrectionException(400,"Confira filial, data, combustível e medições.");
        _=ObservationForSave(request.Observation);
        if(request.MeasurementMethod is not ("manual" or "eletronica"))
            throw new CorrectionException(400,"Informe se as medições físicas foram manuais ou eletrônicas.");
        if(request.Pumps is not null && (request.Pumps.Count>1000 || request.Pumps.Any(x=>x is null)))
            throw new CorrectionException(400,"Confira os encerrantes e as aferições dos bicos.");
    }

    public static IReadOnlyList<BookPump> ApplyPumpInputs(BookPreparation p,IReadOnlyList<BookPumpInput>? inputs,List<string> blockers)
    {
        if(inputs is null)
        {
            foreach(var b in p.Pumps.Where(x=>x.Cutoff is not null))
                blockers.Add($"Bico {b.PumpId}: confira a diferença entre automação e turnos e escolha o encerrante de fechamento.");
            return p.Pumps;
        }
        if(inputs.Count!=p.Pumps.Count || inputs.Select(x=>x.PumpId).Distinct().Count()!=inputs.Count
            || !inputs.Select(x=>x.PumpId).Order().SequenceEqual(p.Pumps.Select(x=>x.PumpId).Order()))
        {blockers.Add("Informe exatamente um encerrante final e uma aferição para cada bico deste combustível.");return p.Pumps;}
        return p.Pumps.Select(b=>
        {
            var edited=inputs.Single(x=>x.PumpId==b.PumpId);
            var opening=b.Opening;
            if(b.Cutoff is not null)
            {
                if(b.Cutoff.AutomationAmbiguous && edited.CounterSource=="automation" &&
                    (!edited.Closing.HasValue || !b.Cutoff.AutomationCandidates.Contains(edited.Closing.Value)))
                    blockers.Add($"Bico {b.PumpId}: escolha um dos fechamentos registrados pela automação.");
                if(edited.CounterSource is not ("automation" or "shifts"))
                    blockers.Add($"Bico {b.PumpId}: confira a diferença entre automação e turnos e escolha o encerrante de fechamento.");
            }
            else if(edited.CounterSource is not null)
                blockers.Add($"Bico {b.PumpId}: a escolha de origem não corresponde à conferência atual. Atualize o livro.");
            if(edited.Closing is null || edited.Calibration is null)
            {blockers.Add($"Bico {b.PumpId}: preencha o encerrante final e a aferição (zero quando não houver).");return b;}
            var closing=edited.Closing.Value;var calibration=edited.Calibration.Value;
            if(closing<0 || closing>1000000000 || calibration<0 || calibration>1000000000
                || decimal.Round(closing,4)!=closing || decimal.Round(calibration,4)!=calibration)
                blockers.Add($"Bico {b.PumpId}: informe valores positivos ou zero, com até quatro casas decimais.");
            if(closing<opening)blockers.Add($"Bico {b.PumpId}: o encerrante final deve ser igual ou maior que o inicial.");
            if(calibration>closing-opening)blockers.Add($"Bico {b.PumpId}: a aferição não pode exceder o volume movimentado.");
            return b with {Opening=opening,Closing=closing,Calibration=calibration,Sales=closing-opening-calibration};
        }).ToArray();
    }

    public static BookPreview Preview(BookPreparation p,BookPreviewRequest input)
    {
        ValidateInput(input);
        if(p.Version!=input.Version) throw new CorrectionException(409,"As fontes ou o livro mudaram. Atualize a conferência antes de salvar.");
        var blockers=p.Blockers.ToList();var warnings=p.Warnings.ToList();
        var pumps=ApplyPumpInputs(p,input.Pumps,blockers);
        if(input.Tanks.Count!=p.Tanks.Count || input.Tanks.Select(x=>x.TankId).Distinct().Count()!=input.Tanks.Count
            || !input.Tanks.Select(x=>x.TankId).Order().SequenceEqual(p.Tanks.Select(x=>x.TankId).Order()))
            blockers.Add("Informe exatamente uma medição para cada tanque deste combustível.");
        foreach(var t in input.Tanks)
        {
            var expected=p.Tanks.SingleOrDefault(x=>x.TankId==t.TankId);
            if(t.PreviousClosing is null || t.Closing is null) blockers.Add($"Tanque {t.TankId}: informe os dois fechamentos físicos.");
            if(t.PreviousClosing is <0 or >1000000000 || t.Closing is <0 or >1000000000) blockers.Add($"Tanque {t.TankId}: medição inválida.");
            if(expected?.Capacity is >0 && (t.PreviousClosing>expected.Capacity || t.Closing>expected.Capacity)) blockers.Add($"Tanque {t.TankId}: a medição excede a capacidade cadastrada.");
            if(expected is {Inherited:true} && t.PreviousClosing!=expected.PreviousClosing) blockers.Add($"Tanque {t.TankId}: a abertura deve ser o fechamento já gravado no dia anterior.");
            if(expected is not null && t.PreviousClosing is { } opening
                && opening+p.Documents.Where(x=>x.TankId==t.TankId).Sum(x=>x.Volume)-pumps.Where(x=>x.TankId==t.TankId).Sum(x=>x.Sales)<0)
                blockers.Add($"Tanque {t.TankId}: o estoque escritural ficaria negativo. Confira a abertura, as entradas e os bicos vinculados.");
            if(t.PreviousClosing.HasValue && decimal.Round(t.PreviousClosing.Value,4)!=t.PreviousClosing || t.Closing.HasValue && decimal.Round(t.Closing.Value,4)!=t.Closing)
                blockers.Add("Informe no máximo quatro casas decimais nas medições.");
        }
        Balance? balance=null;
        if(blockers.Count==0)
        {
            var tanks=p.Tanks.Select(t=>new DayTank(p.ProductId,p.Product,t.TankId,p.BookId,t.Capacity,null,null)).ToArray();
            balance=LmcConference.Calculate(input.Tanks,tanks,p.Documents.Sum(x=>x.Volume),pumps.Sum(x=>x.Sales),true);
            if(balance.BookStock<0) blockers.Add("O estoque escritural ficaria negativo. Confira a abertura, as entradas e os encerrantes.");
            if((balance.AboveReference==true || balance.BookStock==0 && balance.Difference!=0) && string.IsNullOrWhiteSpace(input.Observation))
                blockers.Add("Registre nas observações a justificativa da variação do estoque antes de salvar este LMC.");
            warnings.AddRange(balance.Alerts);
        }
        return new(blockers.Count==0,balance,decimal.Round(pumps.Sum(x=>x.Sales*x.Price),2,MidpointRounding.AwayFromZero),
            blockers.Distinct().ToArray(),warnings.Distinct().ToArray(),p.Version,p.BookId,p.Number);
    }
}

public sealed partial class Repository
{
    private sealed record WriteContext(BookPreparation Preparation,List<BookHeader> Headers,List<StoredPump> Pumps,
        List<StoredTank> Tanks,List<StoredReceipt> Receipts,LmcDay Day);

    public async Task<BookSaved> SaveBook(BookSaveRequest request,CancellationToken ct)
    {
        await using var c=await Open(ct);using var tx=c.BeginTransaction(IsolationLevel.Serializable);
        BookAuditAttempt? attempt=null;
        try
        {
            var result=await SaveBookInTransaction(c,tx,request,ct,change=>
            {
                try {attempt=bookAuditLog.Begin(c.DataSource,c.Database,request.Branch,request.Date,request.ProductId,change);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
                {
                    logger?.LogError(ex,"Não foi possível preparar o arquivo de auditoria do LMC");
                    throw new CorrectionException(503,"Não foi possível gravar o arquivo de auditoria. A operação não foi confirmada. Confira o espaço e a permissão da pasta de logs.");
                }
            });
            await tx.CommitAsync(ct);
            return RecordBookAuditOutcome(attempt,"committed")?result:result with
                {AuditWarning="O LMC foi salvo, mas o log ficou com confirmação pendente. Confira a pasta de auditoria; não é necessário salvar o livro novamente."};
        }
        catch
        {
            var outcome="unknown";
            try {await tx.RollbackAsync(CancellationToken.None);outcome="rolled_back";}catch(Exception ex) when(ex is SqlException or InvalidOperationException) { }
            RecordBookAuditOutcome(attempt,outcome);
            throw;
        }
    }

    private bool RecordBookAuditOutcome(BookAuditAttempt? attempt,string outcome)
    {
        if(attempt is null)return true;
        try {bookAuditLog.Complete(attempt,outcome);return true;}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {logger?.LogError(ex,"Não foi possível confirmar o resultado {Outcome} no arquivo de auditoria do LMC {BookId}",outcome,attempt.Record.Change.BookId);return false;}
    }

    internal async Task<BookSaved> SaveBookInTransaction(SqlConnection c,SqlTransaction tx,BookSaveRequest request,CancellationToken ct,Action<BookChange>? recordAudit=null)
    {
        var input=new BookPreviewRequest(request.Branch,request.Date,request.ProductId,request.Version,request.Tanks,request.Observation,request.MeasurementMethod,request.Pumps);
        BookWriting.ValidateInput(input);
        if(request.OperationId==Guid.Empty) throw new CorrectionException(400,"Identificador de gravação inválido.");
        // Trava transacional, sem criar índice, constraint ou qualquer objeto no banco.
        using(var cmd=Command(c,"""
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=N'LMCWEB-WRITE',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000;
            SELECT @result;
            """,transaction:tx))
            if(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))<0) throw new CorrectionException(409,"Outro LMC está sendo gravado. Tente novamente após concluir essa operação.");
        // A versão lida sob a trava rejeita novas tentativas de uma gravação já aplicada.
        // Não é necessário procurar ou guardar identificadores nas observações do livro.
        var context=await ReadWriteContext(c,tx,request.Branch,Rules.Date(request.Date)!.Value,request.ProductId,ct);
        var p=context.Preparation;var preview=BookWriting.Preview(p,input);
        if(!preview.CanSave) throw new CorrectionException(422,string.Join(" ",preview.Blockers));
        var effectivePumps=BookWriting.ApplyPumpInputs(p,request.Pumps,[]);
        var original=context.Headers.SingleOrDefault(x=>x.Id==p.BookId);
        var beforeRows=recordAudit is null?"":await BookRows(c,tx,request.Branch,p.BookId??0,ct);
        var protectedBefore=await BookProtected(c,tx,request.Branch,p.BookId??0,ct);
        var sourceBefore=BookWriting.Hash(new {context.Day.Archive,context.Day.Receipts,context.Day.Readings});
        var mode=p.BookId is null?"inserido":"refeito";
        int bookId;
        decimal? accumulated=null;
        if(original?.AccumulatedValue is not null)
        {
            var previousMonthBooks=context.Headers.Where(x=>x.Date.StartsWith(request.Date[..7],StringComparison.Ordinal)&&string.CompareOrdinal(x.Date,request.Date)<0).ToArray();
            if(previousMonthBooks.Any(x=>x.SalesValue is null)) throw new CorrectionException(422,"Há valores de venda ausentes no mês; confira o acumulado antes de refazer.");
            accumulated=previousMonthBooks.Sum(x=>x.SalesValue!.Value)+preview.SalesValue;
        }
        using(var cmd=Command(c,p.BookId is null?"""
            INSERT dbo.LMC(ID_FILIAL,ID_PRODUTOS,NROCONTROLE,DTACONTA,VLRVENDASDIA,HORALMC,VLRACUMULADO,OBS)
            VALUES(@branch,@product,@number,@date,@value,GETDATE(),@accumulated,'');
            SELECT CONVERT(int,SCOPE_IDENTITY());
            """:"""
            UPDATE dbo.LMC SET VLRVENDASDIA=@value,VLRACUMULADO=@accumulated,NROCONTROLE=@number
            WHERE ID_FILIAL=@branch AND ID_LMC=@id;
            SELECT @id;
            """,request.Branch,id:p.BookId,transaction:tx))
        {
            cmd.Parameters.Add("@product",SqlDbType.Int).Value=request.ProductId;
            cmd.Parameters.Add("@number",SqlDbType.Int).Value=p.Number;
            cmd.Parameters.Add("@date",SqlDbType.SmallDateTime).Value=Rules.Date(request.Date)!.Value.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@value",SqlDbType.Real).Value=(float)preview.SalesValue;
            cmd.Parameters.Add("@accumulated",SqlDbType.Real).Value=accumulated is null?DBNull.Value:(float)accumulated.Value;
            bookId=Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        // As tabelas de detalhes não possuem referências de outros objetos nesta base. O pai é mantido ao refazer.
        using(var cmd=Command(c,"""
            DELETE FROM dbo.LMCCOMPROVANTES WHERE ID_FILIAL=@branch AND ID_LMC=@id;
            DELETE FROM dbo.LMCBICOS WHERE ID_FILIAL=@branch AND ID_LMC=@id;
            DELETE FROM dbo.LMCTANQUES WHERE ID_FILIAL=@branch AND ID_LMC=@id;
            """,request.Branch,id:bookId,transaction:tx)) await cmd.ExecuteNonQueryAsync(ct);
        foreach(var t in request.Tanks)
        {
            using var cmd=Command(c,"INSERT dbo.LMCTANQUES(ID_FILIAL,ID_LMC,ID_TANQUES,QTDETANQUE,QTDECONCILIACAO) VALUES(@branch,@id,@tank,@opening,@closing);",request.Branch,id:bookId,transaction:tx);
            cmd.Parameters.Add("@tank",SqlDbType.SmallInt).Value=t.TankId;
            cmd.Parameters.Add("@opening",SqlDbType.Real).Value=(float)t.PreviousClosing!.Value;
            cmd.Parameters.Add("@closing",SqlDbType.Real).Value=(float)t.Closing!.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach(var b in effectivePumps)
        {
            using var cmd=Command(c,"""
                INSERT dbo.LMCBICOS(ID_FILIAL,ID_LMC,ID_BICOS,ID_TANQUES,ENCERRANTEABERTURA,ENCERRANTEFECHAMENTO,AFERICAO,PPL,VENDAS)
                VALUES(@branch,@id,@pump,@tank,@opening,@closing,@calibration,@price,@sales);
                """,request.Branch,id:bookId,transaction:tx);
            cmd.Parameters.Add("@pump",SqlDbType.SmallInt).Value=b.PumpId;cmd.Parameters.Add("@tank",SqlDbType.SmallInt).Value=b.TankId;
            cmd.Parameters.Add("@opening",SqlDbType.Float).Value=(double)b.Opening;cmd.Parameters.Add("@closing",SqlDbType.Float).Value=(double)b.Closing;
            cmd.Parameters.Add("@calibration",SqlDbType.Money).Value=b.Calibration;cmd.Parameters.Add("@price",SqlDbType.Real).Value=(float)b.Price;
            cmd.Parameters.Add("@sales",SqlDbType.Money).Value=b.Sales;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach(var r in p.Documents)
        {
            using var cmd=Command(c,"""
                INSERT dbo.LMCCOMPROVANTES(ID_FILIAL,ID_LMC,ID_TANQUES,NROCOMPROVANTE,VOLUMERECEBIDO,DTAENTRADA,CNPJ_FORNECEDOR)
                VALUES(@branch,@id,@tank,@number,@volume,@date,@tax);
                """,request.Branch,id:bookId,transaction:tx);
            cmd.Parameters.Add("@tank",SqlDbType.SmallInt).Value=r.TankId;cmd.Parameters.Add("@number",SqlDbType.Int).Value=r.Number;
            cmd.Parameters.Add("@volume",SqlDbType.Real).Value=(float)r.Volume;
            cmd.Parameters.Add("@date",SqlDbType.SmallDateTime).Value=Rules.Date(r.DocumentDate)!.Value.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@tax",SqlDbType.VarChar,20).Value=r.SupplierTaxId;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var observation=BookWriting.ObservationForSave(request.Observation);
        using(var cmd=Command(c,"UPDATE dbo.LMC SET OBS=@observation WHERE ID_FILIAL=@branch AND ID_LMC=@id; SELECT CONVERT(varchar(max),OBS) FROM dbo.LMC WHERE ID_FILIAL=@branch AND ID_LMC=@id;",request.Branch,id:bookId,transaction:tx))
        {
            cmd.Parameters.Add("@observation",SqlDbType.NVarChar,-1).Value=observation;
            if((string?)await cmd.ExecuteScalarAsync(ct)!=observation) throw new CorrectionException(422,"A observação contém caracteres incompatíveis com o banco. A gravação foi desfeita.");
        }
        if(protectedBefore!=await BookProtected(c,tx,request.Branch,bookId,ct))
            throw new CorrectionException(422,"As rotinas do banco alteraram dados fora deste livro. A gravação foi desfeita para preservar os demais registros.");
        // Após gravar, só as fontes do dia precisam ser relidas. O livro gravado é
        // conferido por VerifyBookWrite e os demais registros por BookProtected.
        var after=await ReadDay(c,tx,request.Branch,Rules.Date(request.Date)!.Value,ct);
        if(sourceBefore!=BookWriting.Hash(new {after.Archive,after.Receipts,after.Readings}))
            throw new CorrectionException(422,"As fontes de entrada ou medição mudaram durante a gravação. A operação foi desfeita.");
        await VerifyBookWrite(c,tx,p with {Pumps=effectivePumps},request,bookId,preview.SalesValue,accumulated,ct);
        recordAudit?.Invoke(new BookChange(request.OperationId,DateTimeOffset.Now.ToString("O",CultureInfo.InvariantCulture),CurrentActor,
            BookWriting.Hash(request),bookId,mode,beforeRows,original?.Observation,request.Tanks,p.Documents,
            p.FollowingBooks.Select(x=>x.Id).ToArray(),request.MeasurementMethod!,
            effectivePumps.Select(b=>(Before:p.Pumps.Single(x=>x.PumpId==b.PumpId),After:b))
                .Where(x=>x.Before.Cutoff is not null || (x.Before.SavedOpening??x.Before.Opening)!=x.After.Opening || x.Before.Closing!=x.After.Closing || x.Before.Calibration!=x.After.Calibration)
                .Select(x=>new BookPumpChange(x.After.PumpId,x.Before.Closing,x.Before.Calibration,x.After.Closing,x.After.Calibration,
                    x.Before.SavedOpening??x.Before.Opening,x.After.Opening,request.Pumps?.Single(b=>b.PumpId==x.After.PumpId).CounterSource,x.Before.Cutoff)).ToArray(),observation));
        return new(bookId,request.Date,request.ProductId,mode,false,request.OperationId,p.FollowingBooks);
    }

    private static async Task VerifyBookWrite(SqlConnection c,SqlTransaction tx,BookPreparation p,BookSaveRequest input,int id,decimal salesValue,decimal? accumulated,CancellationToken ct)
    {
        var failed=false;
        using var cmd=Command(c,"""
            SELECT ID_PRODUTOS,DTACONTA,NROCONTROLE,VLRVENDASDIA,VLRACUMULADO FROM dbo.LMC WHERE ID_FILIAL=@branch AND ID_LMC=@id;
            SELECT ID_TANQUES,QTDETANQUE,QTDECONCILIACAO FROM dbo.LMCTANQUES WHERE ID_FILIAL=@branch AND ID_LMC=@id ORDER BY ID_TANQUES;
            SELECT ID_BICOS,ID_TANQUES,ENCERRANTEABERTURA,ENCERRANTEFECHAMENTO,AFERICAO,PPL,VENDAS FROM dbo.LMCBICOS WHERE ID_FILIAL=@branch AND ID_LMC=@id ORDER BY ID_BICOS;
            SELECT ID_TANQUES,NROCOMPROVANTE,VOLUMERECEBIDO,DTAENTRADA,RTRIM(CNPJ_FORNECEDOR) FROM dbo.LMCCOMPROVANTES WHERE ID_FILIAL=@branch AND ID_LMC=@id ORDER BY ID_LMCCOMPROVANTES;
            """,input.Branch,id:id,transaction:tx);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct) || r.GetInt32(0)!=input.ProductId || DateOnly.FromDateTime(r.GetDateTime(1))!=Rules.Date(input.Date)
            || r.GetInt32(2)!=p.Number || r.GetFloat(3)!=(float)salesValue || (accumulated is null?!r.IsDBNull(4):r.IsDBNull(4)||r.GetFloat(4)!=(float)accumulated)) failed=true;
        await r.NextResultAsync(ct);var index=0;var tanks=input.Tanks.OrderBy(x=>x.TankId).ToArray();
        while(await r.ReadAsync(ct))
        {
            if(index>=tanks.Length) {failed=true;continue;}
            var t=tanks[index++];if(r.GetInt16(0)!=t.TankId || r.GetFloat(1)!=(float)t.PreviousClosing! || r.GetFloat(2)!=(float)t.Closing!) failed=true;
        }
        if(index!=tanks.Length) failed=true;
        await r.NextResultAsync(ct);index=0;var pumps=p.Pumps.OrderBy(x=>x.PumpId).ToArray();
        while(await r.ReadAsync(ct))
        {
            if(index>=pumps.Length) {failed=true;continue;}
            var b=pumps[index++];if(r.GetInt16(0)!=b.PumpId||r.GetInt16(1)!=b.TankId||r.GetDouble(2)!=(double)b.Opening||r.GetDouble(3)!=(double)b.Closing||r.GetDecimal(4)!=b.Calibration||r.GetFloat(5)!=(float)b.Price||r.GetDecimal(6)!=b.Sales) failed=true;
        }
        if(index!=pumps.Length) failed=true;
        await r.NextResultAsync(ct);index=0;
        while(await r.ReadAsync(ct))
        {
            if(index>=p.Documents.Count) {failed=true;continue;}
            var doc=p.Documents[index++];if(r.GetInt16(0)!=doc.TankId||r.GetInt32(1)!=doc.Number||r.GetFloat(2)!=(float)doc.Volume||DateOnly.FromDateTime(r.GetDateTime(3))!=Rules.Date(doc.DocumentDate)||r.GetString(4)!=doc.SupplierTaxId) failed=true;
        }
        if(index!=p.Documents.Count) failed=true;
        if(failed) throw new CorrectionException(422,"Os valores gravados diferem da conferência. A operação foi desfeita.");
    }

    internal static async Task<string> BookRows(SqlConnection c,SqlTransaction tx,short branch,int id,CancellationToken ct)
    {
        using var cmd=Command(c,"""
            SELECT
             (SELECT ID_LMC,ID_FILIAL,ID_PRODUTOS,NROCONTROLE,DTACONTA,VLRVENDASDIA,HORALMC,VLRACUMULADO,DATAREPL FROM dbo.LMC WHERE ID_FILIAL=@branch AND ID_LMC=@id FOR XML RAW,BINARY BASE64),
             (SELECT * FROM dbo.LMCBICOS WHERE ID_FILIAL=@branch AND ID_LMC=@id ORDER BY ID_LMCBICOS FOR XML RAW,BINARY BASE64),
             (SELECT * FROM dbo.LMCTANQUES WHERE ID_FILIAL=@branch AND ID_LMC=@id ORDER BY ID_LMCTANQUES FOR XML RAW,BINARY BASE64),
             (SELECT * FROM dbo.LMCCOMPROVANTES WHERE ID_FILIAL=@branch AND ID_LMC=@id ORDER BY ID_LMCCOMPROVANTES FOR XML RAW,BINARY BASE64);
            """,branch,id:id,transaction:tx);
        await using var r=await cmd.ExecuteReaderAsync(ct);await r.ReadAsync(ct);
        return JsonSerializer.Serialize(Enumerable.Range(0,r.FieldCount).Select(i=>r.IsDBNull(i)?"":r.GetString(i)).ToArray());
    }

    internal static async Task<string> BookProtected(SqlConnection c,SqlTransaction tx,short branch,int id,CancellationToken ct)
        // HASHBYTES aceita entradas maiores que 8.000 bytes a partir do SQL Server 2016.
        =>await ReadBookProtection(c,tx,branch,id,int.Parse(c.ServerVersion.Split('.')[0],CultureInfo.InvariantCulture)>=13,ct);

    internal static async Task<string> ReadBookProtection(SqlConnection c,SqlTransaction tx,short branch,int id,bool hashOnServer,CancellationToken ct)
    {
        using var cmd=Command(c,Resource(hashOnServer?"LmcProtected.sql":"LmcProtectedLegacy.sql"),branch,id:id,transaction:tx);
        await using var r=await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess,ct);await r.ReadAsync(ct);
        // Cada tabela retorna SHA-256 do XML integral, calculado no SQL Server.
        // Instâncias antigas continuam compatíveis, sem truncar o XML nem alterar o banco.
        var hashes=new string[r.FieldCount];
        for(var i=0;i<hashes.Length;i++)
        {
            ct.ThrowIfCancellationRequested();
            hashes[i]=Convert.ToHexString(hashOnServer?r.GetFieldValue<byte[]>(i):
                SHA256.HashData(Encoding.Unicode.GetBytes(r.IsDBNull(i)?"":r.GetString(i))));
        }
        return string.Concat(hashes);
    }

    public async Task<BookPreparation> PrepareBook(short branch,DateOnly date,int product,CancellationToken ct)
    {
        await using var c=await Open(ct);using var tx=c.BeginTransaction(IsolationLevel.Serializable);
        var context=await ReadWriteContext(c,tx,branch,date,product,ct);
        var preparation=context.Preparation with {MissingPeriods=await ReadBookMissingPeriods(c,tx,branch,product,date,ct)};
        tx.Commit();return preparation;
    }

    internal static async Task<BookPreparation> PrepareBookInTransaction(SqlConnection c,SqlTransaction tx,short branch,DateOnly date,int product,CancellationToken ct)
        =>(await ReadWriteContext(c,tx,branch,date,product,ct)).Preparation;

    public async Task<BookPreview> PreviewBook(BookPreviewRequest input,CancellationToken ct)
    {
        BookWriting.ValidateInput(input);
        return BookWriting.Preview(await PrepareBook(input.Branch,Rules.Date(input.Date)!.Value,input.ProductId,ct),input);
    }

    private static async Task<WriteContext> ReadWriteContext(SqlConnection c,SqlTransaction tx,short branch,DateOnly date,int product,CancellationToken ct)
    {
        var headers=new List<BookHeader>();var storedPumps=new List<StoredPump>();var storedTanks=new List<StoredTank>();var storedReceipts=new List<StoredReceipt>();
        var mappings=new List<PumpMapping>();var shifts=new List<ShiftCounter>();var taxes=new Dictionary<int,string?>();
        var blockers=new List<string>();var warnings=new List<string>();var linked=new List<int>();
        bool supportedNumbering=false;int firstPage=2;decimal monthSales=0;
        using(var cmd=Command(c,Resource("LmcWriteSources.sql"),branch,transaction:tx))
        {
            cmd.Parameters.Add("@date",SqlDbType.Date).Value=date.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@product",SqlDbType.Int).Value=product;
            await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) headers.Add(new(Value<int>(r,"Id"),Value<int>(r,"ProductId"),Value<string>(r,"Date"),Optional<int>(r,"Number"),Optional<decimal>(r,"SalesValue"),Optional<decimal>(r,"AccumulatedValue"),Text(r,"CreatedAt"),Text(r,"Observation"),Text(r,"ReplicatedAt")));
            await r.NextResultAsync(ct);
            while(await r.ReadAsync(ct)) storedPumps.Add(new(Value<int>(r,"Id"),Value<int>(r,"BookId"),Value<short>(r,"PumpId"),Optional<short>(r,"TankId"),Optional<decimal>(r,"Opening"),Optional<decimal>(r,"Closing"),Optional<decimal>(r,"Calibration"),Optional<decimal>(r,"Price"),Optional<decimal>(r,"Sales")));
            await r.NextResultAsync(ct);
            while(await r.ReadAsync(ct)) storedTanks.Add(new(Value<int>(r,"Id"),Value<int>(r,"BookId"),Value<short>(r,"TankId"),Optional<decimal>(r,"Opening"),Optional<decimal>(r,"Closing")));
            await r.NextResultAsync(ct);
            while(await r.ReadAsync(ct)) storedReceipts.Add(new(Value<int>(r,"Id"),Value<int>(r,"BookId"),Value<short>(r,"TankId"),Optional<int>(r,"Number"),Optional<decimal>(r,"Volume"),Text(r,"DocumentDate"),Text(r,"SupplierTaxId")));
            await r.NextResultAsync(ct);
            while(await r.ReadAsync(ct)) mappings.Add(new(Value<short>(r,"PumpId"),Value<short>(r,"TankId"),Value<bool>(r,"TankActive"),Text(r,"ActivatedAt")));
            await r.NextResultAsync(ct);
            while(await r.ReadAsync(ct)) shifts.Add(new(Value<int>(r,"Id"),Value<short>(r,"PumpId"),Value<int>(r,"ShiftId"),Value<int>(r,"ShiftNumber"),Optional<decimal>(r,"Opening"),Optional<decimal>(r,"Closing"),Optional<decimal>(r,"Calibration"),Optional<decimal>(r,"Price")));
            await r.NextResultAsync(ct);
            while(await r.ReadAsync(ct)) taxes.Add(Value<int>(r,"ParcelId"),Text(r,"SupplierTaxId"));
            await r.NextResultAsync(ct);
            while(await r.ReadAsync(ct)) linked.Add(r.GetInt32(0));
            await r.NextResultAsync(ct);
            if(await r.ReadAsync(ct))
            {
                supportedNumbering=Value<int>(r,"ConfigCount")==1 && Optional<int>(r,"Sequential")==0 && Optional<int>(r,"SingleNumber")==0 && Optional<int>(r,"NumberType")==2;
                firstPage=Optional<int>(r,"FirstPage")??2;
            }
            await r.NextResultAsync(ct);
            if(await r.ReadAsync(ct)) monthSales=r.GetDecimal(0);
        }
        var day=await ReadDay(c,tx,branch,date,ct);
        var name=day.Products.FirstOrDefault(x=>x.Id==product)?.Name??throw new CorrectionException(404,"Combustível não localizado nesta data.");
        var today=date.ToString("yyyy-MM-dd");var yesterday=date.AddDays(-1).ToString("yyyy-MM-dd");
        var existing=headers.Where(x=>x.Date==today).ToArray();var previous=headers.Where(x=>x.Date==yesterday).ToArray();
        if(existing.Length>1 || previous.Length>1) blockers.Add("Há livros duplicados para este combustível no dia ou no dia anterior.");
        if(linked.Count>0) blockers.Add("Há livro vinculado a envio externo neste dia ou no período posterior. Confira o envio antes de gravar.");
        if(date>=DateOnly.FromDateTime(DateTime.Today)) blockers.Add("Aguarde o encerramento do dia para gravar o LMC.");
        if(previous.Length==0) warnings.Add("Não há livro no dia anterior. Informe o fechamento físico anterior com base na medição e confira a lacuna.");
        if(existing.Length==0 && !supportedNumbering) blockers.Add("A numeração configurada nesta base ainda não é suportada para inserção.");
        var number=existing.FirstOrDefault()?.Number??(date.Day+firstPage-1);
        if(number<=0) blockers.Add("A numeração do livro está inválida.");
        var nextBooks=headers.Where(x=>string.CompareOrdinal(x.Date,today)>0).Select(x=>new Book(x.Id,x.ProductId,name,x.Date,x.Number)).ToArray();
        if(nextBooks.Length>0) warnings.Add($"Há {nextBooks.Length} livro(s) posteriores. Confira a abertura do dia seguinte após salvar; os fechamentos físicos posteriores serão preservados.");
        var previousAudit=BookWriting.ReadAudit(previous.FirstOrDefault()?.Observation).Audit;
        var dayTanks=day.Tanks.Where(x=>x.ProductId==product).ToArray();
        var invalidReadings=day.Readings.Count(r=>r.InvalidVolume is not null&&dayTanks.Any(t=>t.TankId==r.TankId));
        if(invalidReadings>0)warnings.Add($"Há {invalidReadings} leitura(s) de tanque com volume inválido na base. Elas não podem ser usadas no fechamento. Confira os registros em Leituras disponíveis por tanque ou informe a medição real.");
        if(dayTanks.Length==0 || dayTanks.GroupBy(x=>x.TankId).Any(g=>g.Count()>1)) blockers.Add("Tanques ausentes ou repetidos para este combustível.");
        if(existing.Length>0 && dayTanks.Any(x=>x.BookId is null)) blockers.Add("O cadastro atual de tanques difere do livro. Confira o vínculo histórico antes de refazer.");
        var tanks=dayTanks.GroupBy(x=>x.TankId).Select(g=>
        {
            var t=g.First();var prior=storedTanks.Where(x=>x.BookId==previous.FirstOrDefault()?.Id && x.TankId==t.TankId).ToArray();
            var recorded=prior.Length==1?prior[0].Closing:null;
            var inherited=recorded.HasValue;
            var audited=previousAudit.Changes.LastOrDefault()?.Measurements.SingleOrDefault(x=>x.TankId==t.TankId)?.Closing;
            if(audited.HasValue && LmcConference.Differs(recorded,audited)) warnings.Add($"Tanque {t.TankId}: o fechamento anterior mudou desde a última gravação pela interface. A abertura usa o valor atualmente salvo.");
            return new BookTank(t.TankId,t.Capacity,recorded,inherited);
        }).ToArray();
        if(existing.Length==0 && tanks.Any(x=>!x.Inherited)) warnings.Add("Há tanque sem fechamento salvo no dia anterior. Confira o livro anterior e informe a abertura ausente.");
        var documents=new List<BookDocument>();
        foreach(var r in day.Receipts.Where(x=>x.ProductId==product&&!x.Canceled))
        {
            var tax=taxes.GetValueOrDefault(r.Id)?.Trim();
            if(!r.Usable || r.Number is null || r.DocumentId is null || r.DocumentDate is null || r.Volume is null || r.Volume<0
                || !tanks.Any(x=>x.TankId==r.TankId) || string.IsNullOrEmpty(tax) || tax.Length>20)
            {blockers.Add($"Entrada {r.Number?.ToString()??r.Id.ToString()}: confira vínculos, emissão, fornecedor, tanque e volume.");continue;}
            documents.Add(new(r.Id,r.DatabaseId,r.DocumentId.Value,r.Number.Value,r.TankId,r.DocumentDate,tax,r.Volume.Value));
        }
        var pumps=new List<BookPump>();
        var current=storedPumps.Where(x=>x.BookId==existing.FirstOrDefault()?.Id).ToArray();
        var expected=existing.Length>0 ? current.Select(x=>new PumpMapping(x.PumpId,x.TankId??0,true,null)).ToArray()
            : mappings.Where(x=>x.TankActive && (x.ActivatedAt is null || string.CompareOrdinal(x.ActivatedAt,today)<=0)).ToArray();
        if(expected.Length==0 || expected.GroupBy(x=>x.PumpId).Any(g=>g.Count()>1)) blockers.Add("Bicos ausentes ou repetidos para este combustível.");
        foreach(var map in expected.GroupBy(x=>x.PumpId).Select(g=>g.First()))
        {
            if(!tanks.Any(x=>x.TankId==map.TankId)) {blockers.Add($"Bico {map.PumpId}: tanque não localizado.");continue;}
            var a=day.Archive.SingleOrDefault(x=>x.PumpId==map.PumpId);
            var old=current.FirstOrDefault(x=>x.PumpId==map.PumpId);
            var prior=storedPumps.Where(x=>x.BookId==previous.FirstOrDefault()?.Id&&x.PumpId==map.PumpId).ToArray();
            var turns=shifts.Where(x=>x.PumpId==map.PumpId).OrderBy(x=>x.ShiftNumber).ToArray();
            decimal? opening=a?.Opening,closing=a?.Closing,calibration=null,price=null;string source="CONSOLEARQUIVO + turnos";
            var openingSource="Automação · sem fechamento no LMC anterior";
            var inheritsOpening=prior.Length==1 && prior[0].Closing is >=0 and <=1000000000;
            if(inheritsOpening)
            {
                opening=prior[0].Closing;
                openingSource=$"Fechamento do LMC de {date.AddDays(-1):dd/MM/yyyy}";
                if(a is not null && LmcConference.Differs(opening,a.Opening))
                    warnings.Add($"Bico {map.PumpId}: a abertura da automação difere do fechamento do LMC anterior. Foi mantido o fechamento anterior como abertura.");
            }
            else if(prior.Length>0)
                blockers.Add($"Bico {map.PumpId}: o fechamento do LMC anterior está ausente, inválido ou repetido. Confira o livro anterior.");
            else if(old is null && a is not null)
                warnings.Add($"Bico {map.PumpId}: sem fechamento registrado no LMC do dia anterior. A abertura vem da automação e deve ser conferida.");
            BookPumpCutoff? cutoff=null;
            if(old is not null)
            {
                opening=inheritsOpening?prior[0].Closing:old.Opening;closing=old.Closing;calibration=old.Calibration;price=old.Price;source="Valores salvos no LMC";
                if(!inheritsOpening)openingSource="Abertura salva neste LMC · sem fechamento no LMC anterior";
                if(inheritsOpening && opening!=old.Opening)
                    warnings.Add($"Bico {map.PumpId}: ao refazer, a abertura usará o fechamento já salvo no LMC de {date.AddDays(-1):dd/MM/yyyy}, mantendo a continuidade.");
                if(a is not null && (LmcConference.Differs(a.Opening,old.Opening)||LmcConference.Differs(a.Closing,old.Closing))) warnings.Add($"Bico {map.PumpId}: os encerrantes salvos no LMC diferem da automação. Confira os ajustes e a continuidade com o dia seguinte.");
            }
            else if(turns.Length>0 && turns.Select(x=>x.ShiftNumber).Distinct().Count()==turns.Length
                && turns.All(x=>x.Opening.HasValue&&x.Closing.HasValue&&x.Calibration.HasValue&&x.Calibration>=0&&x.Price>0&&x.Closing-x.Opening-x.Calibration>=0)
                && turns.Select(x=>x.Price).Distinct().Count()==1
                && turns.Zip(turns.Skip(1)).All(x=>!LmcConference.Differs(x.First.Closing,x.Second.Opening)))
            {
                calibration=turns.Sum(x=>x.Calibration!.Value);price=turns[0].Price;
                if(a is {Ambiguous:true} && inheritsOpening)
                {
                    closing=turns[^1].Closing;
                    cutoff=new(a.Opening??opening!.Value,a.Closing??closing!.Value,turns[0].Opening!.Value,closing!.Value,a.FirstAt,a.LastAt){AutomationAmbiguous=true,AutomationCandidates=a.ClosingCandidates};
                    warnings.Add($"Bico {map.PumpId}: a automação possui encerrantes conflitantes. Escolha o fechamento da automação ou dos turnos para continuar; a abertura permanece a do LMC anterior.");
                }
                if(a is {Ambiguous:false,Opening:not null,Closing:not null} && LmcConference.Differs(closing,turns[^1].Closing))
                    cutoff=new(a.Opening.Value,a.Closing.Value,turns[0].Opening!.Value,turns[^1].Closing!.Value,a.FirstAt,a.LastAt);
                if(LmcConference.Differs(opening,turns[0].Opening))
                    warnings.Add($"Bico {map.PumpId}: a abertura dos turnos difere da abertura fixa deste LMC. A escolha de fechamento não altera a abertura.");
            }
            if(old is null && a is null && turns.Length>0 && turns.All(x=>x.Opening.HasValue&&x.Opening==x.Closing&&x.Calibration==0)
                && turns.Select(x=>x.Opening).Distinct().Count()==1)
            {
                if(prior.Length==1&&prior[0].Closing is >=0 and <=1000000000&&!LmcConference.Differs(prior[0].Closing,turns[0].Opening))
                {opening=closing=prior[0].Closing;source="Turnos sem movimento, conferidos com o fechamento anterior";}
            }
            if(old is null && a?.Ambiguous==true && cutoff?.AutomationAmbiguous!=true || opening is null || closing is null || closing<opening || calibration is null || calibration<0
                || price is null || price<=0 || closing-opening-calibration<0)
            {blockers.Add($"Bico {map.PumpId}: faltam encerrantes, aferição ou preço confiáveis para gravar.");continue;}
            pumps.Add(new(map.PumpId,map.TankId,opening.Value,closing.Value,calibration.Value,price.Value,closing.Value-opening.Value-calibration.Value,source){Cutoff=cutoff,OpeningSource=openingSource,SavedOpening=old?.Opening});
        }
        foreach(var a in day.Archive.Where(x=>x.ProductId==product))
            if(!expected.Any(x=>x.PumpId==a.PumpId)) blockers.Add($"Bico {a.PumpId}: há automação sem vínculo no conjunto de bicos do livro.");
        if(day.Alerts.Count>0) blockers.AddRange(day.Alerts);
        var audit=BookWriting.ReadAudit(existing.FirstOrDefault()?.Observation);
        var auditedMeasurements=audit.Audit.Changes.LastOrDefault()?.Measurements;
        var currentTanks=storedTanks.Where(x=>x.BookId==existing.FirstOrDefault()?.Id).ToArray();
        var auditMatches=auditedMeasurements is not null && auditedMeasurements.Count==currentTanks.Length && auditedMeasurements.All(m=>
            currentTanks.Count(t=>t.TankId==m.TankId)==1 && !LmcConference.Differs(currentTanks.First(t=>t.TankId==m.TankId).Opening,m.PreviousClosing)
            && !LmcConference.Differs(currentTanks.First(t=>t.TankId==m.TankId).Closing,m.Closing));
        if(auditedMeasurements is not null && !auditMatches)
        {
            warnings.Add("As quantidades deste livro mudaram fora da interface. Os valores atuais salvos foram carregados; confira a origem das medições antes de gravar.");
        }
        // O livro salvo é a fonte dos valores ao reabrir, inclusive quando não tem histórico desta interface.
        var savedMeasurements=existing.Length==1?currentTanks.Select(t=>new PhysicalInput(t.TankId,t.Opening,t.Closing)).ToArray():null;
        var version=BookWriting.Hash(new {branch,date=today,product,headers,storedPumps,storedTanks,storedReceipts,mappings,shifts,taxes,linked,supportedNumbering,firstPage,day});
        var prep=new BookPreparation(branch,today,product,name,existing.FirstOrDefault()?.Id,number,version,tanks,pumps,documents,
            blockers.Distinct().ToArray(),warnings.Distinct().ToArray(),nextBooks,audit.Observation,
            audit.Audit.Changes.TakeLast(5).Select(x=>new BookHistoryItem(x.AppliedAt,x.Actor,x.Mode)).ToArray(),
            savedMeasurements,auditMatches?audit.Audit.Changes.LastOrDefault()?.MeasurementMethod:null,monthSales,
            headers.Where(x=>x.Date.StartsWith(today[..7],StringComparison.Ordinal)&&string.CompareOrdinal(x.Date,today)<0).Sum(x=>x.SalesValue??0));
        return new(prep,headers,storedPumps,storedTanks,storedReceipts,day);
    }
}
