using Lmc.Web;
using Microsoft.Extensions.Configuration;

static class TankTableChecks
{
    public static void Run(Action<bool,string> check)
    {
        void Reject(Action action,int status,string message)
        {try{action();check(false,message);}catch(CorrectionException ex){check(ex.Status==status,message);}}
        CalibrationImport Input(string csv,string revision="0")=>new("Tabela de teste","tanque.csv",csv,revision);
        var path=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..","fixtures","tanque-pleno-30k-revisado.csv"));
        var original=TankCalibrationCsv.Parse(Input(File.ReadAllText(path)));
        check(original.Rows.Length==255 && original.Rows[63]==new CalibrationPoint(64,6024) && original.Rows[93].Volume==10225 && original.Rows[^1].Volume==30642,"importação preserva a tabela original de 255 medidas, inclusive valores irregulares");
        var decimalCsv="\uFEFFAltura (cm);Volume (L);\r\n\"0\";\"0\";\r\n\"1,5\";\"12,3456\";\r\n";
        var parsed=TankCalibrationCsv.Parse(Input(decimalCsv));
        check(parsed.Rows[1]==new CalibrationPoint(1.5m,12.3456m),"CSV brasileiro com cabeçalho, aspas, BOM e coluna vazia final preserva decimais");
        check(TankCalibrationCsv.Parse(Input("cm,litros\n0,0\n1.5,12.3456")).Hash==parsed.Hash,"CSV por vírgula aceita ponto decimal e produz o mesmo conteúdo");
        check(TankCalibrationCsv.Parse(Input("0\t0\n1.5000\t12.3456")).Hash==parsed.Hash,"tabulação e zeros decimais não criam cópia duplicada");
        foreach(var invalid in new[]{"1;10", "1;10\n1;20", "2;10\n1;20", "1;10\n2;9", "1;10\n2;10", "-1;10\n2;20", "1;10\n2;=SUM(A1)", "1;10\n2;1.000,00", "1;10\n2;15,12345", "cm;litros\n1;10;999", "altura;volume\n1;10\n\"2;20"})
            Reject(()=>TankCalibrationCsv.Parse(Input(invalid)),400,"rejeita CSV inválido sem corrigir silenciosamente: "+invalid.Replace('\n','|'));
        Reject(()=>TankCalibrationCsv.Parse(Input(new string('1',TankCalibrationCsv.MaxBytes+1))),400,"CSV acima do limite é rejeitado");
        Reject(()=>TankCalibrationCsv.Parse(Input("1;10\n2;20") with {FileName="../tanque.csv"}),400,"nome de arquivo não pode conter caminho");
        var directory=Path.Combine(Path.GetTempPath(),"lmc-tank-tables-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new TankTableStore(directory);
            check(store.Read().Tables.Length==0&&!Directory.Exists(directory),"consulta sem tabelas não cria arquivo ou diretório");
            var first=store.Import(Input("0;0\n100;10000"));
            check(new TankTableStore(directory).Read().Tables.Single().Id==first.Id,"catálogo em arquivo persiste entre instâncias do aplicativo");
            var revision=store.Read().Revision;
            check(store.Import(Input("0;0\n100;10000")).Id==first.Id && store.Read().Revision==revision,"repetição de importação não duplica o CSV nem invalida a revisão");
            Reject(()=>store.Import(Input("0;0\n100;15000")),409,"nova importação com versão antiga não sobrescreve outra sessão");
            var second=store.Import(Input("0;0\n100;15000",revision));revision=store.Read().Revision;
            store.Assign("server-a",4500,new(1,1,first.Id,revision));revision=store.Read().Revision;
            store.Assign("server-a",4500,new(2,1,second.Id,revision));revision=store.Read().Revision;
            store.Assign("server-a",4501,new(1,1,second.Id,revision));revision=store.Read().Revision;
            store.Assign("server-b",4500,new(1,1,second.Id,revision));
            var data=new TankTableStore(directory).Read();
            check(data.Bindings.Length==4 && data.Bindings[0].TableId!=data.Bindings[1].TableId,"vínculos distinguem tanque, filial e servidor, inclusive tanques do mesmo produto");
            Reject(()=>store.Assign("server-a",4500,new(1,1,second.Id,revision)),409,"escolha concorrente com versão antiga é rejeitada");
            Reject(()=>store.Assign("server-a",4500,new(1,1,"ausente",data.Revision)),400,"não permite vínculo com tabela inexistente");
            store.Assign("server-a",4500,new(1,1,null,data.Revision));
            check(store.Read().Bindings.Length==3 && store.Read().Tables.Length==2,"desvincular um tanque preserva outras filiais e todas as tabelas");
            using(var lease=new FileStream(Path.Combine(directory,"catalog.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                Reject(()=>new TankTableStore(directory).Assign("server-a",4500,new(1,1,first.Id,store.Read().Revision)),409,"trava de arquivo impede escrita concorrente entre processos");
            File.WriteAllText(Path.Combine(directory,"catalog.json"),"invalid");
            try{store.Import(Input("0;0\n100;15000"));check(false,"arquivo danificado preservado");}
            catch(System.Text.Json.JsonException){check(File.ReadAllText(Path.Combine(directory,"catalog.json"))=="invalid","arquivo danificado nunca é substituído por catálogo vazio");}
        }
        finally
        {
            // This exact, uniquely-created temporary directory belongs only to this test.
            if(Directory.Exists(directory))Directory.Delete(directory,true);
        }
        var root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"lmc-config-test"));
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ProtectedConfig",Path.Combine(root,"configuration.bin")}}).Build();
        check(TankTableStore.DirectoryFor(config,"release-1")==TankTableStore.DirectoryFor(config,"release-2") && TankTableStore.DirectoryFor(config,"release-1")==Path.Combine(root,"tank-tables"),"produção usa diretório persistente fora da pasta de versão e do SQL Server");
    }
}
