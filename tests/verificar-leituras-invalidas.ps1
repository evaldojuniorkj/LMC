param([string]$BaseUrl='http://127.0.0.1:5080',[int]$Branch=4500,[string]$Date='2026-02-01')
$ErrorActionPreference='Stop'
$lmcReadingChecks=0
function Check($condition,$label){if(-not $condition){throw "FALHOU: $label"};$script:lmcReadingChecks++;Write-Output "OK: $label"}
# Somente GET, contra a nova cópia de teste: nenhuma gravação ou alteração estrutural.
$lmcReadingDay=Invoke-RestMethod "$BaseUrl/api/lmc/day?branch=$Branch&date=$Date" -TimeoutSec 30
$lmcBadReadings=@($lmcReadingDay.readings|Where-Object {$null-ne $_.invalidVolume})
Check ($lmcBadReadings.Count-gt 0) 'Dia com leituras muito grandes abre sem estouro numérico'
Check (@($lmcBadReadings|Where-Object {$null-ne $_.volume-or $_.canUse}).Count-eq 0) 'Leituras inválidas não viram zero nem podem ser escolhidas'
Check (@($lmcBadReadings|Where-Object {$_.source-and $_.id-and $_.at-and $_.invalidVolume}).Count-eq $lmcBadReadings.Count) 'Fonte, registro, data e valor original continuam identificados'
$lmcTankIds=@($lmcBadReadings|ForEach-Object tankId)
$lmcProductId=($lmcReadingDay.tanks|Where-Object {$_.tankId-in $lmcTankIds}|Select-Object -First 1).productId
$lmcReadingPrep=Invoke-RestMethod "$BaseUrl/api/lmc/prepare?branch=$Branch&date=$Date&product=$lmcProductId" -TimeoutSec 30
Check ($lmcReadingPrep.productId-eq $lmcProductId-and $lmcReadingPrep.date-eq $Date) 'Editor do combustível também carrega na nova base'
Check (@($lmcReadingPrep.warnings|Where-Object {$_-like '*volume inválido*'}).Count-gt 0) 'Editor explica que há leituras inválidas a conferir'
Write-Output "$lmcReadingChecks verificações passaram; nenhuma gravação."
