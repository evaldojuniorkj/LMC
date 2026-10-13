using System.Data.Common;
using System.IO;
using System.Text.Json;

namespace Lmc.Setup;
internal static class SetupChecks
{
    public static async Task Run()
    {
        var checks=0;
        void Check(bool condition,string text){if(!condition)throw new Exception("FALHOU: "+text);checks++;Console.WriteLine("OK: "+text);}
        var valid=new SetupOptions("SERVIDOR\\INSTANCIA","sa","fictícia;\"= senha",false,"192.168.1.10","operador","1234567890","1234567890",5443,true,true);
        var named=SetupDefaults.Create([new("localhost",false),new("localhost\\POSTO",true)],["192.168.50.10"]);
        Check(named.Options.SqlServer=="localhost\\POSTO"&&named.Options.ServerAddress=="192.168.50.10","padrão detecta a instância SQL ativa e usa o endereço da máquina de destino");
        Check(named.Options.OperatorPassword.Length==10&&named.Options.OperatorPassword==named.Options.OperatorPasswordConfirm&&named.Options.ValidateAccess() is null,"senha inicial da interface já vem preenchida e confirmada");
        Check(named.Options.SqlPassword.Length==0&&named.Options.TrustSqlCertificate&&named.Options.Port==5443,"perfil padrão da rede interna não inclui senha SQL no pacote");
        var ambiguous=SetupDefaults.Create([new("localhost\\A",true),new("localhost\\B",true)],[]);
        Check(ambiguous.Options.SqlServer=="localhost"&&ambiguous.SqlServers.Contains("localhost\\A")&&ambiguous.SqlServers.Contains("localhost\\B"),"múltiplas instâncias são oferecidas sem escolher arbitrariamente uma base");
        Check(valid.ValidateSql() is null&&valid.ValidateAccess() is null,"instalação aceita senha de interface com dez caracteres");
        Check((valid with{OperatorPassword="123456789",OperatorPasswordConfirm="123456789"}).ValidateAccess() is not null,"senha de nove caracteres rejeitada");
        Check((valid with{OperatorPasswordConfirm="123456789X"}).ValidateAccess() is not null,"confirmação divergente rejeitada");
        Check((valid with{Port=80}).ValidateAccess() is not null,"porta fora da faixa rejeitada");
        Check((valid with{ServerAddress="https://192.168.1.10"}).ValidateAccess() is not null,"URL não confundida com endereço do servidor");
        Check((valid with{ServerAddress="127.0.0.1"}).ValidateAccess() is not null,"endereço da rede não pode ser loopback");
        Check((valid with{SqlPassword=""}).ValidateSql() is not null,"senha SQL ausente rejeitada");
        var builder=new DbConnectionStringBuilder{ConnectionString=valid.ConnectionString()};
        Check((string)builder["Password"]==valid.SqlPassword&&(string)builder["Initial Catalog"]=="ATXDADOS","credenciais com caracteres especiais preservadas sem interpolação SQL");
        Check(builder["Encrypt"].ToString()=="True"&&builder["TrustServerCertificate"].ToString()=="False","cliente pode reativar a validação do certificado SQL mantendo a criptografia");
        var edited=valid with {SqlServer="OUTRO\\SQL",SqlUser="cliente",Database="ATXDADOS_TESTE",ServerAddress="192.168.1.20",Port=6443,Operator="gerente",TrustLocalHttpsCertificate=false,CreateDesktopShortcut=false};
        var editedConnection=new DbConnectionStringBuilder{ConnectionString=edited.ConnectionString()};
        Check(edited.ValidateSql() is null&&edited.ValidateAccess() is null&&(string)editedConnection["Initial Catalog"]=="ATXDADOS_TESTE"&&(string)editedConnection["Data Source"]=="OUTRO\\SQL"&&edited.Url=="https://192.168.1.20:6443","configurações editadas chegam à conexão e ao endereço sem voltar aos padrões");
        Check(!valid.Redact(valid.SqlPassword+" / "+valid.OperatorPassword).Contains(valid.SqlPassword),"mensagens ocultam senhas");
        Check(SetupOptions.ExplainSqlError("código -2146893019").Contains("marque a opção")&&!SetupOptions.ExplainSqlError("18456").Contains("certificado"),"diagnóstico visual orienta o campo correto");
        try{InstallerEngine.SafeEntryPath(Path.GetFullPath("local"),"../fora.txt");Check(false,"escape do pacote bloqueado");}catch(InvalidDataException){Check(true,"escape do pacote bloqueado");}
        var script="[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $v=[Console]::In.ReadToEnd()|ConvertFrom-Json; if($v.SqlPassword -ceq 'fictícia;\"= senha' -and $v.OperatorPassword.Length -eq 10){Write-Output 'PIPE_OK'}else{exit 3}";
        var encoded=Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var result=await InstallerEngine.Run(InstallerEngine.PowerShell,["-NoProfile","-NonInteractive","-EncodedCommand",encoded],Environment.CurrentDirectory,JsonSerializer.Serialize(valid),_=>{},TimeSpan.FromSeconds(10));
        Check(result.ExitCode==0&&result.Output.Contains("PIPE_OK"),"JSON com senhas percorre pipe privado UTF-8 do Windows PowerShell");
        Check(!result.Output.Contains(valid.SqlPassword),"senhas não aparecem na saída do processo");
        var profiles=await InstallerEngine.NetworkProfiles();
        Check(!string.IsNullOrWhiteSpace(profiles)&&!profiles.StartsWith("Não foi possível"),"diagnóstico consulta o perfil de rede pelos módulos do Windows");
        var badArchive=Path.Combine(Path.GetTempPath(),"lmc-invalid-package-"+Guid.NewGuid().ToString("N")+".zip");
        try
        {
            using(var zip=System.IO.Compression.ZipFile.Open(badArchive,System.IO.Compression.ZipArchiveMode.Create))
            {using var writer=new StreamWriter(zip.CreateEntry("SHA256SUMS.txt").Open());writer.WriteLine("manifesto inválido");}
            using var invalidEngine=new InstallerEngine(badArchive);
            for(var attempt=0;attempt<2;attempt++)
            {
                try{await invalidEngine.Prepare(_=>{});Check(false,"pacote inválido não pode ser reutilizado");}
                catch(InvalidDataException){Check(true,"pacote inválido é rejeitado inclusive ao repetir a operação");}
            }
        }finally{File.Delete(badArchive);}
        Console.WriteLine($"{checks} verificações do instalador visual passaram. Nenhum serviço, certificado ou dado alterado.");
    }
    public static async Task Package(string? path)
    {
        string root;
        using(var engine=new InstallerEngine(path))
        {
            root=await engine.Prepare(Console.WriteLine);
            using var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();
            var port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
            var options=new SetupOptions($"tcp:127.0.0.1,{port}","teste-ficticio","senha-ficticia",false,"192.168.1.10","operador","1234567890","1234567890",5443,true,true,"ATXDADOS_TESTE");
            var result=await engine.CheckSql(options,_=>{});
            if(result.ExitCode!=1||!result.Output.Contains("código")||result.Output.Contains(options.SqlPassword))throw new Exception($"Falha ao verificar o tratamento do SQL indisponível (saída {result.ExitCode}): "+options.Redact(result.Output));
            if(File.Exists(Path.Combine(root,"sql-check.bin")))throw new Exception("Configuração temporária não foi removida.");
            Console.WriteLine("OK: aplicação incorporada executou a verificação e retornou o erro SQL esperado, sem expor senha.");
            // Executa somente o bloco real de entrada do script, sem suas etapas de instalação.
            var scriptPath=Path.Combine(root,"Instalar-LMC.ps1").Replace("'","''");
            var script="[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $ErrorActionPreference='Stop'; $tokens=$null;$errors=$null; $ast=[Management.Automation.Language.Parser]::ParseInput([IO.File]::ReadAllText('"+scriptPath+"'),[ref]$tokens,[ref]$errors); $block=$ast.EndBlock.Statements|Where-Object{$_.Extent.Text.StartsWith('if($InputFromStdin)')}; if($errors.Count -or -not $block){exit 5}; $InputFromStdin=$true; . ([ScriptBlock]::Create($block.Extent.Text)); if($Database -eq 'ATXDADOS_TESTE' -and $SqlUser -eq 'teste-ficticio' -and $Port -eq 5443 -and $operatorPassword.Length -eq 10 -and $TrustLocalHttpsCertificate -and $CreateDesktopShortcut -and -not $TrustSqlCertificate){Write-Output 'INPUT_OK'}else{exit 6}";
            var r=await InstallerEngine.Run(InstallerEngine.PowerShell,["-NoProfile","-NonInteractive","-ExecutionPolicy","RemoteSigned","-OutputFormat","Text","-EncodedCommand",Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))],root,JsonSerializer.Serialize(options),_=>{},TimeSpan.FromSeconds(10));
            if(r.ExitCode!=0||!r.Output.Contains("INPUT_OK"))throw new Exception($"A entrada não interativa do instalador falhou (saída {r.ExitCode}): "+options.Redact(r.Output));
            Console.WriteLine("OK: script incorporado recebeu opções e senhas pelo pipe privado, sem prompts.");
        }
        if(Directory.Exists(root))throw new Exception("Pasta temporária não foi removida.");
        Console.WriteLine("OK: arquivos temporários removidos. Nenhuma instalação, certificado ou acesso ao banco real.");
    }
}
