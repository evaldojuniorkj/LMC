using System.Data;
using Lmc.Web;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

static class AllocationChecks
{
    public static void Rules(Action<bool,string> check)
    {
        var n=new Note(1,1,1,123,"Fornecedor","Gasolina","2026-09-01","2026-09-02","2026-09-02",1,0,"2024-01-01",10000,1,1,false,null);
        var d=new Detail(n,[new(10,1,"Gasolina",1,"1",10000,n.OperationalDate,null)],[new(1,1,n.OperationalDate,null)],
            [new(1,1,"1",true,"2020-01-01"),new(2,1,"2",true,"2020-01-01"),new(3,2,"3",true,"2020-01-01"),new(4,1,"4",false,null)]);
        AllocationInput[] input=[new(10,1,1,6000),new(null,1,2,4000)];
        var result=AllocationRules.Validate(d,n.OperationalDate!,input);
        check(result?.Count==2,"redistribuição aceita dividir uma entrada entre dois tanques do mesmo combustível");
        var effects=AllocationRules.Effects(d,n.OperationalDate!,result);
        check(effects.Count==2&&effects.Single(e=>e.TankId==1).Liters==-4000&&effects.Single(e=>e.TankId==2).Liters==4000,"prévia mostra transferência de litros mesmo sem mudança de data ou turno");
        check(AllocationRules.Effects(d,"2026-09-03",result).Sum(e=>e.Liters)==0,"data e redistribuição combinadas conservam o volume");
        void Reject(AllocationInput[] values,int status,string label)
        {
            try{AllocationRules.Validate(d,n.OperationalDate!,values);check(false,label);}catch(CorrectionException e){check(e.Status==status,label);}
        }
        Reject([new(10,1,1,6000),new(null,1,2,5000)],422,"volume excedente é rejeitado");
        Reject([new(10,1,1,6000)],422,"volume faltante é rejeitado");
        Reject([new(10,1,1,6000),new(null,1,3,4000)],422,"tanque de outro combustível é rejeitado");
        Reject([new(10,1,1,6000),new(null,1,4,4000)],422,"tanque inativo não recebe redistribuição");
        Reject([new(10,1,1,6000),new(10,1,2,4000)],400,"parcela repetida é rejeitada");
        Reject([new(99,1,1,10000)],400,"parcela de outra nota é rejeitada");
        Reject([new(10,1,1,-1),new(null,1,2,10001)],400,"litros negativos são rejeitados");
        Reject([new(10,1,1,null)],400,"volume não informado é rejeitado");
        Reject([new(10,1,1,6000.00001m),new(null,1,2,3999.99999m)],400,"precisão não suportada é rejeitada");
        check(AllocationRules.Validate(d,n.OperationalDate!,[new(10,1,1,10000)]) is null,"distribuição idêntica não cria correção desnecessária");
        var two=d with {Parcels=[..d.Parcels,new(20,2,"Diesel",3,"3",1000,n.OperationalDate,null)]};
        try{AllocationRules.Validate(two,n.OperationalDate!,[new(10,1,1,9000),new(20,2,3,2000)]);check(false,"total por produto");}
        catch(CorrectionException ex){check(ex.Status==422,"conservar apenas o total geral não permite transferir litros entre combustíveis");}
        var audit=new AllocationRevision(d.Parcels,[d.Parcels[0] with {Volume=6000},new(11,1,"Gasolina",2,"2",4000,n.OperationalDate,null)]);
        var lines=AllocationRules.EncodeHistory(Guid.NewGuid(),audit);
        check(lines.All(x=>x.Length<=255)&&AllocationRules.DecodeHistory(lines.Reverse()).After.Sum(x=>x.Volume)==10000,"histórico preserva antes/depois usando somente o campo existente de 255 caracteres");
        try{AllocationRules.DecodeHistory(lines.Skip(1));check(false,"histórico incompleto");}
        catch(CorrectionException ex){check(ex.Status==422,"histórico parcial é identificado");}
        var request=new ApplyRequest(n.OperationalDate!,1,"",d.Version,Guid.NewGuid(),true,input);
        check(CorrectionAudit.Fingerprint(1,1,1,request)!=CorrectionAudit.Fingerprint(1,1,1,request with {Allocations=[new(10,1,1,5000),new(null,1,2,5000)]}),"assinatura da operação inclui os volumes por tanque");
    }

