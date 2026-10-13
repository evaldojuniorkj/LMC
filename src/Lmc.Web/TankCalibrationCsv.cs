using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lmc.Web;

public sealed record CalibrationPoint(decimal Height, decimal Volume);
public sealed record CalibrationImport(string Name, string FileName, string Csv, string? Revision);
public sealed record CalibrationTable(string Id, string Name, string FileName, string ImportedAt, string Hash, CalibrationPoint[] Rows);

public static class TankCalibrationCsv
{
    public const int MaxBytes = 512 * 1024;
    public static CalibrationTable Parse(CalibrationImport input)
    {
        var name=input.Name?.Trim()??"";
        var file=input.FileName?.Trim()??"";
        if(name.Length is <1 or >100 || name.Any(char.IsControl)) Fail("Informe um nome de até 100 caracteres para a tabela.");
        if(file.Length is <5 or >180 || !file.EndsWith(".csv",StringComparison.OrdinalIgnoreCase)
            || file.IndexOfAny(['/', '\\', ':'])>=0 || file.Any(char.IsControl)) Fail("Escolha um arquivo .csv com nome válido.");
        if(string.IsNullOrWhiteSpace(input.Csv) || Encoding.UTF8.GetByteCount(input.Csv)>MaxBytes) Fail("O CSV deve ter conteúdo e no máximo 512 KB.");
        var rows=new List<CalibrationPoint>();
        char? separator=null;
        bool first=true;
        var lines=input.Csv!.TrimStart('\uFEFF').Split('\n');
        for(var i=0;i<lines.Length;i++)
        {
            var line=lines[i].Trim();if(line.Length==0)continue;
            separator??=line.Contains(';')?';':line.Contains('\t')?'\t':',';
            var cells=Cells(line,separator.Value,i+1);
            while(cells.Count>2&&cells[^1].Length==0)cells.RemoveAt(cells.Count-1);
            if(cells.Count!=2) Fail($"Linha {i+1}: use duas colunas, altura em cm e volume em litros.");
            if(first && Header(cells[0],true) && Header(cells[1],false)){first=false;continue;}
            first=false;
            var heightValid=Number(cells[0],separator.Value,out var height);
            var volumeValid=Number(cells[1],separator.Value,out var volume);
            if(!heightValid || !volumeValid)
                Fail($"Linha {i+1}: altura e volume devem ser números, sem separador de milhares, com até 4 casas decimais.");
            if(height<0 || height>10000 || volume<0 || volume>1000000000)
                Fail($"Linha {i+1}: altura ou volume fora dos limites (0 a 10.000 cm; 0 a 1 bilhão de litros).");
            if(rows.Count>0 && (height<=rows[^1].Height || volume<=rows[^1].Volume))
                Fail($"Linha {i+1}: alturas e volumes devem crescer, sem repetições. Confira a ordem do CSV.");
            rows.Add(new(height,volume));
            if(rows.Count>10000)Fail("A tabela deve ter no máximo 10.000 linhas de medidas.");
        }
        if(rows.Count<2)Fail("A tabela deve ter pelo menos duas linhas de medidas.");
        // Canonical numeric text deduplicates retries, independent of decimal scale or CSV dialect.
        var canonical=string.Join('\n',rows.Select(r=>r.Height.ToString("0.####",CultureInfo.InvariantCulture)+";"+r.Volume.ToString("0.####",CultureInfo.InvariantCulture)));
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(Guid.NewGuid().ToString("N"),name,file,DateTimeOffset.UtcNow.ToString("O"),hash,rows.ToArray());
    }
    private static void Fail(string message)=>throw new CorrectionException(400,message);
    private static bool Header(string value,bool height)
    {
        var normalized=new string(value.Normalize(NormalizationForm.FormD).Where(c=>CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
        return Regex.IsMatch(normalized,height?@"^(altura|cm|centimetros)(\s*\(?cm\)?)?$":@"^(volume|litros|l)(\s*\(?(litros|l)\)?)?$");
    }
    private static bool Number(string text,char separator,out decimal value)
    {
        value=0;
        var pattern=separator==','?@"^\d+(\.\d{1,4})?$":@"^\d+([.,]\d{1,4})?$";
        return Regex.IsMatch(text,pattern) && decimal.TryParse(text.Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value);
    }
    private static List<string> Cells(string line,char separator,int lineNumber)
    {
        var cells=new List<string>();var cell=new StringBuilder();bool quoted=false,closed=false;
        foreach(var c in line)
        {
            if(c=='"')
            {
                if(quoted){quoted=false;closed=true;}
                else if(cell.ToString().Trim().Length==0&&!closed)quoted=true;
                else Fail($"Linha {lineNumber}: aspas inválidas no CSV.");
            }
            else if(c==separator&&!quoted){cells.Add(cell.ToString().Trim());cell.Clear();closed=false;}
            else {if(closed&&!char.IsWhiteSpace(c))Fail($"Linha {lineNumber}: conteúdo após as aspas.");cell.Append(c);}
        }
        if(quoted)Fail($"Linha {lineNumber}: aspas não fechadas.");
        cells.Add(cell.ToString().Trim());return cells;
    }
}
