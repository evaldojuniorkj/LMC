param([string]$BaseUrl='http://127.0.0.1:5080',[int]$Branch=17581,[int]$Product=1)
$ErrorActionPreference='Stop'
$lmcContinuityChecks=0
function Check($condition,$label) {
    if(-not $condition){throw "FALHOU: $label"}
    $script:lmcContinuityChecks++;Write-Output "OK: $label"
}
# Consultas apenas: a simulação de pular datas não grava ou exclui livros.
$lmcNext=Invoke-RestMethod "$BaseUrl/api/lmc/next?branch=$Branch&product=$Product&fallback=2026-09-01"
Check ($null-ne $lmcNext.lastBookDate) 'Há histórico disponível para consultar continuidade'
$lmcPrep=Invoke-RestMethod "$BaseUrl/api/lmc/prepare?branch=$Branch&product=$Product&date=$($lmcNext.date)"
Check ($lmcPrep.productId-eq $Product-and $lmcPrep.date-eq $lmcNext.date) 'Consulta preserva combustível e data de inserção'
Check ($null-ne $lmcPrep.missingPeriods) 'API informa períodos pendentes separadamente dos bloqueios'
$lmcPending=Invoke-RestMethod "$BaseUrl/api/lmc/pending?branch=$Branch"
$lmcPendingProduct=$lmcPending|Where-Object productId -eq $Product
Check ($null-ne $lmcPendingProduct-and $lmcPendingProduct.lastBookDate-eq $lmcNext.lastBookDate) 'Menu de pendências consulta o último livro do combustível'
Check (($lmcPendingProduct.missingPeriods|ConvertTo-Json -Depth 5)-eq ($lmcPrep.missingPeriods|ConvertTo-Json -Depth 5)) 'Menu independente encontra os mesmos intervalos sem carregar o editor'
Check (@($lmcPrep.missingPeriods|Where-Object {$_.until-ge $lmcNext.date-or $_.from-gt $_.until-or $_.days-le 0}).Count-eq 0) 'Sugestões incluem somente dias anteriores ao movimento'
$lmcLater=[datetime]::ParseExact($lmcNext.date,'yyyy-MM-dd',$null).AddDays(2).ToString('yyyy-MM-dd')
$lmcLaterPrep=Invoke-RestMethod "$BaseUrl/api/lmc/prepare?branch=$Branch&product=$Product&date=$lmcLater"
$lmcTrailing=@($lmcLaterPrep.missingPeriods|Where-Object from -eq $lmcNext.date)
Check ($lmcTrailing.Count-eq 1-and $lmcTrailing[0].days-eq 2) 'Pular dois dias manualmente acrescenta exatamente esse período à sugestão'
$lmcNextAfter=Invoke-RestMethod "$BaseUrl/api/lmc/next?branch=$Branch&product=$Product&fallback=2026-09-01"
Check ($lmcNextAfter.date-eq $lmcNext.date-and $lmcNextAfter.lastBookDate-eq $lmcNext.lastBookDate) 'Consulta das sugestões não cria livros nem altera a próxima data'
Write-Output "$lmcContinuityChecks verificações passaram; somente leitura."
