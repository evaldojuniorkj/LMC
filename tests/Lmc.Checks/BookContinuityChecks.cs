using Lmc.Web;
using System.Data;
using Microsoft.Data.SqlClient;

internal static class BookContinuityChecks
{
    public static void Run(Action<bool,string> check)
    {
        static DateOnly D(string value)=>DateOnly.Parse(value);
        var missing=BookContinuity.MissingPeriods([D("2026-09-01"),D("2026-09-03")],D("2026-09-04"));
        check(missing.SequenceEqual(new[]{new BookMissingPeriod("2026-09-02","2026-09-02",1)}),"livros nos dias 1 e 3 sugerem preencher o dia 2 ao inserir o dia 4");
        check(BookContinuity.MissingPeriods([D("2026-09-02"),D("2026-09-01"),D("2026-09-02"),D("2026-09-03")],D("2026-09-04")).Count==0,"sequência completa não gera aviso, mesmo com datas repetidas ou desordenadas");
        check(BookContinuity.MissingPeriods([],D("2026-09-04")).Count==0,"sem histórico não inventa período pendente");
        check(BookContinuity.MissingPeriods([D("2026-09-03")],D("2026-09-04")).Count==0,"não presume lacuna antes do primeiro livro disponível");
        check(BookContinuity.MissingPeriods([D("2026-09-01"),D("2026-09-03")],D("2026-09-02")).Count==0,"ao preencher uma lacuna, não avisa sobre o próprio dia nem datas posteriores");
        missing=BookContinuity.MissingPeriods([D("2024-02-28"),D("2024-03-02"),D("2024-03-05")],D("2024-03-08"));
        check(missing.SequenceEqual(new[]{new BookMissingPeriod("2024-02-29","2024-03-01",2),new BookMissingPeriod("2024-03-03","2024-03-04",2),new BookMissingPeriod("2024-03-06","2024-03-07",2)}),"agrupa lacunas e inclui dias pulados manualmente, atravessando mês e ano bissexto");
        missing=BookContinuity.MissingPeriods([D("2025-12-30"),D("2026-01-02")],D("2026-01-03"));
        check(missing.Single()==new BookMissingPeriod("2025-12-31","2026-01-01",2),"detecta lacuna na virada do ano");
        var p=new BookPreparation(17581,"2026-09-04",1,"Gasolina",null,5,new string('A',64),
            [new(1,30000,10000,true)],[new(1,1,1000,2000,0,7,1000,"teste")],[],[],[],[],"",[])
            {MissingPeriods=[new("2026-09-02","2026-09-02",1)]};
        var preview=BookWriting.Preview(p,new(17581,p.Date,1,p.Version,[new(1,10000,9000)],"","manual"));
        check(preview.CanSave&&preview.Blockers.Count==0,"dia anterior pendente é sugestão e não impede gravar o novo LMC");
        BookContinuityHeader[] books=[new(1,1,"Gasolina",D("2026-08-31")),new(2,1,"Gasolina",D("2026-09-01"))];
        BookContinuityReading[] readings=[new(1,"tank",1,1200,1000),new(1,"tank",2,2200,2000),new(1,"pump",13,10000,11000),
            new(2,"tank",1,1000,900),new(2,"tank",2,2000,1800),new(2,"pump",13,11000,12000)];
        IReadOnlyList<BookInconsistency> Issues(BookContinuityReading[] values)=>BookContinuity.Inspect(books,values).Single().Inconsistencies;
        check(Issues(readings).Count==0,"continuidade correta de tanques e bicos atravessa o mês sem alerta");
        var changed=readings.Select(x=>x.BookId==2?x with {Opening=x.Opening+(x.Kind=="pump"?.0001m:x.EquipmentId==1?10:-10)}:x).ToArray();
        var issues=Issues(changed);
        check(issues.Count==3&&issues.All(x=>x.Code=="opening_mismatch"&&x.PreviousDate=="2026-08-31"),"compara por equipamento: diferenças opostas nos tanques não se anulam no total");
        check(issues.Single(x=>x.Kind=="pump").Difference==.0001m,"divergência dos encerrantes conserva a precisão gravada");
        check(issues.Single(x=>x.Kind=="tank"&&x.EquipmentId==2) is {PreviousClosing:2000,Opening:1990,Difference:-10},"informa referência, abertura salva e diferença com sinal");
        var gapBooks=books.Select(x=>x.Id==2?x with {Date=D("2026-09-02")}:x).ToArray();
        var gap=BookContinuity.Inspect(gapBooks,changed).Single();
        check(gap.Inconsistencies.Count==0&&gap.MissingPeriods.Single().From=="2026-09-01","lacuna não compara abertura com fechamento de data mais antiga");
        check(BookContinuity.Inspect([books[1]],changed.Where(x=>x.BookId==2).ToArray()).Single().Inconsistencies.Count==0,"primeiro livro não presume um fechamento anterior");
        check(BookContinuity.Inspect([books[0],books[1] with {ProductId=2}],changed).All(x=>x.Inconsistencies.Count==0),"bico com mesmo número em combustíveis distintos não mistura referências");
        check(Issues([..readings,new(2,"pump",14,300,400)]).Count==0,"bico novo sem registro anterior não ganha referência zero");
        check(Issues(readings.Select(x=>x.BookId==2&&x.Kind=="pump"?x with {Closing=100}:x).ToArray()).Single().Code=="counter_order","encerrante final menor que inicial também é pendência");
        check(Issues(readings.Select(x=>x.BookId==2&&x.Kind=="pump"?x with {Opening=null}:x).ToArray()).Single().Code=="missing_value","encerrante ausente não é convertido em zero");
        check(Issues(readings.Select(x=>x.BookId==1&&x.Kind=="pump"?x with {Closing=null}:x).ToArray()).Any(x=>x.BookId==2&&x.Code=="missing_previous_closing"),"fechamento anterior ausente informa que a comparação não pôde ser feita");
        check(Issues([..readings,new(1,"pump",13,10000,10099)]).Any(x=>x.BookId==2&&x.Code=="ambiguous_previous"),"bico anterior repetido não escolhe referência arbitrária");
        var duplicates=BookContinuity.Inspect([..books,new(3,1,"Gasolina",books[0].Date)],readings).Single().Inconsistencies;
        check(duplicates.Count(x=>x.Code=="duplicate_book")==2&&duplicates.Any(x=>x.BookId==2&&x.Code=="ambiguous_previous"),"livros duplicados aparecem e invalidam a comparação com o dia seguinte");
        check(BookContinuity.Inspect([],[]).Count==0,"histórico vazio não cria pendências");
        BookContinuityReading[] misallocated=[new(2,"tank",1,10000,13000){Received=10000,Sales=2000},new(2,"tank",2,10000,13000){Received=0,Sales=2000}];
        BookPendingProduct InspectTanks(BookContinuityReading[] values)=>BookContinuity.Inspect([books[1]],values).Single();
        var review=InspectTanks(misallocated).TankReviews.Single();
        check(review.PossibleMisallocation&&review.TotalWithinReference&&review.Tanks.Sum(x=>x.Difference)==0,"entrada de 10 mil em um tanque alerta mesmo com total do combustível correto");
        check(review.Tanks[0] is {Received:10000,Difference:-5000}&&review.Tanks[1] is {Received:0,Difference:5000},"detalhe mostra falta de 5 mil em A e sobra de 5 mil em B sem presumir correção");
        check(InspectTanks(misallocated.Select(x=>x with {Received=5000}).ToArray()).TankReviews.Count==0,"distribuição conferida de 5 mil por tanque elimina a diferença");
        check(!InspectTanks(misallocated.Select(x=>x with {Received=0}).ToArray()).TankReviews.Single().PossibleMisallocation,"diferença sem entrada não é atribuída a distribuição de nota");
        check(InspectTanks([new(2,"tank",1,1000,1006)]).TankReviews.Count==0&&InspectTanks([new(2,"tank",1,1000,994)]).TankReviews.Count==0,"diferença exatamente em 0,6% do escritural não dispara alerta por tanque");
        check(InspectTanks([new(2,"tank",1,1000,1006.0001m)]).TankReviews.Single().Tanks[0] is {AboveReference:true,Percent:>0.6m},"diferença acima da referência é detectada por tanque");
        check(InspectTanks([new(2,"tank",1,0,5)]).TankReviews.Single().Tanks[0].Percent is null,"escritural zero mantém diferença em litros sem inventar percentual");
        var incomplete=InspectTanks([new(2,"tank",1,1000,900){Received=null}]);
        check(incomplete.TankReviews.Count==0&&incomplete.Inconsistencies.Single().Code=="incomplete_balance","entrada sem volume impede confirmar o saldo e aparece para conferência");
    }

