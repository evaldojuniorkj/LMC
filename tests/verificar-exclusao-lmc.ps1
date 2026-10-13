param([string]$BaseUrl='http://127.0.0.1:5080')
$ErrorActionPreference='Stop'
$lmcDeleteChecks=0
function Check($condition,$label) {
    if(-not $condition){throw "FALHOU: $label"}
    $script:lmcDeleteChecks++;Write-Output "OK: $label"
}
function Reject($body,$expected,$session=$null,$headers=@{}) {
    try {
        $lmcDeleteRequestArgs=@{Uri="$BaseUrl/api/lmc/delete";Method='Post';ContentType='application/json';Body=($body|ConvertTo-Json);Headers=$headers}
        if($null-ne $session){$lmcDeleteRequestArgs.WebSession=$session}
        $null=Invoke-RestMethod @lmcDeleteRequestArgs
    } catch {
        Check ([int]$_.Exception.Response.StatusCode-eq $expected) "Exclusão rejeitada com HTTP $expected"
        return
    }
    throw 'Uma exclusão inválida foi aceita.'
}
# Somente leitura e rejeições: nunca fornece uma versão válida ao endpoint de exclusão.
$lmcDeletePreview=Invoke-RestMethod "$BaseUrl/api/lmc/deletion?branch=17581&id=4428"
Check ($lmcDeletePreview.bookId-eq 4428-and $lmcDeletePreview.date-eq '2026-09-24'-and $lmcDeletePreview.productId-eq 1) 'Confirmação identifica o livro por filial e ID'
Check ($lmcDeletePreview.tankCount-gt 0-and $lmcDeletePreview.pumpCount-gt 0-and $lmcDeletePreview.version.Length-eq 64) 'Confirmação informa detalhes e versão'
$lmcDeleteBody=@{branch=17581;bookId=4428;version=('0'*64);operationId=[guid]::NewGuid().ToString()}
Reject $lmcDeleteBody 403
$lmcDeleteSecurity=Invoke-RestMethod "$BaseUrl/api/security" -SessionVariable lmcDeleteSession
$lmcDeleteHeaders=@{'X-LMC-CSRF'=$lmcDeleteSecurity.token}
Reject $lmcDeleteBody 409 $lmcDeleteSession $lmcDeleteHeaders
$lmcDeleteBody.bookId=-1
Reject $lmcDeleteBody 400 $lmcDeleteSession $lmcDeleteHeaders
$lmcDeleteAfter=Invoke-RestMethod "$BaseUrl/api/lmc/deletion?branch=17581&id=4428"
Check ($lmcDeleteAfter.version-eq $lmcDeletePreview.version) 'Livro e detalhes permanecem intactos após consultas e rejeições'
Write-Output "$lmcDeleteChecks verificações passaram; nenhuma exclusão real."
