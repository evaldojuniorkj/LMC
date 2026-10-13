using System.Data;
using Lmc.Web;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

internal static class BookBatchChecks
{
    public static async Task Rollback(Action<bool,string> check)
    {
        const string connection="Server=localhost;Database=ATXDADOS;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=5";
        var repo=new Repository(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:AtxDados",connection}}).Build());
        await using var c=new SqlConnection(connection);await c.OpenAsync();
        var ids=new List<int>();DateOnly? last=null;
        using(var cmd=new SqlCommand("SELECT TOP(2) ID_LMC,DTACONTA FROM dbo.LMC WHERE ID_FILIAL=17581 AND ID_PRODUTOS=1 ORDER BY DTACONTA DESC,ID_LMC DESC;",c))
        {
            await using var r=await cmd.ExecuteReaderAsync();while(await r.ReadAsync()){ids.Add(r.GetInt32(0));last??=DateOnly.FromDateTime(r.GetDateTime(1));}
        }
        if(ids.Count!=2||last is null)throw new Exception("Ensaio requer dois livros existentes de gasolina na cópia de desenvolvimento.");
        var next=await repo.NextBook(17581,1,new DateOnly(2026,9,1),default);
        check(next.Date==last.Value.AddDays(1).ToString("yyyy-MM-dd")&&next.ProductId==1,"inserção consulta o último livro atual do combustível e avança exatamente um dia");
        var prep=await repo.PrepareBook(17581,Rules.Date(next.Date)!.Value,1,default);
        using(var cmd=new SqlCommand("SELECT ID_TANQUES,CONVERT(decimal(19,4),QTDECONCILIACAO) FROM dbo.LMCTANQUES WHERE ID_FILIAL=17581 AND ID_LMC=@id;",c))
        {
            cmd.Parameters.Add("@id",SqlDbType.Int).Value=ids[0];await using var r=await cmd.ExecuteReaderAsync();var found=0;
            while(await r.ReadAsync())
            {
                var t=prep.Tanks.Single(x=>x.TankId==r.GetInt16(0));decimal? closing=r.IsDBNull(1)?null:r.GetDecimal(1);
                check(t.PreviousClosing==closing&&t.Inherited==closing.HasValue,$"tanque {t.TankId}: abertura carrega o fechamento salvo no dia anterior, inclusive de livro antigo");found++;
            }
            check(found==prep.Tanks.Count,"todos os tanques do novo livro têm correspondência no livro anterior");
        }
        foreach(var invalid in new[]{Array.Empty<int>(),new[]{ids[0],ids[0]},Enumerable.Range(1,201).ToArray()})
        {
            try{BookBatchDeletion.Validate(17581,invalid);check(false,"seleção inválida");}
            catch(CorrectionException e){check(e.Status==400,"lote vazio, repetido ou acima do limite é rejeitado");}
        }
        var selection=new BookBatchSelection(17581,ids);var preview=await repo.PrepareBookBatchDeletion(selection,default);
        check(preview.Books.Count==2&&!preview.FollowingBooks.Any(b=>ids.Contains(b.Id)),"confirmação inclui exatamente a seleção e exclui esses livros do aviso de posteriores");
        var before=await Snapshot(c,null);
        using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
        {
            try
            {
                var input=new BookBatchDeleteRequest(17581,preview.Books.Select(b=>new BookBatchItem(b.BookId,b.Version)).ToArray(),Guid.NewGuid());
                var bad=input with {Books=[input.Books[0],input.Books[1] with {Version=new string('0',64)}]};
                try{await repo.DeleteBookBatchInTransaction(c,tx,bad,default);check(false,"lote desatualizado");}
                catch(CorrectionException e){check(e.Status==409,"um livro desatualizado impede a exclusão de todo o lote");}
                check(before==await Snapshot(c,tx),"falha de versão em um item preserva inclusive os itens válidos do lote");
                var result=await repo.DeleteBookBatchInTransaction(c,tx,input,default);
                check(result.Books.Count==2&&!result.AlreadyDeleted,"exclusão em lote retorna recibo para todos os selecionados");
                foreach(var id in ids)check(await Repository.BookDeletionRows(c,tx,17581,id,default)=="[\"\",\"\",\"\",\"\"]","cada livro do lote é removido com todos os detalhes");
                var audit=await Repository.ReadBookDeletionAudit(c,tx,input.OperationId,default);
                check(audit?.Batch?.Books.Count==2&&audit.BeforeRows.Contains("QTDETANQUE"),"histórico conserva o lote e a cópia anterior de cada livro");
                var repeated=await repo.DeleteBookBatchInTransaction(c,tx,input with {Books=input.Books.Reverse().ToArray()},default);
                check(repeated.AlreadyDeleted&&repeated.Books.Count==2,"repetição do mesmo lote não faz nova exclusão, mesmo com ordem diferente");
                try{await repo.DeleteBookBatchInTransaction(c,tx,input with {Books=[input.Books[0]]},default);check(false,"identificador reutilizado");}
                catch(CorrectionException e){check(e.Status==409,"UUID do lote não pode ser reaproveitado para outra seleção");}
            }
            finally{await tx.RollbackAsync();}
        }
        check(before==await Snapshot(c,null),"rollback restaura todos os livros, fontes e histórico após excluir o lote");
        using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
        {
            try
            {
                using(var cmd=new SqlCommand("INSERT dbo.LMC_WS_VINCULADO(ID_FILIAL,ID_LMC) VALUES(17581,@id);",c,tx))
                {cmd.Parameters.Add("@id",SqlDbType.Int).Value=ids[0];await cmd.ExecuteNonQueryAsync();}
                var blocked=await Repository.PrepareBookBatchDeletionInTransaction(c,tx,selection,default);
                check(blocked.Blockers.Count>0,"confirmação do lote identifica vínculo de envio externo");
                try{await repo.DeleteBookBatchInTransaction(c,tx,new(17581,blocked.Books.Select(b=>new BookBatchItem(b.BookId,b.Version)).ToArray(),Guid.NewGuid()),default);check(false,"vínculo ignorado");}
                catch(CorrectionException e){check(e.Status==422,"envio externo em um livro impede a exclusão do lote");}
                foreach(var id in ids)check(await Repository.BookDeletionRows(c,tx,17581,id,default)!="[\"\",\"\",\"\",\"\"]","livros permanecem inteiros após bloqueio do lote");
            }
            finally{await tx.RollbackAsync();}
        }
        check(before==await Snapshot(c,null),"ensaio de bloqueio não mantém alterações na cópia de desenvolvimento");
    }
    private static async Task<string> Snapshot(SqlConnection c,SqlTransaction? tx)
    {
        var books=await BookWriteChecks.Snapshot(c,tx);
        using var cmd=new SqlCommand("SELECT (SELECT * FROM dbo.HISTORICOTURNORETRO ORDER BY ID_HISTORICOTURNORETRO FOR XML RAW,BINARY BASE64),(SELECT * FROM dbo.LMC_WS_VINCULADO ORDER BY ID_LMC_WS_VINCULADO FOR XML RAW,BINARY BASE64);",c,tx);
        await using var r=await cmd.ExecuteReaderAsync();await r.ReadAsync();
        return BookWriting.Hash(new {books,history=r.IsDBNull(0)?"":r.GetString(0),links=r.IsDBNull(1)?"":r.GetString(1)});
    }
}
