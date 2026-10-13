using Lmc.Web;

var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FALHOU: " + label);
    Console.WriteLine("OK: " + label); checks++;
}
void Reject(Action action, string label)
{
    try { action(); } catch (ArgumentException) { Check(true,label); return; }
    Check(false,label);
}

var note = new Note(541442,17581,17581,1337740,"Fornecedor","Gasolina","2026-08-13","2026-08-14",
    "2026-09-04",1,0,"2024-03-25",6000m,3,1,false,null);
var detail = new Detail(note,
    [new(1,2,"Gasolina",6,"6",2000m,"2026-09-04",null), new(2,2,"Gasolina",6,"6",1000m,"2026-09-04",null),
     new(3,2,"Gasolina",5,"5",3000m,"2026-09-04",null)],
    [new(9,1,"2026-09-04",null)]);
Check(note.GapDays == 21, "intervalo de 21 dias reproduz o caso de referência");
Check(note.Alerts(5).SequenceEqual(new[]{"intervalo"}), "turno geral não gera falso alerta por ter data cadastral diferente");
Check((note with { ShiftNumber=1 }).Alerts(5).Contains("turno_data"), "turno comum em outra data exige conferência");
Check((note with { OperationalDate="2026-08-13" }).Alerts(5).Contains("data_anterior"), "intervalo negativo exige conferência");
Check((note with { OperationalDate="2026-08-15" }).Alerts(5).Count==0, "transporte de um dia não é marcado como erro");
Check((note with { IssueFieldDate=null }).Alerts(5).Contains("data_ausente"), "data ausente não vira diferença zero");
var effects = Rules.Effects(detail,"2026-08-14");
Check(effects.Count==4 && effects.Sum(e=>e.Liters)==0m, "troca entre meses conserva volume por tanque");
Check(effects.Where(e=>e.Date=="2026-08-14").Sum(e=>e.Liters)==6000m, "soma parcelas sem multiplicar pelos movimentos");
Check(effects.Where(e=>e.Date=="2026-09-04").All(e=>e.Liters<0), "retira volume somente da origem");
Check(Rules.Effects(detail,"2026-09-04").Count==0, "troca apenas de turno preserva entradas diárias");
Reject(()=>Rules.Effects(detail with {Parcels=[detail.Parcels[0] with {Volume=null}]},"2026-08-14"), "volume ausente impede a simulação");
Reject(()=>Rules.Effects(detail with {Parcels=[detail.Parcels[0] with {OperationalDate="2026-09-05"}]},"2026-08-14"), "datas de parcelas divergentes impedem simulação enganosa");
Check(detail.Version != (detail with {Parcels=[detail.Parcels[0] with {Volume=2001m}]}).Version, "mudança de volume invalida a versão");
Check(detail.Version != (detail with {Movements=[new(9,2,"2026-09-04",null)]}).Version, "mudança de turno no movimento invalida a versão");
Check(Rules.Date("31/02/2026") is null && Rules.Date("2026-02-30") is null, "datas inválidas são rejeitadas");
LmcDayChecks.Run(Check);
BookWriteChecks.Rules(Check);
BookAuditLogChecks.Run(Check);
BookPumpChecks.Rules(Check);
AllocationChecks.Rules(Check);
BookDeleteChecks.Rules(Check);
BookContinuityChecks.Run(Check);
DeploymentChecks.Rules(Check);
TankTableChecks.Run(Check);
var deploymentArg=Array.IndexOf(args,"--deployment-http");
if(deploymentArg>=0)await DeploymentChecks.Http(args[deploymentArg+1],Check);
if(args.Contains("--delete-rollback")) await BookDeleteChecks.Rollback(Check);
if(args.Contains("--batch-rollback")) await BookBatchChecks.Rollback(Check);
if(args.Contains("--allocation-rollback")) await AllocationChecks.Rollback(Check);
if(args.Contains("--lmc-rollback")) await BookWriteChecks.Rollback(Check);
if(args.Contains("--pump-rollback")) await BookPumpChecks.Rollback(Check);
if(args.Contains("--pump-cutoff-rollback")) await BookPumpChecks.CutoffRollback(Check);
if(args.Contains("--pending-rollback")) await BookContinuityChecks.Rollback(Check);
if(args.Contains("--integration-rollback")) await CorrectionChecks.Run(Check);
var performanceArg=Array.IndexOf(args,"--save-performance-rollback");
if(performanceArg>=0)await BookPerformanceChecks.Rollback(short.Parse(args[performanceArg+1]),DateOnly.Parse(args[performanceArg+2]),int.Parse(args[performanceArg+3]),Check);
var sourceArg=Array.IndexOf(args,"--sources-rollback");
if(sourceArg>=0)await BookSourceChecks.Rollback(short.Parse(args[sourceArg+1]),DateOnly.Parse(args[sourceArg+2]),int.Parse(args[sourceArg+3]),args.Length>sourceArg+4?args[sourceArg+4]:null,Check);
Console.WriteLine($"{checks} verificações passaram. " + (args.Any(x=>x.EndsWith("-rollback"))?"Ensaios de gravação desfeitos por rollback.":deploymentArg>=0?"Consultas de leitura na cópia de desenvolvimento; nenhuma gravação.":"Nenhum acesso ao banco."));
