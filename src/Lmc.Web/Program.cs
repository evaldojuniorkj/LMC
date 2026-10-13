using System.Net;
using Lmc.Web;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions {Args=args,ContentRootPath=WindowsServiceHelpers.IsWindowsService()?AppContext.BaseDirectory:null});
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables();
DeploymentSecurity.LoadProtectedConfiguration(builder.Configuration);
if(builder.Configuration.GetValue<bool>("PrepareOperatorAccess"))
{Environment.ExitCode=OperatorPasswordStore.Prepare(builder.Configuration);return;}
if(builder.Configuration.GetValue<bool>("CheckInstallation"))
{Environment.ExitCode=await DeploymentSecurity.CheckInstallation(builder.Configuration);return;}
if(builder.Configuration.GetValue<bool>("PrepareServiceCertificate"))
{Environment.ExitCode=ServiceCertificate.Prepare(builder.Configuration);return;}
if(builder.Configuration.GetValue<bool>("CheckServiceHttps"))
{Environment.ExitCode=await ServiceCertificate.CheckHttps(builder.Configuration);return;}
var access=new DeploymentSecurity(builder.Configuration);
using var serviceCertificate=ServiceCertificate.Configure(builder);
builder.Services.AddWindowsService(options=>options.ServiceName="LMC");
access.ConfigureServices(builder.Services,builder.Configuration);
builder.Services.AddSingleton<Repository>();
builder.Services.AddSingleton(new BookAuditLog(BookAuditLog.DirectoryFor(builder.Configuration,builder.Environment.ContentRootPath)));
builder.Services.AddSingleton(new TankTableStore(TankTableStore.DirectoryFor(builder.Configuration,builder.Environment.ContentRootPath)));
builder.Services.AddAntiforgery(options => options.HeaderName = "X-LMC-CSRF");
var app = builder.Build();