    public static async Task Rollback(Action<bool,string> check)
    {
        const string connection="Server=localhost;Database=ATXDADOS;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=5";
        var repo=new Repository(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:AtxDados",connection}}).Build());
        const short branch=17581,db=17581;const int note=560835;
        var original=await repo.Detail(branch,db,note,default)??throw new Exception("Nota de ensaio ausente.");
        await using var c=new SqlConnection(connection);await c.OpenAsync();
        var snapshot=await Snapshot(c,null);
        foreach(var fail in new[]{false,true})
        {
            using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
            {
                try
                {
                    var before=await Repository.ReadDetail(c,tx,branch,db,note,default)??throw new Exception();
                    var parcel=before.Parcels.Single();var total=parcel.Volume!.Value;
                    var other=before.AllocationTanks!.First(t=>t.ProductId==parcel.ProductId&&t.Active&&t.TankId!=parcel.TankId);
                    var request=new ApplyRequest(before.Note.OperationalDate!,before.Note.ShiftId!.Value,fail?"Ensaio de rollback 🚨":"",before.Version,Guid.NewGuid(),true,
                        [new(parcel.Id,parcel.ProductId,parcel.TankId,total-4000),new(null,parcel.ProductId,other.TankId,4000)]);
                    if(fail)
                    {
                        try{await repo.ApplyInTransaction(c,tx,branch,db,note,request,default);check(false,"falha posterior à distribuição");}
                        catch(CorrectionException ex){check(ex.Status==422&&ex.Message.Contains("caracteres"),"falha no histórico principal após redistribuir impede confirmação");}
                    }
                    else
                    {
                        var receipt=await repo.ApplyInTransaction(c,tx,branch,db,note,request,default);
                        check(receipt.SourceDate==receipt.TargetDate&&receipt.SourceShiftId==receipt.TargetShiftId,"redistribuição é gravada sem trocar data ou turno");
                        check(receipt.Allocation?.Before.Count==1&&receipt.Allocation.After.Count==2&&receipt.Allocation.After.Sum(p=>p.Volume)==total,"nova parcela mantém o total da nota e registra antes/depois");
                        check(receipt.Allocation!.Before[0].Id==parcel.Id&&receipt.Allocation.After.Any(p=>p.TankId==parcel.TankId&&p.Volume==total-4000),"histórico conserva o ID anterior da parcela recomposta");
                        var repeated=await repo.ApplyInTransaction(c,tx,branch,db,note,request,default);
                        check(repeated.AlreadyApplied&&repeated.Allocation?.After.Count==2,"repetição retorna a mesma correção e o histórico de volumes");
                        try{await repo.ApplyInTransaction(c,tx,branch,db,note,request with {Allocations=[new(parcel.Id,parcel.ProductId,parcel.TankId,total)]},default);check(false,"UUID reutilizado");}
                        catch(CorrectionException ex){check(ex.Status==409,"UUID reutilizado com outra distribuição é rejeitado");}
                        try{await repo.ApplyInTransaction(c,tx,branch,db,note,request with {OperationId=Guid.NewGuid()},default);check(false,"versão vencida");}
                        catch(CorrectionException ex){check(ex.Status==409,"distribuição com versão vencida é rejeitada");}
                        var after=await Repository.ReadDetail(c,tx,branch,db,note,default)??throw new Exception();
                        var second=request with {Version=after.Version,OperationId=Guid.NewGuid(),Allocations=after.Parcels.Select(p=>new AllocationInput(p.Id,p.ProductId,p.TankId,p.TankId==other.TankId?total:0)).ToArray()};
                        var merged=await repo.ApplyInTransaction(c,tx,branch,db,note,second,default);
                        check(merged.Allocation?.After.Count==1&&merged.Allocation.After[0].TankId==other.TankId&&merged.Allocation.After[0].Volume==total,"zero retira a parcela anterior e concentra o volume no tanque escolhido");
                        var current=await Repository.ReadDetail(c,tx,branch,db,note,default)??throw new Exception();
                        var nextDate=DateOnly.Parse(current.Note.OperationalDate!).AddDays(1).ToString("yyyy-MM-dd");
                        var dateOnly=new ApplyRequest(nextDate,current.Note.ShiftId!.Value,"",current.Version,Guid.NewGuid(),true);
                        var dated=await repo.ApplyInTransaction(c,tx,branch,db,note,dateOnly,default);
                        check(dated.Allocation is null&&dated.TargetDate==nextDate,"correção somente de data continua disponível sem redistribuir os litros");
                        current=await Repository.ReadDetail(c,tx,branch,db,note,default)??throw new Exception();
                        var combined=dateOnly with {OperationalDate=before.Note.OperationalDate!,Version=current.Version,OperationId=Guid.NewGuid(),
                            Allocations=[new(current.Parcels.Single().Id,parcel.ProductId,other.TankId,4000),new(null,parcel.ProductId,parcel.TankId,total-4000)]};
                        var combinedReceipt=await repo.ApplyInTransaction(c,tx,branch,db,note,combined,default);
                        check(combinedReceipt.TargetDate==before.Note.OperationalDate&&combinedReceipt.Allocation?.After.Count==2
                            &&combinedReceipt.Allocation.After.All(p=>p.OperationalDate==before.Note.OperationalDate),"data e distribuição são corrigidas juntas na mesma transação");
                    }
                }
                finally{await tx.RollbackAsync();}
            }
            check(await Snapshot(c,null)==snapshot,"rollback restaura parcelas, dados fiscais, livros, medições e todo o histórico");
            check((await repo.Detail(branch,db,note,default))!.Version==original.Version,"nota original permanece inalterada após o ensaio");
        }
    }

    private static async Task<string> Snapshot(SqlConnection c,SqlTransaction? tx)
    {
        using var cmd=new SqlCommand("""
            SET QUOTED_IDENTIFIER ON;
            SELECT (SELECT * FROM dbo.LMCENTRADATANQUES ORDER BY ID_FILIAL,ID_LMCENTRADATANQUES FOR XML RAW,BINARY BASE64),
             (SELECT * FROM dbo.HISTORICOTURNORETRO ORDER BY ID_HISTORICOTURNORETRO FOR XML RAW,BINARY BASE64),
             (SELECT * FROM dbo.COMPROVANTES WHERE ID_COMPROVANTE=560835 AND ID_FILIAL=17581 FOR XML RAW,BINARY BASE64),
             (SELECT * FROM dbo.ITENSCOMPROVANTE WHERE ID_COMPROVANTE=560835 AND ID_FILIAL=17581 ORDER BY ID_ITENSCOMPROVANTE FOR XML RAW,BINARY BASE64),
             (SELECT * FROM dbo.LMC ORDER BY ID_FILIAL,ID_LMC FOR XML RAW,BINARY BASE64),
             (SELECT * FROM dbo.LMCCOMPROVANTES ORDER BY ID_FILIAL,ID_LMCCOMPROVANTES FOR XML RAW,BINARY BASE64),
             (SELECT * FROM dbo.MOVTANQUES ORDER BY ID_FILIAL,ID_DB,ID_MOVTANQUES FOR XML RAW,BINARY BASE64),
             (SELECT * FROM dbo.QTDESTANQUES ORDER BY ID_FILIAL,ID_TANQUES FOR XML RAW,BINARY BASE64);
            """,c,tx);
        await using var r=await cmd.ExecuteReaderAsync();await r.ReadAsync();return BookWriting.Hash(Enumerable.Range(0,r.FieldCount).Select(i=>r.IsDBNull(i)?"":r.GetString(i)).ToArray());
    }
}
