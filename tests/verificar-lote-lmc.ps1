param([string]$BaseUrl='http://127.0.0.1:5080',[int]$Branch=17581)
$ErrorActionPreference='Stop'
$lmcBatchChecks=0
function Check($condition,$label) {
    if(-not $condition){throw "FALHOU: $label"}
    $script:lmcBatchChecks++;Write-Output "OK: $label"
}
function Reject($route,$body,$expected,$session=$null,$headers=@{}) {
    try {
        $lmcBatchArgs=@{Uri="$BaseUrl/api/lmc/$route";Method='Post';ContentType='application/json';Body=($body|ConvertTo-Json -Depth 8);Headers=$headers}
        if($null-ne $session){$lmcBatchArgs.WebSession=$session}
        $null=Invoke-RestMethod @lmcBatchArgs
    } catch {
        Check ([int]$_.Exception.Response.StatusCode-eq $expected) "$route rejeitado com HTTP $expected"
        return
    }
    throw 'Uma solicitação inválida foi aceita.'
}
# Consulta amostras atuais. Nunca envia todas as versões válidas para excluir.
$lmcBatchNext=Invoke-RestMethod "$BaseUrl/api/lmc/next?branch=$Branch&fallback=2026-09-01"
Check ($null-ne $lmcBatchNext.lastBookDate) 'Há livro anterior disponível para o ensaio'
$lmcBatchLast=[datetime]::ParseExact($lmcBatchNext.lastBookDate,'yyyy-MM-dd',$null)
Check ($lmcBatchNext.date-eq $lmcBatchLast.AddDays(1).ToString('yyyy-MM-dd')) 'Inserir usa o dia posterior ao último livro salvo'
$lmcBatchFrom=$lmcBatchLast.AddDays(-30).ToString('yyyy-MM-dd')
$lmcBatchLedger=Invoke-RestMethod "$BaseUrl/api/lmc/ledger?branch=$Branch&from=$lmcBatchFrom&until=$($lmcBatchNext.lastBookDate)"
$lmcBatchChosen=@($lmcBatchLedger|Where-Object productId -eq $lmcBatchNext.productId|Sort-Object date -Descending|Select-Object -First 2)
Check ($lmcBatchChosen.Count-eq 2) 'Dois livros atuais disponíveis para consultar a confirmação'
$lmcBatchSelection=@{branch=$Branch;bookIds=@($lmcBatchChosen|ForEach-Object {$_.id})}
$lmcBatchPreview=Invoke-RestMethod "$BaseUrl/api/lmc/deletion-batch" -Method Post -ContentType 'application/json' -Body ($lmcBatchSelection|ConvertTo-Json)
Check ($lmcBatchPreview.books.Count-eq 2-and @($lmcBatchPreview.books|Where-Object {$_.bookId-notin $lmcBatchSelection.bookIds}).Count-eq 0) 'Confirmação contém exatamente os livros selecionados'
Check (@($lmcBatchPreview.followingBooks|Where-Object {$_.id-in $lmcBatchSelection.bookIds}).Count-eq 0) 'Livros selecionados não aparecem como posteriores remanescentes'
$lmcBatchItems=@($lmcBatchPreview.books|ForEach-Object {@{bookId=$_.bookId;version=$_.version}})
# A versão inválida impede a remoção de qualquer item, mesmo com token válido.
$lmcBatchItems[-1].version='0'*64
$lmcBatchBody=@{branch=$Branch;books=$lmcBatchItems;operationId=[guid]::NewGuid().ToString()}
Reject 'delete-batch' $lmcBatchBody 403
$lmcBatchSecurity=Invoke-RestMethod "$BaseUrl/api/security" -SessionVariable lmcBatchSession
$lmcBatchHeaders=@{'X-LMC-CSRF'=$lmcBatchSecurity.token}
Reject 'delete-batch' $lmcBatchBody 409 $lmcBatchSession $lmcBatchHeaders
Reject 'deletion-batch' @{branch=$Branch;bookIds=@()} 400
$lmcBatchAfter=Invoke-RestMethod "$BaseUrl/api/lmc/deletion-batch" -Method Post -ContentType 'application/json' -Body ($lmcBatchSelection|ConvertTo-Json)
Check (($lmcBatchAfter.books.version-join ',')-eq ($lmcBatchPreview.books.version-join ',')) 'Todos os livros permanecem intactos após as rejeições'
Write-Output "$lmcBatchChecks verificações passaram; nenhuma exclusão real."