    public static async Task Rollback(Action<bool,string> check)
    {
        const string connection="Server=localhost;Database=ATXDADOS;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=5";
        await using var c=new SqlConnection(connection);await c.OpenAsync();
        var before=await BookWriteChecks.Snapshot(c,null);
        using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
        {
            try
            {
                var baseline=await Repository.ReadPendingBooks(c,tx,17581,default);
                check(baseline.Count>0,"consulta SQL das pendências lê o histórico salvo da filial");
                using var target=new SqlCommand("""
                    SELECT TOP(1) l.ID_LMC,l.ID_PRODUTOS,CONVERT(date,l.DTACONTA),t.ID_LMCTANQUES,b.ID_LMCBICOS,
                        CONVERT(decimal(19,4),pt.QTDECONCILIACAO),CONVERT(decimal(19,4),pb.ENCERRANTEFECHAMENTO)
                    FROM dbo.LMC l JOIN dbo.LMC p ON p.ID_FILIAL=l.ID_FILIAL AND p.ID_PRODUTOS=l.ID_PRODUTOS AND CONVERT(date,p.DTACONTA)=DATEADD(day,-1,CONVERT(date,l.DTACONTA))
                    JOIN dbo.LMCTANQUES t ON t.ID_FILIAL=l.ID_FILIAL AND t.ID_LMC=l.ID_LMC
                    JOIN dbo.LMCTANQUES pt ON pt.ID_FILIAL=p.ID_FILIAL AND pt.ID_LMC=p.ID_LMC AND pt.ID_TANQUES=t.ID_TANQUES
                    JOIN dbo.LMCBICOS b ON b.ID_FILIAL=l.ID_FILIAL AND b.ID_LMC=l.ID_LMC
                    JOIN dbo.LMCBICOS pb ON pb.ID_FILIAL=p.ID_FILIAL AND pb.ID_LMC=p.ID_LMC AND pb.ID_BICOS=b.ID_BICOS
                    WHERE l.ID_FILIAL=17581 AND l.ID_PRODUTOS=1 AND pt.QTDECONCILIACAO IS NOT NULL AND pb.ENCERRANTEFECHAMENTO IS NOT NULL
                    ORDER BY l.DTACONTA DESC,t.ID_LMCTANQUES,b.ID_LMCBICOS;
                    """,c,tx);
                int id,product,tank,pump;decimal opening,counter;string date;
                await using(var r=await target.ExecuteReaderAsync())
                {
                    if(!await r.ReadAsync())throw new Exception("Sem par de livros para testar continuidade.");
                    id=r.GetInt32(0);product=r.GetInt32(1);date=r.GetDateTime(2).ToString("yyyy-MM-dd");tank=r.GetInt32(3);pump=r.GetInt32(4);opening=r.GetDecimal(5);counter=r.GetDecimal(6);
                }
                using var edit=new SqlCommand("""
                    UPDATE dbo.LMCTANQUES SET QTDETANQUE=@opening+@delta WHERE ID_FILIAL=17581 AND ID_LMCTANQUES=@tank;
                    UPDATE dbo.LMCBICOS SET ENCERRANTEABERTURA=@counter+@delta,ENCERRANTEFECHAMENTO=@counter+1000 WHERE ID_FILIAL=17581 AND ID_LMCBICOS=@pump;
                    """,c,tx);
                edit.Parameters.AddWithValue("@opening",opening);edit.Parameters.AddWithValue("@counter",counter);
                edit.Parameters.AddWithValue("@tank",tank);edit.Parameters.AddWithValue("@pump",pump);edit.Parameters.AddWithValue("@delta",7.25m);
                await edit.ExecuteNonQueryAsync();
                var inspected=await Repository.ReadPendingBooks(c,tx,17581,default);
                var issues=inspected.Single(x=>x.ProductId==product).Inconsistencies.Where(x=>x.BookId==id&&x.Code=="opening_mismatch").ToArray();
                check(issues.Any(x=>x.Kind=="tank"&&x.Difference==7.25m&&x.Date==date)&&issues.Any(x=>x.Kind=="pump"&&x.Difference==7.25m),"consulta encontra abertura do tanque e do bico diferentes dos fechamentos salvos anteriores");
                edit.Parameters["@delta"].Value=0m;await edit.ExecuteNonQueryAsync();
                var beforeRead=await BookWriteChecks.Snapshot(c,tx);
                var corrected=await Repository.ReadPendingBooks(c,tx,17581,default);
                check(!corrected.Single(x=>x.ProductId==product).Inconsistencies.Any(x=>x.BookId==id&&x.Code=="opening_mismatch"&&issues.Any(y=>y.Kind==x.Kind&&y.EquipmentId==x.EquipmentId&&y.Difference==7.25m)),"atualizar a consulta remove divergências corrigidas");
                check(beforeRead==await BookWriteChecks.Snapshot(c,tx),"consulta de pendências não executa correções");
            }
            finally {await tx.RollbackAsync();}
        }
        check(before==await BookWriteChecks.Snapshot(c,null),"rollback restaura todos os livros após teste de inconsistências");
    }
}
