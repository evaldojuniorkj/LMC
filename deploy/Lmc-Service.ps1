# Funções compartilhadas de diagnóstico; este arquivo não executa a instalação.
function Set-LmcServicePath {
    param([Parameter(Mandatory=$true)][string]$Path)
    # Passa o caminho como dado, preservando aspas e espaços no Windows PowerShell 5.1.
    $target=Get-CimInstance Win32_Service -Filter "Name='LMC'" -ErrorAction Stop
    if(-not $target){throw 'Serviço LMC não localizado ao atualizar o caminho.'}
    $result=Invoke-CimMethod -InputObject $target -MethodName Change -Arguments @{PathName=$Path} -ErrorAction Stop
    if($result.ReturnValue -ne 0){throw "Falha ao atualizar caminho do serviço (Win32_Service.Change, código $($result.ReturnValue))."}
    $actual=Get-CimInstance Win32_Service -Filter "Name='LMC'" -ErrorAction Stop
    if($actual.PathName -cne $Path){throw 'O Windows não confirmou o caminho solicitado para o serviço LMC.'}
}
function Get-LmcServiceFailure {
    param([datetime]$Since,[string]$Summary)
    try {
        $events=@(Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=$Since.AddSeconds(-1);Level=@(1,2)} -MaxEvents 40 -ErrorAction Stop |
            Where-Object {$_.ProviderName -eq 'Lmc.Web' -or ($_.ProviderName -in @('.NET Runtime','Application Error') -and $_.Message -match 'Lmc\.Web') } |
            Select-Object -First 2)
        if($events.Count){
            $details=@($events | ForEach-Object {
                $message=[string]$_.Message
                # Não retornar strings de conexão eventualmente incluídas em eventos.
                $message=[regex]::Replace($message,'(?im)^.*(?:password|pwd|passwordhash)\s*[:=].*$','[linha com credencial omitida]')
                if($message.Length -gt 5000){$message=$message.Substring(0,5000)}
                '{0} — evento {1}: {2}' -f $_.ProviderName,$_.Id,$message
            })
            return $Summary+[Environment]::NewLine+($details -join [Environment]::NewLine)
        }
    }catch{}
    return $Summary+[Environment]::NewLine+'Não foi possível obter um evento recente do LMC. Consulte Logs do Windows > Aplicativo no horário desta tentativa.'
}
