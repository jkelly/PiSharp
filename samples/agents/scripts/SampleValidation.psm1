# Canonical comparison for authored fixture results; this does not judge free-form model prose.
function ConvertTo-SampleCanonicalJson {
    param([AllowNull()]$Value, [string]$Workspace = '')
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [string]) {
        $text = $Value
        if ($Workspace) {
            $text = $text.Replace($Workspace, '<workspace>')
            $text = $text.Replace($Workspace.Replace('\', '/'), '<workspace>')
        }
        return ConvertTo-Json -InputObject $text -Compress
    }
    if ($Value -is [Collections.IDictionary]) {
        $pairs = foreach ($key in @($Value.Keys | Sort-Object -CaseSensitive)) {
            (ConvertTo-Json -InputObject ([string]$key) -Compress) + ':' + (ConvertTo-SampleCanonicalJson $Value[$key] $Workspace)
        }
        return '{' + ($pairs -join ',') + '}'
    }
    if ($Value -is [pscustomobject]) {
        $pairs = foreach ($property in @($Value.PSObject.Properties | Sort-Object Name -CaseSensitive)) {
            (ConvertTo-Json -InputObject $property.Name -Compress) + ':' + (ConvertTo-SampleCanonicalJson $property.Value $Workspace)
        }
        return '{' + ($pairs -join ',') + '}'
    }
    if ($Value -is [Collections.IEnumerable]) {
        $items = foreach ($item in $Value) { ConvertTo-SampleCanonicalJson $item $Workspace }
        return '[' + ($items -join ',') + ']'
    }
    return ConvertTo-Json -InputObject $Value -Compress -Depth 100
}

function Compare-SampleToolResults {
    param([object[]]$Expected, [object[]]$Actual, [string]$ExpectedWorkspace = '', [string]$ActualWorkspace = '')
    foreach ($output in $Expected) {
        if ($output.error) { "Fixture tool $($output.tool) failed; no reliable result expectation."; continue }
        $canonical = ConvertTo-SampleCanonicalJson $output.result $ExpectedWorkspace
        $matches = @($Actual | Where-Object {
            $_.tool -ceq $output.tool -and -not $_.error -and
            (ConvertTo-SampleCanonicalJson $_.result $ActualWorkspace) -ceq $canonical
        })
        if ($matches.Count -eq 0) { "Tool $($output.tool) did not produce the expected fixture result." }
    }
}

Export-ModuleMember -Function Compare-SampleToolResults
