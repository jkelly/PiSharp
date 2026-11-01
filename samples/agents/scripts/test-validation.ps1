$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SampleValidation.psm1') -Force
$expected = @([pscustomobject]@{ tool = 'calculate'; error = $false; result = [pscustomobject]@{ amount = 17; path = 'C:\baseline\receipt.json'; ids = @('A', 'B') } })
$actual = @([pscustomobject]@{ tool = 'calculate'; error = $false; result = [pscustomobject]@{ ids = @('A', 'B'); path = 'C:\live\receipt.json'; amount = 17 } })
if (@(Compare-SampleToolResults $expected $actual 'C:\baseline' 'C:\live').Count) { throw 'Property order or workspace normalization changed equality.' }
$actual[0].result.amount = 18
if (@(Compare-SampleToolResults $expected $actual 'C:\baseline' 'C:\live').Count -ne 1) { throw 'Wrong domain result was accepted.' }
$actual[0].result.amount = 17
$actual[0].result.ids = @('B', 'A')
if (@(Compare-SampleToolResults $expected $actual 'C:\baseline' 'C:\live').Count -ne 1) { throw 'Meaningful array order was ignored.' }
if (@(Compare-SampleToolResults $expected @()).Count -ne 1) { throw 'Missing tool result was accepted.' }
$actual[0].result.ids = @('A', 'B')
$actual[0].error = $true
if (@(Compare-SampleToolResults $expected $actual 'C:\baseline' 'C:\live').Count -ne 1) { throw 'Failed tool result was accepted.' }
Write-Host 'PASS fixture-result validation (normalization, wrong result, order, missing result, failed tool)'
