using System.Text.Json;

namespace Lmc.Web;

public sealed record TankTableBinding(string Scope, short Branch, short TankId, int ProductId, string TableId);
public sealed record TankTableCatalog(int Format, string Revision, CalibrationTable[] Tables, TankTableBinding[] Bindings);
public sealed record TankTableSelection(short TankId, int ProductId, string? TableId, string Revision);

// Application files only. This store has no SQL dependency and never modifies a measurement.
public sealed class TankTableStore(string directory)
{
    private readonly object gate=new();
    private readonly string path=Path.Combine(directory,"catalog.json");
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true};
    public static string DirectoryFor(IConfiguration config,string contentRoot)
    {
        if(!string.IsNullOrWhiteSpace(config["TankTables:Directory"]))return Path.GetFullPath(config["TankTables:Directory"]!);
        return !string.IsNullOrWhiteSpace(config["ProtectedConfig"])
            ?Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config["ProtectedConfig"]!))!,"tank-tables")
            :Path.Combine(contentRoot,"App_Data","tank-tables");
    }
    public TankTableCatalog Read()
    {
        lock(gate)
        {
            if(!File.Exists(path))return new(1,"0",[],[]);
            using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
            var data=JsonSerializer.Deserialize<TankTableCatalog>(file,Json);
            if(data is null || data.Format!=1 || data.Tables is null || data.Bindings is null || string.IsNullOrEmpty(data.Revision)
                || data.Tables.Any(t=>t is null || t.Rows is null || t.Rows.Length<2)
                || data.Bindings.Any(b=>b is null || !data.Tables.Any(t=>t.Id==b.TableId)))
                throw new JsonException("Catálogo inválido.");
            return data;
        }
    }
    public CalibrationTable Import(CalibrationImport input)
    {
        var table=TankCalibrationCsv.Parse(input);
        lock(gate)
        {
            using var lease=Acquire();var catalog=Read();
            var existing=catalog.Tables.FirstOrDefault(t=>t.Hash==table.Hash);
            if(existing is not null)return existing;
            CheckRevision(catalog,input.Revision);
            if(catalog.Tables.Length>=100)throw new CorrectionException(400,"Limite de 100 tabelas importadas atingido.");
            Write(catalog with {Tables=[..catalog.Tables,table]});return table;
        }
    }
    public void Assign(string scope,short branch,TankTableSelection input)
    {
        if(branch<=0 || input.TankId<=0 || input.ProductId<=0)throw new CorrectionException(400,"Escolha um tanque válido.");
        lock(gate)
        {
            using var lease=Acquire();var catalog=Read();CheckRevision(catalog,input.Revision);
            if(input.TableId is not null && !catalog.Tables.Any(t=>t.Id==input.TableId))
                throw new CorrectionException(400,"Tabela não localizada. Atualize a lista.");
            var bindings=catalog.Bindings.Where(b=>b.Scope!=scope || b.Branch!=branch || b.TankId!=input.TankId).ToList();
            if(input.TableId is not null)bindings.Add(new(scope,branch,input.TankId,input.ProductId,input.TableId));
            Write(catalog with {Bindings=bindings.ToArray()});
        }
    }
    private FileStream Acquire()
    {
        Directory.CreateDirectory(directory);
        try{return new FileStream(Path.Combine(directory,"catalog.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){throw new CorrectionException(409,"Outra operação está atualizando as tabelas. Aguarde e tente novamente.");}
    }
    private static void CheckRevision(TankTableCatalog catalog,string? revision)
    {
        if(revision!=catalog.Revision)throw new CorrectionException(409,"As tabelas foram atualizadas em outra sessão. Atualize esta tela antes de salvar.");
    }
    private void Write(TankTableCatalog catalog)
    {
        var temporary=Path.Combine(directory,Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(file,catalog with {Revision=Guid.NewGuid().ToString("N")},Json);file.Flush(true);}
            File.Move(temporary,path,overwrite:true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
