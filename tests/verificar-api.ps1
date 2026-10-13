param([string]$BaseUrl = 'http://127.0.0.1:5080')
$ErrorActionPreference = 'Stop'
$checks = 0
function Assert-Check([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw "FALHOU: $Label" }
    $script:checks++
    Write-Host "OK: $Label"
}
function Get-Api([string]$Path) { Invoke-RestMethod -Uri ($BaseUrl + $Path) }
function Post-Preview($Payload, [int]$ExpectedStatus) {
    $json = $Payload | ConvertTo-Json
    try {
        $response = Invoke-WebRequest -Uri ($BaseUrl + '/api/notes/17581/17581/541442/preview') -Method Post -ContentType 'application/json' -Body $json
        Assert-Check ($response.StatusCode -eq $ExpectedStatus) "HTTP $ExpectedStatus na previa"
        return ($response.Content | ConvertFrom-Json)
    } catch {
        if (-not $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne $ExpectedStatus) { throw }
        Assert-Check $true "HTTP $ExpectedStatus na previa"
    }
}

# Casos de referência: consultas, prévias e tentativas de gravação rejeitadas antes de alterar dados.
$context = Get-Api '/api/context'
Assert-Check ($context.canApply -eq $true -and $context.reasonLimit -eq 80) 'aplicacao oferece correcao com limite do historico existente'
$all = Get-Api '/api/notes?branch=17581'
Assert-Check ($all.summary.total -eq 1000 -and $all.summary.attention -eq 35) 'triagem reproduz 1000 notas e 35 candidatas'
Assert-Check ($all.summary.delayed -eq 5 -and $all.summary.earlier -eq 30) 'grupos de datas coincidem com o diagnostico'
Assert-Check ($all.items.Count -eq 25) 'lista paginada'
$attention = Get-Api '/api/notes?branch=17581&status=attention'
Assert-Check ($attention.total -eq 35) 'filtro somente candidatas'
$note = Get-Api '/api/notes?branch=17581&search=1337740'
Assert-Check ($note.total -eq 1 -and $note.items[0].note.volume -eq 6000) 'busca por numero e volume sem duplicacao'
$crossMonth = Get-Api '/api/notes?branch=17581&search=1337740&from=2026-08-13&until=2026-08-14&dateField=any'
Assert-Check ($crossMonth.total -eq 1) 'qualquer data encontra entrada deslocada para outro mes'
$operational = Get-Api '/api/notes?branch=17581&search=1337740&from=2026-08-13&until=2026-08-14&dateField=operational'
Assert-Check ($operational.total -eq 0) 'filtro operacional respeita o intervalo escolhido'
$empty = Get-Api '/api/notes?branch=17581&search=NOTA_INEXISTENTE_XYZ'
Assert-Check ($empty.total -eq 0 -and $empty.items.Count -eq 0) 'estado vazio'
$page2 = Get-Api '/api/notes?branch=17581&page=2'
Assert-Check ($page2.page -eq 2 -and $page2.items[0].note.id -ne $all.items[0].note.id) 'navegacao entre paginas'
$detail = Get-Api '/api/notes/17581/17581/541442'
Assert-Check ($detail.note.number -eq 1337740 -and $detail.parcels.Count -eq 1) 'detalhe pela chave completa'
Assert-Check ($detail.parcels[0].tank -eq '6') 'tanque sem codigo usa identificador existente'
$shifts = Get-Api '/api/shifts?branch=17581&date=2026-08-14'
Assert-Check ($shifts.Count -eq 2 -and $shifts[0].isGeneral -and $shifts[0].id -eq 1 -and $shifts[1].id -eq 8912) 'destinos incluem turno geral da filial e turno diario da data'
$generalOnly = Get-Api '/api/shifts?branch=17581&date=2026-10-07'
Assert-Check ($generalOnly.Count -eq 1 -and $generalOnly[0].isGeneral) 'turno geral disponivel mesmo sem turno diario cadastrado'
$body = @{ operationalDate='2026-08-14'; shiftId=8912; reason='Teste de previa sem aplicar alteracoes.'; version=$detail.version }
$preview = Post-Preview $body 200
Assert-Check ($preview.canApply -eq $false -and $preview.blockers[0] -like '*fechamento*') 'destino com indicacao de fechamento bloqueia gravacao'
Assert-Check ($preview.bookReceipts.Count -eq 2 -and ($preview.bookReceipts | Where-Object supplierMatches).Count -eq 2) 'exibe recebimento da mesma nota em dois LMCs com fornecedor coincidente'
Assert-Check (($preview.effects | Measure-Object -Property liters -Sum).Sum -eq 0) 'previa conserva os 6000 litros entre datas'
Assert-Check ($preview.effects[0].liters -eq 6000 -and $preview.effects[1].liters -eq -6000) 'sinais da redistribuicao corretos'
Assert-Check ($preview.booksToReview.Count -eq 46) 'inclui LMCs posteriores dos produtos afetados'
$body.shiftId = 1
$generalPreview = Post-Preview $body 200
Assert-Check ($generalPreview.canApply -and $generalPreview.targetShift.isGeneral -and $generalPreview.sourceShiftId -eq $generalPreview.targetShift.id) 'corrige data mantendo turno geral mesmo com turno diario fechado'
$body.operationalDate = '2026-09-04'
Post-Preview $body 400 | Out-Null
$body.operationalDate = '2026-08-14'
$body.shiftId = 8912
$body.version = 'VERSAO_DESATUALIZADA'
Post-Preview $body 409 | Out-Null
$body.version = $detail.version
$body.shiftId = 8933
Post-Preview $body 400 | Out-Null
$body.shiftId = 8912
$body.operationalDate = '2026-02-30'
Post-Preview $body 400 | Out-Null
$body.operationalDate = '2026-08-14'
$body.reason = 'curto'
Post-Preview $body 200 | Out-Null
$body.reason = 'a' * 80
Post-Preview $body 200 | Out-Null
$body.reason = 'a' * 81
Post-Preview $body 400 | Out-Null
$body.operationalDate = '2026-09-30'
$body.shiftId = 8960
foreach ($optionalReason in @('', '   ', $null)) {
    $body.reason = $optionalReason
    $optional = Post-Preview $body 200
    Assert-Check ($optional.canApply -and $optional.reason -eq '') 'observacao vazia ou nula nao bloqueia correcao'
}
$body.Remove('reason')
$available = Post-Preview $body 200
Assert-Check ($available.canApply -and $available.blockers.Count -eq 0 -and $available.reason -eq '') 'destino sem fechamento permite correcao com observacao omitida'
$applyBody = @{ operationalDate='2026-09-30'; shiftId=8960; version='INVALIDA';operationId=[guid]::NewGuid().ToString();acceptBookReview=$true } | ConvertTo-Json
try {
    Invoke-WebRequest -Uri ($BaseUrl+'/api/notes/17581/17581/541442/apply') -Method Post -ContentType 'application/json' -Body $applyBody | Out-Null
    throw 'Gravacao sem token foi aceita.'
} catch {
    if (-not $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 403) { throw }
    Assert-Check $true 'gravacao sem protecao da sessao retorna 403'
}
$security = Invoke-RestMethod -Uri ($BaseUrl+'/api/security') -SessionVariable taskSession
try {
    Invoke-WebRequest -Uri ($BaseUrl+'/api/notes/17581/17581/541442/apply') -Method Post -ContentType 'application/json' -Body $applyBody -WebSession $taskSession -Headers @{'X-LMC-CSRF'=$security.token} | Out-Null
    throw 'Gravacao com versao invalida foi aceita.'
} catch {
    if (-not $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 409) { throw }
    Assert-Check $true 'rota de gravacao com sessao valida rejeita versao antiga sem alterar dados'
}
$queue = Get-Api '/api/review-queue?branch=17581'
Assert-Check ($queue.resolutionAvailable -eq $false) 'fila nao declara recálculo implementado'
$detailAfter = Get-Api '/api/notes/17581/17581/541442'
Assert-Check ($detailAfter.version -eq $detail.version) 'nota e movimentos continuam inalterados apos as previas'
$books = Get-Api '/api/books?branch=17581&date=2026-09-28'
Assert-Check ($books.Count -eq 4) 'segunda etapa consulta quatro livros existentes'
Write-Output "$checks verificacoes HTTP passaram; consultas, previas e gravacoes rejeitadas."
