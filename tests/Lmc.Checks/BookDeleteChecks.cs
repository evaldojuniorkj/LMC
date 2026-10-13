using System.Data;
using Lmc.Web;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

internal static class BookDeleteChecks
{
    public static void Rules(Action<bool,string> check)
    {
        var operation=Guid.NewGuid();
        var request=new BookDeleteRequest(17581,4428,new string('A',64),operation);
        BookDeletion.Validate(request);
        check(BookDeletion.Fingerprint(request)!=BookDeletion.Fingerprint(request with {BookId=4429}),"exclusão identifica exatamente o livro e a versão confirmados");
        var receipt=new BookDeleted(17581,4428,1,"Gasolina","2026-09-24",25,operation,false,[],"2026-09-28");
        var audit=new BookDeletionAudit(BookDeletion.Fingerprint(request),"2026-10-06","operador","<row OBS='Histórico anterior com acentos'/>"+new string('x',800),receipt);
        var lines=BookDeletion.Encode(audit);
        check(lines.All(l=>l.Length<=255)&&BookWriting.Hash(BookDeletion.Decode(operation,lines.Reverse()))==BookWriting.Hash(audit),"cópia da exclusão conserva conteúdo completo no histórico existente");
        try{BookDeletion.Decode(operation,lines.Skip(1));check(false,"histórico incompleto");}
        catch(CorrectionException e){check(e.Status==422,"histórico incompleto impede confirmar a exclusão");}
        try{BookDeletion.Validate(request with {Version="inválida"});check(false,"versão inválida");}
        catch(CorrectionException e){check(e.Status==400,"exclusão sem versão válida é rejeitada");}
    }

    public static async Task Rollback(Action<bool,string> check)
    {
        const string connection="Server=localhost;Database=ATXDADOS;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=5";
        var repo=new Repository(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:AtxDados",connection}}).Build());
        var preview=await repo.PrepareBookDeletion(17581,4428,default);
        check(preview.BookId==4428&&preview.ProductId==1&&preview.TankCount==2&&preview.PumpCount>0&&preview.Blockers.Count==0,"confirmação localiza o livro salvo e seus detalhes sem depender de medições");
        await using var c=new SqlConnection(connection);await c.OpenAsync();
        var before=await Snapshot(c,null);
        using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
        {
            try
            {
                var rows=await Repository.BookDeletionRows(c,tx,17581,4428,default);
                var input=new BookDeleteRequest(17581,4428,preview.Version,Guid.NewGuid());
                try{await repo.DeleteBookInTransaction(c,tx,input with {Version=new string('0',64)},default);check(false,"versão vencida");}
                catch(CorrectionException e){check(e.Status==409,"versão vencida bloqueia a exclusão antes de alterar dados");}
                var receipt=await repo.DeleteBookInTransaction(c,tx,input,default);
                check(!receipt.AlreadyDeleted&&receipt.BookId==4428&&receipt.FollowingBooks.Count==preview.FollowingBooks.Count,"exclusão retorna o livro correto e os posteriores a conferir");
                check(await Repository.BookDeletionRows(c,tx,17581,4428,default)=="[\"\",\"\",\"\",\"\"]","livro, tanques, bicos e recebimentos são removidos juntos");
                var audit=await Repository.ReadBookDeletionAudit(c,tx,input.OperationId,default);
                check(audit?.BeforeRows==rows&&audit.Actor==Repository.Actor,"histórico preserva todas as linhas anteriores e identifica o operador");
                var again=await repo.DeleteBookInTransaction(c,tx,input,default);
                check(again.AlreadyDeleted&&again.BookId==4428,"repetir a solicitação confirma a mesma exclusão sem nova alteração");
                try{await repo.DeleteBookInTransaction(c,tx,input with {BookId=4429},default);check(false,"operação reutilizada");}
                catch(CorrectionException e){check(e.Status==409,"identificador da exclusão não pode ser reutilizado para outro livro");}
                try{await repo.DeleteBookInTransaction(c,tx,input with {OperationId=Guid.NewGuid()},default);check(false,"livro ausente");}
                catch(CorrectionException e){check(e.Status==404,"livro ausente com nova operação exige atualizar a lista");}
            }
            finally{await tx.RollbackAsync();}
        }
        check(before==await Snapshot(c,null),"rollback restaura integralmente livros, notas, medições e histórico da exclusão");
        using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
        {
            try
            {
                foreach(var linkedId in new[]{4428,preview.FollowingBooks[0].Id})
                {
                    using var link=new SqlCommand("INSERT dbo.LMC_WS_VINCULADO(ID_FILIAL,ID_LMC) VALUES(17581,@id);",c,tx);
                    link.Parameters.Add("@id",SqlDbType.Int).Value=linkedId;await link.ExecuteNonQueryAsync();
                    var protectedBook=await Repository.PrepareBookDeletionInTransaction(c,tx,17581,4428,default);
                    check(protectedBook.Blockers.Count>0,"envio externo no livro ou na sequência aparece como impedimento");
                    try{await repo.DeleteBookInTransaction(c,tx,new(17581,4428,protectedBook.Version,Guid.NewGuid()),default);check(false,"envio externo ignorado");}
                    catch(CorrectionException e){check(e.Status==422,"livro vinculado a envio externo não é excluído");}
                    using var clear=new SqlCommand("DELETE dbo.LMC_WS_VINCULADO WHERE ID_FILIAL=17581 AND ID_LMC=@id;",c,tx);
                    clear.Parameters.Add("@id",SqlDbType.Int).Value=linkedId;await clear.ExecuteNonQueryAsync();
                }
            }
            finally{await tx.RollbackAsync();}
        }
        check(before==await Snapshot(c,null),"ensaio de bloqueio preserva todos os livros e vínculos originais");
    }

    private static async Task<string> Snapshot(SqlConnection c,SqlTransaction? tx)
    {
        var books=await BookWriteChecks.Snapshot(c,tx);
        using var cmd=new SqlCommand("SELECT (SELECT * FROM dbo.HISTORICOTURNORETRO ORDER BY ID_HISTORICOTURNORETRO FOR XML RAW,BINARY BASE64),(SELECT * FROM dbo.LMC_WS_VINCULADO ORDER BY ID_LMC_WS_VINCULADO FOR XML RAW,BINARY BASE64);",c,tx);
        await using var r=await cmd.ExecuteReaderAsync();await r.ReadAsync();
        return BookWriting.Hash(new {books,history=r.IsDBNull(0)?"":r.GetString(0),links=r.IsDBNull(1)?"":r.GetString(1)});
    }
}
