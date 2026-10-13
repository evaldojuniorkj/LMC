$ErrorActionPreference = 'Stop'
$lmcBase = 'http://127.0.0.1:5080'
$lmcChecks = 0
function Check($condition, $label) {
    if (-not $condition) { throw "FALHOU: $label" }
    $script:lmcChecks++
    Write-Output "OK: $label"
}
function Reject($path, $body, $expected, $session = $null, $headers = @{}) {
    try {
        $arguments = @{ Uri="$lmcBase$path"; Method='Post'; ContentType='application/json'; Body=($body | ConvertTo-Json -Depth 8); Headers=$headers }
        if ($null -ne $session) { $arguments.WebSession = $session }
        $null = Invoke-RestMethod @arguments
    } catch {
        Check ([int]$_.Exception.Response.StatusCode -eq $expected) "$path rejeita solicitação com HTTP $expected"
        return
    }
    throw 'Solicitação inválida aceita'
}
$lmcContext = Invoke-RestMethod "$lmcBase/api/context"
$lmcBranch = $lmcContext.branches[0].id
$lmcDate = $lmcContext.branches[0].lastBookDate
$lmcFrom = ([datetime]$lmcDate).ToString('yyyy-MM-01')
$lmcLedgerUrl = "$lmcBase/api/lmc/ledger?branch=$lmcBranch&from=$lmcFrom&until=$lmcDate"
$lmcRows = Invoke-RestMethod $lmcLedgerUrl
Check ($lmcRows.Count -gt 0) 'Grade mensal retorna os livros gravados'
Check (@($lmcRows | Group-Object id | Where-Object Count -gt 1).Count -eq 0) 'Grade não duplica linhas ao somar os detalhes'
$lmcRow = $lmcRows | Where-Object date -eq $lmcDate | Select-Object -First 1
$lmcProduct = $lmcRow.productId
$lmcPrepareUrl = "$lmcBase/api/lmc/prepare?branch=$lmcBranch&date=$lmcDate&product=$lmcProduct"
$lmcPreparation = Invoke-RestMethod $lmcPrepareUrl
Check ($lmcPreparation.bookId -eq $lmcRow.id -and $lmcPreparation.version.Length -eq 64) 'Preparação identifica o livro e sua versão'
Check ($lmcPreparation.tanks.Count -gt 0 -and $lmcPreparation.pumps.Count -gt 0) 'Preparação disponibiliza tanques e bicos'
$lmcDay = Invoke-RestMethod "$lmcBase/api/lmc/day?branch=$lmcBranch&date=$lmcDate"
foreach ($lmcDayRow in @($lmcRows | Where-Object date -eq $lmcDate)) {
    $lmcStored = @($lmcDay.tanks | Where-Object bookId -eq $lmcDayRow.id)
    $lmcLoaded = Invoke-RestMethod "$lmcBase/api/lmc/prepare?branch=$lmcBranch&date=$lmcDate&product=$($lmcDayRow.productId)"
    $lmcSame = $lmcLoaded.savedMeasurements.Count -eq $lmcStored.Count -and $lmcStored.Count -gt 0
    foreach ($lmcTank in $lmcStored) {
        $lmcValue = @($lmcLoaded.savedMeasurements | Where-Object tankId -eq $lmcTank.tankId)
        $lmcSame = $lmcSame -and $lmcValue.Count -eq 1 -and $lmcValue[0].previousClosing -eq $lmcTank.legacyOpening -and $lmcValue[0].closing -eq $lmcTank.legacyClosing
    }
    Check $lmcSame "Livro $($lmcDayRow.id): abertura e fechamento carregados dos valores salvos em cada tanque"
}
$lmcBody = @{
    branch=$lmcBranch; date=$lmcDate; productId=$lmcProduct; version=('0' * 64)
    tanks=@($lmcPreparation.tanks | ForEach-Object { @{tankId=$_.tankId;previousClosing=10000;closing=8000} })
    observation='Solicitação de rejeição com versão inválida; não deve gravar.'
    measurementMethod='manual'; operationId=[guid]::NewGuid().ToString()
}
Reject '/api/lmc/save' $lmcBody 403
$lmcSecurity = Invoke-RestMethod "$lmcBase/api/security" -SessionVariable lmcSession
$lmcHeaders = @{ 'X-LMC-CSRF'=$lmcSecurity.token }
Reject '/api/lmc/save' $lmcBody 409 $lmcSession $lmcHeaders
Reject '/api/lmc/preview' $lmcBody 409
$lmcBody.version = $lmcPreparation.version
$lmcBody.tanks = @($lmcPreparation.tanks | ForEach-Object { @{tankId=$_.tankId;previousClosing=$null;closing=$null} })
$lmcPreview = Invoke-RestMethod "$lmcBase/api/lmc/preview" -Method Post -ContentType 'application/json' -Body ($lmcBody | ConvertTo-Json -Depth 8)
Check (-not $lmcPreview.canSave -and $lmcPreview.blockers.Count -gt 0) 'Prévia mantém medições ausentes bloqueadas'
Reject '/api/lmc/save' $lmcBody 422 $lmcSession $lmcHeaders
$lmcBody.tanks = @($null)
Reject '/api/lmc/save' $lmcBody 400 $lmcSession $lmcHeaders
$lmcAfter = Invoke-RestMethod $lmcPrepareUrl
Check ($lmcAfter.version -eq $lmcPreparation.version) 'Rejeições e prévias preservam livro e fontes'
$lmcRowsAfter = Invoke-RestMethod $lmcLedgerUrl
Check (($lmcRows | ConvertTo-Json -Depth 8 -Compress) -eq ($lmcRowsAfter | ConvertTo-Json -Depth 8 -Compress)) 'Grade mensal permanece inalterada'
Write-Output "$lmcChecks verificações passaram; somente consultas, prévias e solicitações rejeitadas, sem persistência."