app.Use(async (context, next) =>
{
    if (!access.Allows(context.Connection.RemoteIpAddress))
    { context.Response.StatusCode = 403; return; }
    if(access.NetworkAccess&&!context.Request.IsHttps)
    {context.Response.StatusCode=426;return;}
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (CorrectionException ex)
    {
        await Results.Json(new { message=ex.Message },statusCode:ex.Status).ExecuteAsync(context);
    }
    catch (SqlException ex)
    {
        app.Logger.LogError(ex, "Falha na consulta ao SQL Server");
        await Results.Problem("Não foi possível concluir a operação no banco. Se estava aplicando uma correção, consulte o histórico ou repita a mesma solicitação.", statusCode:503).ExecuteAsync(context);
    }
});
app.UseAuthentication();
app.UseRateLimiter();
app.Use(async(context,next)=>
{
    var path=context.Request.Path;
    if(access.NetworkAccess&&context.User.Identity?.IsAuthenticated!=true&&path.StartsWithSegments("/api")
        &&path!="/api/session"&&path!="/api/login")
    {await Results.Json(new {message="Entre para acessar o LMC."},statusCode:401).ExecuteAsync(context);return;}
    await next();
});
access.MapEndpoints(app);
app.MapTankTables();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/context", (Repository repo, CancellationToken ct) => repo.Bootstrap(ct));
app.MapGet("/api/security", (HttpContext context, IAntiforgery csrf) => Results.Ok(new { token=csrf.GetAndStoreTokens(context).RequestToken }));
app.MapGet("/api/notes", async (short branch, string? search, string? from, string? until,
    string? dateField, string? status, string? sort, int? threshold, int? page, Repository repo, CancellationToken ct) =>
{
    var minDays = threshold ?? 5;
    var currentPage = page ?? 1;
    if (branch <= 0 || minDays is < 1 or > 365 || currentPage < 1 || search?.Length > 120)
        return Results.BadRequest(new { message = "Confira filial, busca, página e intervalo de atenção (1 a 365 dias)." });
    if ((!string.IsNullOrEmpty(from) && Rules.Date(from) is null) || (!string.IsNullOrEmpty(until) && Rules.Date(until) is null)
        || (Rules.Date(from) is { } f && Rules.Date(until) is { } u && f > u))
        return Results.BadRequest(new { message = "Informe um período válido." });
    if (dateField is not (null or "any" or "operational" or "document" or "issue")
        || status is not (null or "all" or "attention" or "intervalo" or "data_anterior" or "vinculos")
        || sort is not (null or "priority" or "recent"))
        return Results.BadRequest(new { message = "Filtro desconhecido." });
    var notes = await repo.Notes(branch, ct);
    IEnumerable<Note> filtered = notes;
    if (!string.IsNullOrWhiteSpace(search))
    {
        var term = search.Trim();
        filtered = filtered.Where(n => n.Number.ToString().Contains(term, StringComparison.OrdinalIgnoreCase)
            || n.Supplier.Contains(term, StringComparison.OrdinalIgnoreCase) || n.Products.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
    bool InPeriod(string? value) => Rules.Date(value) is { } d
        && (Rules.Date(from) is not { } a || d >= a) && (Rules.Date(until) is not { } b || d <= b);
    if (!string.IsNullOrEmpty(from) || !string.IsNullOrEmpty(until))
        filtered = filtered.Where(n => (dateField ?? "any") switch {
            "operational" => InPeriod(n.OperationalDate), "document" => InPeriod(n.DocumentDate),
            "issue" => InPeriod(n.IssueFieldDate), _ => InPeriod(n.DocumentDate) || InPeriod(n.IssueFieldDate) || InPeriod(n.OperationalDate)
        });
    var scoped = filtered.ToArray();
    var summary = new { total = scoped.Length, attention = scoped.Count(n => n.Alerts(minDays).Count > 0),
        delayed = scoped.Count(n => n.GapDays >= minDays), earlier = scoped.Count(n => n.GapDays < 0) };
    if (status == "attention") filtered = scoped.Where(n => n.Alerts(minDays).Count > 0);
    else if (status is not (null or "all")) filtered = scoped.Where(n => n.Alerts(minDays).Contains(status));
    var ordered = sort == "recent"
        ? filtered.OrderByDescending(n => n.OperationalDate).ThenByDescending(n => n.Id).ThenBy(n => n.DatabaseId)
        : filtered.OrderByDescending(n => n.Alerts(minDays).Count > 0).ThenByDescending(n => Math.Abs(n.GapDays ?? 0))
            .ThenByDescending(n => n.OperationalDate).ThenByDescending(n => n.Id).ThenBy(n => n.DatabaseId);
    var rows = ordered.ToArray();
    const int pageSize = 25;
    currentPage = Math.Min(currentPage, Math.Max(1,(rows.Length + pageSize - 1)/pageSize));
    return Results.Ok(new { summary, total = rows.Length, page = currentPage, pageSize,
        items = rows.Skip((currentPage-1)*pageSize).Take(pageSize).Select(n => new { note = n, alerts = n.Alerts(minDays) }),
        updatedAt = DateTimeOffset.UtcNow });
});

app.MapGet("/api/notes/{branch:int}/{db:int}/{id:int}", async (short branch, short db, int id, Repository repo, CancellationToken ct) =>
    await repo.Detail(branch, db, id, ct) is { } detail ? Results.Ok(detail) : Results.NotFound(new { message = "Nota não localizada nesta filial e origem." }));

app.MapGet("/api/shifts", async (short branch, string date, Repository repo, CancellationToken ct) =>
    Rules.Date(date) is { } d && d.Year is >= 1900 and <= 2078
        ? Results.Ok(await repo.Shifts(branch, d, ct)) : Results.BadRequest(new { message = "Informe uma data entre 1900 e 2078." }));

app.MapPost("/api/notes/{branch:int}/{db:int}/{id:int}/preview", async (short branch, short db, int id,
    PreviewRequest input, Repository repo, CancellationToken ct) =>
{
    if (Rules.Date(input.OperationalDate) is not { } target || target.Year is < 1900 or > 2078)
        return Results.BadRequest(new { message = "Informe uma data operacional válida entre 1900 e 2078." });
    if (CorrectionAudit.NormalizeReason(input.Reason).Length > 80)
        return Results.BadRequest(new { message = "A observação opcional deve ter no máximo 80 caracteres." });
    var detail = await repo.Detail(branch, db, id, ct);
    if (detail is null) return Results.NotFound(new { message = "Nota não localizada." });
    if (input.Version != detail.Version)
        return Results.Conflict(new { message = "Os dados da nota mudaram. Reabra a revisão para gerar outra prévia." });
    if (detail.Note.Inconsistent || detail.Note.OperationalDate is null)
        return Results.UnprocessableEntity(new { message = "Há divergências entre a nota e seus movimentos. Confira os vínculos antes de simular a troca." });
    var shift = (await repo.Shifts(branch, target, ct)).SingleOrDefault(s => s.Id == input.ShiftId);
    if (shift is null) return Results.BadRequest(new { message = "Escolha o turno geral da filial ou um turno da data operacional selecionada." });
    var allocations=AllocationRules.Validate(detail,input.OperationalDate,input.Allocations);
    if (detail.Note.OperationalDate == input.OperationalDate && detail.Note.ShiftId == input.ShiftId && allocations is null)
        return Results.BadRequest(new { message = "Altere a data, o turno ou a distribuição dos volumes." });
    IReadOnlyList<VolumeEffect> effects;
    try { effects = AllocationRules.Effects(detail, input.OperationalDate,allocations); }
    catch (ArgumentException ex) { return Results.UnprocessableEntity(new { message = ex.Message }); }
    var source = Rules.Date(detail.Note.OperationalDate)!.Value;
    var productIds = detail.Parcels.Select(p => p.ProductId).ToHashSet();
    var assessment=await repo.Assess(branch,db,id,target,input.ShiftId,ct);
    var first=assessment.BookReceipts.Select(r=>Rules.Date(r.BookDate)!.Value).Append(source).Append(target).Min();
    var books = await repo.Books(branch, first, null, ct);
    // Confere também a capacidade do campo de histórico antes de oferecer a aplicação.
    var validationRequest=new ApplyRequest(input.OperationalDate,input.ShiftId,input.Reason,input.Version,Guid.NewGuid(),true,allocations);
    _=CorrectionAudit.Encode(detail,validationRequest,repo.CurrentActor,first.ToString("yyyy-MM-dd"));
    return Results.Ok(new Preview("simulacao", assessment.Blockers.Count==0, CorrectionAudit.NormalizeReason(input.Reason), detail.Note.OperationalDate,
        input.OperationalDate, detail.Note.ShiftId, shift, effects,
        books.Where(b => productIds.Contains(b.ProductId)).ToArray(),
        assessment.Blockers, detail.Version,assessment.BookReceipts,allocations));
});

app.MapPost("/api/notes/{branch:int}/{db:int}/{id:int}/apply",async (short branch,short db,int id,
    ApplyRequest input,HttpContext context,IAntiforgery csrf,Repository repo,CancellationToken ct) =>
{
    try { await csrf.ValidateRequestAsync(context); }
    catch(AntiforgeryValidationException) { return Results.Json(new {message="A sessão de confirmação expirou. Atualize a página antes de aplicar."},statusCode:403); }
    return Results.Ok(await repo.Apply(branch,db,id,input,ct));
});

app.MapGet("/api/notes/{branch:int}/{db:int}/{id:int}/history",(short branch,short db,int id,Repository repo,CancellationToken ct)=>repo.History(branch,db,id,ct));
app.MapGet("/api/review-queue",async(short branch,Repository repo,CancellationToken ct)=>
    new { items=await repo.History(branch,null,null,ct), resolutionAvailable=false });

app.MapGet("/api/books", async (short branch, string date, Repository repo, CancellationToken ct) =>
    Rules.Date(date) is { } d && d.Year is >= 1900 and <= 2078
        ? Results.Ok(await repo.Books(branch,d,d,ct)) : Results.BadRequest(new { message = "Informe uma data válida." }));

app.MapGet("/api/lmc/day",async (short branch,string date,Repository repo,CancellationToken ct)=>
    branch>0 && Rules.Date(date) is {Year:>=1900 and <=2078} d
        ? Results.Ok(await repo.Day(branch,d,ct)) : Results.BadRequest(new {message="Informe filial e data válidas."}));

app.MapPost("/api/lmc/balance",async (BalanceRequest input,Repository repo,CancellationToken ct)=>
{
    if(input.Branch<=0 || Rules.Date(input.Date) is not {Year:>=1900 and <=2078} d || input.Tanks is null || input.Tanks.Count>100 || input.Tanks.Any(x=>x is null))
        return Results.BadRequest(new {message="Informe filial, data e medições válidas."});
    var day=await repo.Day(input.Branch,d,ct);
    var product=day.Products.SingleOrDefault(x=>x.Id==input.ProductId);
    if(product is null) return Results.NotFound(new {message="Combustível não localizado nesta data."});
    var tanks=day.Tanks.Where(x=>x.ProductId==input.ProductId).ToArray();
    if(tanks.GroupBy(x=>x.TankId).Any(g=>g.Count()>1)) return Results.UnprocessableEntity(new {message="Há tanques repetidos nos livros do dia. Confira a duplicidade."});
    try {return Results.Ok(LmcConference.Calculate(input.Tanks,tanks,product.Receipts,product.Sales,product.CanCalculate));}
    catch(ArgumentException ex) {return Results.BadRequest(new {message=ex.Message});}
});

app.MapGet("/api/lmc/next",async(short branch,int? product,string fallback,Repository repo,CancellationToken ct)=>
    Rules.Date(fallback) is {Year:>=1900 and <=2078} d?Results.Ok(await repo.NextBook(branch,product,d,ct)):Results.BadRequest(new {message="Informe o mês de referência."}));
app.MapGet("/api/lmc/pending",(short branch,Repository repo,CancellationToken ct)=>repo.PendingBooks(branch,ct));
app.MapGet("/api/lmc/prepare",async(short branch,string date,int product,Repository repo,CancellationToken ct)=>
    branch>0 && product>0 && Rules.Date(date) is {Year:>=1900 and <=2078} d
        ? Results.Ok(await repo.PrepareBook(branch,d,product,ct)) : Results.BadRequest(new {message="Informe filial, data e combustível válidos."}));
app.MapGet("/api/lmc/ledger",async(short branch,string from,string until,Repository repo,CancellationToken ct)=>
    branch>0 && Rules.Date(from) is {Year:>=1900 and <=2078} a && Rules.Date(until) is {Year:>=1900 and <=2078} b && b>=a && b.DayNumber-a.DayNumber<=366
        ? Results.Ok(await repo.Ledger(branch,a,b,ct)) : Results.BadRequest(new {message="Escolha um período válido de até um ano."}));
app.MapPost("/api/lmc/preview",(BookPreviewRequest input,Repository repo,CancellationToken ct)=>repo.PreviewBook(input,ct));
app.MapPost("/api/lmc/save",async(BookSaveRequest input,HttpContext context,IAntiforgery csrf,Repository repo,CancellationToken ct)=>
{
    try {await csrf.ValidateRequestAsync(context);}
    catch(AntiforgeryValidationException) {return Results.Json(new {message="A sessão expirou. Atualize a conferência antes de gravar."},statusCode:403);}
    return Results.Ok(await repo.SaveBook(input,ct));
});

app.MapGet("/api/lmc/deletion",(short branch,int id,Repository repo,CancellationToken ct)=>repo.PrepareBookDeletion(branch,id,ct));
app.MapPost("/api/lmc/deletion-batch",(BookBatchSelection input,Repository repo,CancellationToken ct)=>repo.PrepareBookBatchDeletion(input,ct));
app.MapPost("/api/lmc/delete-batch",async(BookBatchDeleteRequest input,HttpContext context,IAntiforgery csrf,Repository repo,CancellationToken ct)=>
{
    try {await csrf.ValidateRequestAsync(context);}
    catch(AntiforgeryValidationException) {return Results.Json(new {message="A sessão expirou. Atualize a confirmação antes de excluir."},statusCode:403);}
    return Results.Ok(await repo.DeleteBookBatch(input,ct));
});
app.MapPost("/api/lmc/delete",async(BookDeleteRequest input,HttpContext context,IAntiforgery csrf,Repository repo,CancellationToken ct)=>
{
    try {await csrf.ValidateRequestAsync(context);}
    catch(AntiforgeryValidationException) {return Results.Json(new {message="A sessão expirou. Atualize a confirmação antes de excluir."},statusCode:403);}
    return Results.Ok(await repo.DeleteBook(input,ct));
});

app.Run();
