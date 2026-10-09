# Prints "total passed failed defect" for one TRX file, or for the single TRX file in a
# directory: every result, the passes, the failures, and the failures that are the known
# MsQuic rebind defect, namely QuicRuntimeRebindTest stopping at bind with
# AddressAlreadyInUse. Callers decide what to tolerate; see docs/project/http-engine.md.
param([Parameter(Mandatory = $true)][string] $Path)
$ErrorActionPreference = 'Stop'

$files = @(if (Test-Path -LiteralPath $Path -PathType Container) {
        Get-ChildItem -LiteralPath $Path -Filter '*.trx' -File
    } else { Get-Item -LiteralPath $Path })
if ($files.Count -ne 1) { throw "Expected exactly one TRX file at '$Path', found $($files.Count)." }

$settings = [System.Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$reader = [System.Xml.XmlReader]::Create($files[0].FullName, $settings)
try {
    $document = [System.Xml.XmlDocument]::new()
    $document.Load($reader)
} finally { $reader.Dispose() }

$namespaces = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
$namespaces.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
$rawIds = @($document.SelectNodes("//t:UnitTest[t:TestMethod/@className='EmbedIO.Tests.QuicRuntimeRebindTest']", $namespaces) |
        ForEach-Object { $_.GetAttribute('id') })
$results = @($document.SelectNodes('//t:UnitTestResult', $namespaces))
$failed = @($results | Where-Object { $_.GetAttribute('outcome') -eq 'Failed' })
$defect = @($failed | Where-Object {
        $output = $_.SelectSingleNode('t:Output/t:StdOut', $namespaces)
        $message = $_.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $namespaces)
        ($rawIds -contains $_.GetAttribute('testId')) -and
        ($null -ne $output) -and $output.InnerText.Contains('stage=bind,') -and
        ($null -ne $message) -and $message.InnerText.Contains('SocketErrorCode: AddressAlreadyInUse')
    })
$passed = @($results | Where-Object { $_.GetAttribute('outcome') -eq 'Passed' })
Write-Output ('{0} {1} {2} {3}' -f $results.Count, $passed.Count, $failed.Count, $defect.Count)
