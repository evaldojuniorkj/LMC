using Lmc.Web;

internal static class LmcDayChecks
{
    public static void Run(Action<bool,string> check)
    {
        var reading=new TankReading("MOVTANQUES",36576,5,"2026-02-01T00:00:00",null){InvalidVolume="2.2222222222222222e+029"};
        check(!reading.CanUse&&reading.Volume is null,"leitura fora da faixa permanece identificada sem virar zero utilizável");
        check((reading with {InvalidVolume=null,Volume=0}).CanUse,"leitura real zero é válida");
        check((reading with {InvalidVolume=null,Volume=1000000000m}).CanUse,"limite de leitura segue a faixa aceita no formulário");
        check(!(reading with {InvalidVolume=null,Volume=1000000001m}).CanUse&&!(reading with {InvalidVolume=null,Volume=-1}).CanUse,"leitura negativa ou além da faixa não pode preencher medição");
        check(!(reading with {InvalidVolume=null}).CanUse,"leitura ausente continua indisponível");
        check(BookWriting.Hash(reading)!=BookWriting.Hash(reading with {InvalidVolume="3.3e+030"}),"controle de alteração das fontes distingue valores inválidos originais");
        TankReading[] unordered=[reading,reading with {Volume=0,InvalidVolume=null},
            new("ESTOQUETANQUELMC",12,1,"2026-02-01T00:00:00",1234.5678m),
            new("MOVTANQUES",36576,1,"2026-02-01T00:00:00",1000m),reading];
        var sourceHash=BookWriting.Hash(LmcConference.OrderReadings(unordered));
        check(sourceHash==BookWriting.Hash(LmcConference.OrderReadings(unordered.Reverse())),"mesmas medições em outra ordem não simulam mudança de fonte");
        check(sourceHash!=BookWriting.Hash(LmcConference.OrderReadings(unordered.Skip(1))),"remoção de uma leitura duplicada continua sendo detectada");
        check(sourceHash!=BookWriting.Hash(LmcConference.OrderReadings(unordered.Select((x,i)=>i==2?x with {Volume=x.Volume+0.0001m}:x))),"mudança real de volume continua detectada com quatro casas");
        check(sourceHash!=BookWriting.Hash(LmcConference.OrderReadings(unordered.Select((x,i)=>i==0?x with {InvalidVolume="3.3e+030"}:x))),"mudança do valor inválido original continua detectada após ordenar");
        check(sourceHash!=BookWriting.Hash(LmcConference.OrderReadings(unordered.Select((x,i)=>i==2?x with {TankId=2}:x))),"troca do tanque da medição continua detectada");
        DayTank tank=new(1,"Gasolina",1,100,30000,10000,9000);
        Balance Calc(decimal? opening,decimal? closing,decimal? receipts=0,decimal? sales=1000,bool allowed=true)=>
            LmcConference.Calculate([new(1,opening,closing)],[tank],receipts,sales,allowed);
        var loss=Calc(10000,8900);
        check(loss.BookStock==9000 && loss.Difference==-100 && loss.Percent<0 && loss.AboveReference==true,"perda calculada com abertura física anterior, entradas e vendas");
        var gain=Calc(10000,9100);
        check(gain.Difference==100 && gain.AboveReference==true,"sobra acima da referência também gera alerta");
        check(Calc(10000,9000).Difference==0 && Calc(10000,9000).AboveReference==false,"igualdade real é aceita sem criar variações");
        check(Calc(11000,10060).AboveReference==false && Calc(11000,10060.0001m).AboveReference==true,"limite de 0,6% do escritural usa precisão anterior ao arredondamento");
        check(Calc(11000,9940).AboveReference==false && Calc(11000,9939.9999m).AboveReference==true,"limite de perda usa o mesmo critério simétrico");
        check(Calc(11000,10060).Percent==0.6m && Calc(11000,9940).Percent==-0.6m,"percentual usa o estoque escritural como base");
        check(Calc(1000,0).Percent is null && Calc(1000,0).AboveReference is null && Calc(1000,5).AboveReference is null,"escritural zero não recebe percentual ou aprovação");
        check(Calc(10000,0).Percent==-100 && Calc(10000,0).AboveReference==true,"físico zero com escritural positivo é perda total acima da referência");
        check(Calc(null,9000).BookStock is null && Calc(10000,null).Difference is null,"medição ausente não é tratada como zero");
        check(Calc(10000,9000,null).BookStock is null && Calc(10000,9000,0,null).BookStock is null,"entrada ou venda desconhecida mantém cálculo pendente");
        check(Calc(10000,9000,0,1000,false).BookStock is null,"inconsistências bloqueiam o balanço mesmo com medições completas");
        check(Calc(31000,31000).Alerts.Any(a=>a.Contains("capacidade")),"medição acima da capacidade cadastral gera alerta");
        check(Calc(0,0).BookStock==-1000 && Calc(0,0).Alerts.Any(a=>a.Contains("negativo")),"saldo escritural negativo não é escondido");
        check(loss.ReferenceMin==9000m*0.994m && loss.ReferenceMax==9000m*1.006m,"faixa de referência usa o escritural como base, sem preencher a medição");
        bool Reject(Action call) {try{call();return false;}catch(ArgumentException){return true;}}
        check(Reject(()=>Calc(-1,100)),"medição negativa é rejeitada");
        check(Reject(()=>LmcConference.Calculate([new(2,1,1)],[tank],0,0,true)),"tanque de outro produto não entra no cálculo");
        check(Reject(()=>LmcConference.Calculate([new(1,1,1),new(1,1,1)],[tank],0,0,true)),"tanque repetido não duplica o estoque");
        Book book=new(100,1,"Gasolina","2026-09-28",1);
        DayPump pump=new(1,1,1,100,10000,11000,0,1000);
        ArchivePump archive=new(1,1,1,10,"2026-09-28T07:00:00","2026-09-28T23:00:00",10000,11000,false);
        DayReceipt receipt=new(1,1,1,17581,7,99,"Fornecedor","2026-09-26","2026-09-28","2026-09-28","2026-09-28",5000,false,true);
        LmcDay Day(List<Book>? bs=null,List<DayPump>? ps=null,List<ArchivePump>? archives=null,List<DayReceipt>? rs=null,List<SavedReceipt>? saved=null)=>
            LmcConference.Assess(17581,"2026-09-28",bs??[book],[tank],ps??[pump],archives??[archive],rs??[],saved??[],[]);
        check(Day().Products[0].CanCalculate && Day().Products[0].Receipts==0,"dia com fontes coerentes permite simulação");
        check(!Day(archives:[]).Products[0].CanCalculate,"bico sem automação não vira venda zero");
        check(!Day(archives:[archive with {Ambiguous=true}]).Products[0].CanCalculate,"empate com encerrantes distintos bloqueia cálculo");
        check(!Day(archives:[archive with {Opening=9999}]).Products[0].CanCalculate,"divergência de abertura também é conferida");
        check(!Day(ps:[pump with {Calibration=null}]).Products[0].CanCalculate,"aferição desconhecida não vira zero");
        check(!Day(ps:[pump with {Closing=1}]).Products[0].CanCalculate,"virada de contador exige conferência");
        check(!Day(bs:[book,book with {Id=101}]).Products[0].CanCalculate,"livros duplicados impedem somar duas vezes");
        check(!Day(rs:[receipt with {Usable=false}]).Products[0].CanCalculate,"entrada incoerente bloqueia cálculo");
        check(Day(rs:[receipt with {Canceled=true,Usable=false}]).Products[0].Receipts==0,"entrada cancelada é excluída do balanço");
        check(Day(rs:[receipt]).Products[0].Receipts==5000 && Day(rs:[receipt]).Products[0].Alerts.Any(a=>a.Contains("entradas atuais diferem")),"cálculo usa entradas atuais e avisa que o livro ainda está desatualizado");
        check(!Day(archives:[archive,archive with {PumpId=2,ProductId=null}]).Products[0].CanCalculate,"bico sem produto identificado impede classificação do dia");
    }
}
