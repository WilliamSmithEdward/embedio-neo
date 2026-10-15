# Shared process ownership for the two standalone comparison drivers.
function Start-ComparisonProcess([string]$Runner, [string[]]$Options) {
    $info=[Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.UseShellExecute=$false
    $info.CreateNoWindow=$true
    $info.RedirectStandardInput=$true
    $info.RedirectStandardOutput=$true
    $info.RedirectStandardError=$true
    $info.ArgumentList.Add($Runner)
    foreach($option in $Options) { $info.ArgumentList.Add($option) }
    $child=@{ Process=[Diagnostics.Process]::Start($info); Errors=$null }
    try {
        $child.Errors=$child.Process.StandardError.ReadToEndAsync()
        return $child
    } catch {
        Stop-ComparisonProcesses @($child) $null
        throw
    }
}

function Stop-ComparisonProcesses([object[]]$Children, [string]$ErrorLog) {
    $failures=[Collections.Generic.List[Exception]]::new()
    foreach($child in $Children) {
        if($null -eq $child){continue}
        try {
            if(!$child.Process.HasExited) {
                try { $child.Process.Kill($true) }
                catch [InvalidOperationException] { if(!$child.Process.HasExited){throw} }
                if(!$child.Process.WaitForExit(10000)){throw 'Owned comparison child did not stop within ten seconds.'}
            }
            if($null -ne $child.Errors) {
                $errorText=$child.Errors.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
                if($errorText -and $ErrorLog){$errorText | Add-Content -LiteralPath $ErrorLog}
            }
        } catch { $failures.Add($_.Exception) }
        finally {
            try { $child.Process.Dispose() }
            catch { $failures.Add($_.Exception) }
        }
    }
    if($failures.Count){throw [AggregateException]::new('Comparison child cleanup failed.', $failures)}
}
