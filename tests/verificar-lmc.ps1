$ErrorActionPreference = 'Stop'
$lmcBase = 'http://127.0.0.1:5080'
$lmcChecks = 0
function Check($condition, $label) {
    if (-not $condition) { throw "FALHOU: $label" }
    $script:lmcChecks++
    Write-Output "OK: $label"
}
function Balance($body) {
    Invoke-RestMethod "$lmcBase/api/lmc/balance" -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 8)
}
function Reject($body, $expected) {
    try { $null = Balance $body } catch {
        Check ([int]$_.Exception.Response.StatusCode -eq $expected) "Solicitação inválida retorna HTTP $expected"
        return
    }
    throw 'Solicitação inválida aceita'
}
$lmcContext = Invoke-RestMethod "$lmcBase/api/context"
$lmcBranch = $lmcContext.branches[0].id
$lmcDate = $lmcContext.branches[0].lastBookDate
$lmcDay = Invoke-RestMethod "$lmcBase/api/lmc/day?branch=$lmcBranch&date=$lmcDate"
Check ($lmcDay.books.Count -gt 0 -and $lmcDay.tanks.Count -gt 0) 'Consulta carrega livros e tanques reais'
Check ($lmcDay.pumps.Count -gt 0 -and $lmcDay.archive.Count -gt 0) 'Encerrantes do livro e da automação disponíveis'
Check (@($lmcDay.archive | Where-Object { ([datetime]$_.lastAt).ToString('yyyy-MM-dd') -ne $lmcDate }).Count -eq 0) 'Arquivo respeita o corte do dia, sem capturar o dia seguinte'
Check (@($lmcDay.readings | Where-Object { [string]::IsNullOrEmpty($_.source) -or [string]::IsNullOrEmpty($_.at) }).Count -eq 0) 'Leituras preservam fonte e data'
$lmcProduct = $lmcDay.products | Where-Object canCalculate | Select-Object -First 1
Check ($null -ne $lmcProduct) 'Amostra oferece combustível com fontes suficientes para ensaiar cálculo'
$lmcTanks = @($lmcDay.tanks | Where-Object productId -eq $lmcProduct.id)
$lmcBody = @{ branch=$lmcBranch; date=$lmcDate; productId=$lmcProduct.id; tanks=@($lmcTanks | ForEach-Object { @{tankId=$_.tankId;previousClosing=10000;closing=9000} }) }
$lmcResult = Balance $lmcBody
$lmcExpected = [decimal](10000 * $lmcTanks.Count) + [decimal]$lmcProduct.receipts - [decimal]$lmcProduct.sales
Check ([decimal]$lmcResult.bookStock -eq $lmcExpected) 'Prévia usa abertura informada, entradas atuais e vendas conferidas'
Check ([decimal]$lmcResult.difference -eq ([decimal](9000 * $lmcTanks.Count) - $lmcExpected)) 'Diferença preserva o sinal de perda ou sobra'
foreach ($lmcTank in $lmcBody.tanks) { $lmcTank.closing = 0 }
$lmcResult = Balance $lmcBody
if ($lmcExpected -gt 0) {
    Check ([decimal]$lmcResult.percent -eq -100 -and $lmcResult.aboveReference -eq $true) 'Físico zero com escritural positivo é perda total sobre o escritural'
} else {
    Check ($null -eq $lmcResult.percent -and $null -eq $lmcResult.aboveReference) 'Escritural sem saldo positivo não é classificado como dentro do limite'
}
$lmcBody.tanks[0].previousClosing = $null
$lmcResult = Balance $lmcBody
Check ($null -eq $lmcResult.bookStock) 'Medição ausente permanece pendente'
$lmcBody.tanks[0].previousClosing = -1
Reject $lmcBody 400
$lmcBody.tanks = @($null)
Reject $lmcBody 400
$lmcBody.tanks = @(@{tankId=32767;previousClosing=1;closing=1})
Reject $lmcBody 400
$lmcBody.productId = 2147483647
Reject $lmcBody 404
$lmcBody.date = '2026-02-30'
Reject $lmcBody 400
$lmcNoBook = Invoke-RestMethod "$lmcBase/api/lmc/day?branch=$lmcBranch&date=1900-01-01"
Check (@($lmcNoBook.products | Where-Object canCalculate).Count -eq 0) 'Dia sem livro não recebe balanço presumido'
Write-Output "$lmcChecks verificações passaram; somente consultas e simulações, sem gravação."
