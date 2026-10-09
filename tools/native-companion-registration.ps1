. (Join-Path $PSScriptRoot 'native-source-admission.ps1')

function Resolve-NativeCompanionPath {
    param([string]$Repo, [string]$Relative)
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative.Contains(':') -or @($Relative.Replace('\', '/').Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) {
        throw "Invalid repository-relative companion path: $Relative"
    }
    $basePath = [IO.Path]::GetFullPath($Repo).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $candidatePath = [IO.Path]::GetFullPath((Join-Path $basePath $Relative))
    if (-not $candidatePath.StartsWith($basePath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Companion path must stay within the repository: $Relative"
    }
    return $candidatePath
}

function Get-NativeCompanionRegistration {
    param([string]$Repo, [switch]$RequireLockFiles)
    $registryPath = Join-Path $Repo 'tools/native-companion-targets.json'
    $registration = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json
    $requiredIds = @('anchored', 'anchored-controls', 'binding', 'binding-controls', 'kitty-locks', 'cache', 'cache-controls',
        'reasoning', 'reasoning-controls', 'keypad', 'modify-other-keys', 'grammar', 'grammar-agent', 'grammar-controls',
        'grammar-strict', 'focus', 'thinking', 'thinking-agent', 'indic-conjuncts', 'simple', 'kitty-alternate', 'terminal-keybindings', 'pi-messages', 'google-generative-ai',
        'terminal-select-list', 'terminal-select-dialog', 'anthropic-simple', 'authentication', 'terminal-ownership-focused', 'node-loadout-metadata', 'azure-responses', 'mistral-text')
    if ($registration.schemaVersion -ne 1 -or $registration.requiredCount -ne $requiredIds.Count -or $registration.targets.Count -ne $requiredIds.Count -or
        @($registration.targets.id).Count -ne $requiredIds.Count -or ($registration.targets.id -join ',') -cne ($requiredIds -join ',') -or
        @(Compare-Object ($requiredIds | Sort-Object) ($registration.targets.id | Sort-Object)).Count) {
        throw 'The full native gate requires all current 30 companions plus the reviewed Azure and Mistral consumers.'
    }
    foreach ($field in @('id', 'project', 'entryAssembly', 'reportName')) {
        if (@($registration.targets | ForEach-Object { $_.$field } | Sort-Object -Unique).Count -ne $requiredIds.Count) {
            throw "Companion $field values must be unique."
        }
    }
    $solutionText = Get-Content -LiteralPath (Join-Path $Repo 'PiSharp.slnx') -Raw
    foreach ($target in $registration.targets) {
        $projectPath = Resolve-NativeCompanionPath $Repo $target.project
        if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $projectPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $target.projectSha256) {
            throw "Registered companion project changed: $($target.project)"
        }
        if (-not $solutionText.Contains('<Project Path="' + $target.project + '" />')) {
            throw "Companion missing from solution: $($target.project)"
        }
        if ($target.reportName -notmatch '^[a-z0-9-]+\.json$' -or $target.timeoutSeconds -lt 1) {
            throw "Invalid companion output/deadline: $($target.id)"
        }
        [void](Resolve-NativeCompanionPath $Repo $target.entryAssembly)
        if (@($target.arguments | Where-Object kind -eq 'report').Count -ne 1) {
            throw "Companion must have one fresh report argument: $($target.id)"
        }
        foreach ($argument in $target.arguments) {
            if ($argument.kind -notin @('literal', 'fixture', 'report', 'reviewedRoot', 'sourceManifest', 'sourceManifestSha256', 'buildReceipt', 'buildReceiptSha256')) { throw 'Unknown companion argument kind.' }
            if ($argument.kind -eq 'fixture' -and -not @($target.fixturePins | Where-Object path -eq $argument.path).Count) {
                throw "Unpinned companion fixture argument: $($argument.path)"
            }
        }
        if ($target.id -eq 'node-loadout-metadata' -and
            ($target.project -cne 'tools/NodeCommandInputBridge/NativeAdmissionTests/NativeAdmissionTests.csproj' -or
             $target.entryAssembly -cne 'tools/NodeCommandInputBridge/NativeAdmissionTests/bin/Release/net10.0/NativeAdmissionTests.dll' -or
             $target.reportName -cne 'node-loadout-metadata.json' -or $target.timeoutSeconds -ne 60 -or
             ($target.arguments.kind -join ',') -cne 'literal,literal,report' -or
             $target.arguments[0].value -cne '--metadata-only' -or $target.arguments[1].value -cne '--report' -or
             $target.fixturePins.Count -ne 0 -or $target.outputFixturePins.Count -ne 0 -or
             $target.lockFile.path -cne 'tools/NodeCommandInputBridge/NativeAdmissionTests/packages.lock.json' -or
             $target.lockFile.status -cne 'PINNED_EXISTING')) {
            throw 'Node loadout metadata admission must remain the exact no-worker adapter consumer.'
        }
        if ($target.id -eq 'pi-messages' -and
            (($target.arguments.kind -join ',') -cne 'fixture,report' -or
            $target.arguments[0].path -cne 'tests/PiSharp.PiMessages.Tests/authored-cases-r2.json')) {
            throw 'Pi Messages requires the retained authored fixture and fresh report as its two ordered arguments.'
        }
        if ($target.id -eq 'google-generative-ai' -and
            (($target.arguments.kind -join ',') -cne 'literal,report' -or $target.arguments[0].value -cne '--report')) {
            throw 'Google requires --report and one fresh report path; all authored families run in their retained order.'
        }
        if ($target.id -eq 'anthropic-simple' -and
            (($target.arguments.kind -join ',') -cne 'literal,report' -or $target.arguments[0].value -cne '--report')) {
            throw 'Anthropic Simple requires --report and one fresh report path; authored groups remain in their retained order.'
        }
        if ($target.id -eq 'anthropic-simple' -and
            ($target.project -cne 'tests/PiSharp.AnthropicSimple.Tests/PiSharp.AnthropicSimple.Tests.csproj' -or
            $target.entryAssembly -cne 'tests/PiSharp.AnthropicSimple.Tests/bin/Release/net10.0/PiSharp.AnthropicSimple.Tests.dll' -or
            $target.reportName -cne 'anthropic-simple.json' -or $target.timeoutSeconds -ne 1800 -or
            $target.fixturePins.Count -ne 1 -or $target.fixturePins[0].path -cne 'tests/PiSharp.AnthropicSimple.Tests/source-inventory.json' -or
            $target.outputFixturePins.Count -ne 0 -or $target.lockFile.path -cne 'tests/PiSharp.AnthropicSimple.Tests/packages.lock.json' -or
            $target.lockFile.status -cne 'PINNED_EXISTING')) { throw 'Anthropic Simple requires its exact reviewed project, output, inventory, lock and deadline.' }
        if ($target.id -eq 'authentication' -and
            (($target.arguments.kind -join ',') -cne 'literal,report' -or $target.arguments[0].value -cne '--report')) {
            throw 'Authentication requires --report and one fresh report path; authored cases remain in their retained order.'
        }
        if ($target.id -eq 'authentication' -and
            ($target.project -cne 'tests/PiSharp.Authentication.Tests/PiSharp.Authentication.Tests.csproj' -or
            $target.entryAssembly -cne 'tests/PiSharp.Authentication.Tests/bin/Release/net10.0/PiSharp.Authentication.Tests.dll' -or
            $target.reportName -cne 'authentication.json' -or $target.timeoutSeconds -ne 60 -or
            $target.fixturePins.Count -ne 0 -or $target.outputFixturePins.Count -ne 0 -or
            $target.lockFile.path -cne 'tests/PiSharp.Authentication.Tests/packages.lock.json' -or
            $target.lockFile.status -cne 'PINNED_EXISTING')) { throw 'Authentication requires its exact reviewed project, output, lock and deadline.' }
        if ($target.id -ceq 'azure-responses' -and
            (($target.arguments.kind -join ',') -cne 'literal,report' -or $target.arguments[0].value -cne '--report')) {
            throw 'AZURE-RESPONSES-ARGUMENTS: Exact --report and fresh report order required.'
        }
        if ($target.id -ceq 'azure-responses' -and
            ($target.project -cne 'tests/PiSharp.AzureResponses.Tests/PiSharp.AzureResponses.Tests.csproj' -or $target.entryAssembly -cne 'tests/PiSharp.AzureResponses.Tests/bin/Release/net10.0/PiSharp.AzureResponses.Tests.dll' -or
            $target.reportName -cne 'azure-responses.json' -or $target.timeoutSeconds -ne 1800 -or
            $target.projectSha256 -cne 'dd80069b3545d6291634e031ed2740773ad0af1535420f5ac44ef6451c0218c2' -or $target.fixturePins.Count -ne 1 -or
            $target.fixturePins[0].path -cne 'tests/PiSharp.AzureResponses.Tests/source-inventory.json' -or $target.outputFixturePins.Count -ne 0 -or
            $target.lockFile.path -cne 'tests/PiSharp.AzureResponses.Tests/packages.lock.json' -or $target.lockFile.bytes -ne 443 -or
            $target.lockFile.sha256 -cne '2af4846d277f656313e804866b8c1adb8859481b6237ca8ad8188896090a15a9' -or $target.lockFile.status -cne 'PINNED_EXISTING')) {
            throw 'AZURE-RESPONSES-TARGET: Exact reviewed project/output/inventory/lock/deadline required.'
        }
        if ($target.id -ceq 'mistral-text' -and
            (($target.arguments.kind -join ',') -cne 'literal,report' -or $target.arguments[0].value -cne '--report')) {
            throw 'MISTRAL-TEXT-ARGUMENTS: Exact --report and fresh report order required.'
        }
        if ($target.id -ceq 'mistral-text' -and
            ($target.project -cne 'tests/PiSharp.MistralConversations.Tests/PiSharp.MistralConversations.Tests.csproj' -or $target.entryAssembly -cne 'tests/PiSharp.MistralConversations.Tests/bin/Release/net10.0/PiSharp.MistralConversations.Tests.dll' -or
            $target.reportName -cne 'mistral-text.json' -or $target.timeoutSeconds -ne 60 -or
            $target.projectSha256 -cne '5b39ed009ad7fad5a5972ae64105cad9242c73b3a0a050dac5b0f972f3594582' -or $target.fixturePins.Count -ne 1 -or
            $target.fixturePins[0].path -cne 'src/PiSharp.AI/Protocols/MistralConversations/source-inventory.json' -or $target.outputFixturePins.Count -ne 0 -or
            $target.lockFile.path -cne 'tests/PiSharp.MistralConversations.Tests/packages.lock.json' -or $target.lockFile.bytes -ne 163 -or
            $target.lockFile.sha256 -cne '56f1771a69e65e2d5fe591f44a8b0344755bebc7ff94255c2c622ae969801f41' -or $target.lockFile.status -cne 'PINNED_EXISTING')) {
            throw 'MISTRAL-TEXT-TARGET: Exact reviewed project/output/inventory/lock/deadline required.'
        }
        if ($target.id -eq 'terminal-ownership-focused' -and
            ($target.project -cne 'tests/PiSharp.Terminal.Ownership.Tests/PiSharp.Terminal.Ownership.Tests.csproj' -or
            $target.entryAssembly -cne 'tests/PiSharp.Terminal.Ownership.Tests/bin/Release/net10.0/PiSharp.CodingAgent.Tests.dll' -or
            $target.reportName -cne 'terminal-ownership-focused.json' -or $target.timeoutSeconds -ne 1800 -or
            $target.partialScope -cne 'PARTIAL_SELECTOR_OWNERSHIP_31' -or $target.selectedOriginalOwnershipCases -ne 31 -or
            $target.originalSelectorPlannedCases -ne 140 -or $target.fullSelectorAcceptance -isnot [bool] -or $target.fullSelectorAcceptance)) {
            throw 'SELECTOR-OWNERSHIP: Exact independent project/output/deadline and partial scope required.'
        }
        if ($target.id -eq 'kitty-alternate') {
            $heldKinds = @('fixture', 'report', 'reviewedRoot', 'sourceManifest', 'sourceManifestSha256', 'buildReceipt', 'buildReceiptSha256')
            if (($target.arguments.kind -join ',') -ne ($heldKinds -join ',')) { throw 'Held-I/O admission requires all seven ordered arguments.' }
        } elseif ($target.id -eq 'terminal-keybindings') {
            $bindingKinds = @('report', 'reviewedRoot', 'sourceManifest', 'sourceManifestSha256', 'buildReceipt', 'buildReceiptSha256')
            if (($target.arguments.kind -join ',') -ne ($bindingKinds -join ',')) { throw 'Terminal keybindings admission requires all six ordered arguments.' }
        } elseif ($target.id -in @('terminal-select-list', 'terminal-select-dialog', 'terminal-ownership-focused')) {
            $selectorKinds = @('report', 'reviewedRoot', 'sourceManifest', 'sourceManifestSha256', 'buildReceipt', 'buildReceiptSha256')
            if (($target.arguments.kind -join ',') -cne ($selectorKinds -join ',')) { throw 'Selector admission requires all six ordered arguments.' }
        } elseif (@($target.arguments | Where-Object { $_.kind -in @('reviewedRoot', 'sourceManifest', 'sourceManifestSha256', 'buildReceipt', 'buildReceiptSha256') }).Count) {
            throw 'Review context arguments may only belong to the five exact admitted terminal consumers.'
        }
        foreach ($fixture in $target.fixturePins) {
            $fixturePath = Resolve-NativeCompanionPath $Repo $fixture.path
            if (-not (Test-Path -LiteralPath $fixturePath -PathType Leaf) -or
                (Get-Item -LiteralPath $fixturePath).Length -ne $fixture.bytes -or
                (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $fixture.sha256) {
                throw "Pinned companion fixture changed: $($fixture.path)"
            }
        }
        $lockPath = Resolve-NativeCompanionPath $Repo $target.lockFile.path
        if ($target.lockFile.status -eq 'PENDING_PERMITTED_RESTORE_GENERATION') {
            if ($RequireLockFiles) { throw "Pending permitted restore must generate and freeze the real lockfile before the full native gate: $($target.lockFile.path)" }
        } elseif ($target.lockFile.status -ne 'PINNED_EXISTING' -or
            -not (Test-Path -LiteralPath $lockPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $target.lockFile.sha256) {
            throw "Registered companion lockfile changed: $($target.lockFile.path)"
        }
    }
    Assert-NativeAnthropicSimpleSourcePins -Repo $Repo
    Assert-NativeAuthenticationSourcePins -Repo $Repo
    Assert-NativeAzureResponsesSourcePins -Repo $Repo
    Assert-NativeMistralTextSourcePins -Repo $Repo
    Assert-NativeSelectorOwnershipSource -Repo $Repo
    return $registration
}

function Get-NativeAnthropicSimpleSourcePins {
    # Exact independently reviewed feature 18b2a7f; future feature edits require
    # a separately reviewed successor, not changed pinned expectations.
    return @(
        @{ relative = 'docs/contracts/anthropic-simple-options.md'; bytes = 7416; sha256 = 'e696955cdfd1b23f2c380e520961a0090eacd95a2d49f645cccd6282d6431d60' },
        @{ relative = 'src/PiSharp.AI/Protocols/AnthropicMessages/AnthropicMessagesSimpleContextEstimator.cs'; bytes = 6693; sha256 = 'f697ab389b1dee37ffdc367c0784b17eec6d31be634773c739eafced6d681f9b' },
        @{ relative = 'src/PiSharp.AI/Protocols/AnthropicMessages/AnthropicMessagesSimpleOptions.cs'; bytes = 1937; sha256 = 'b1ca92bc83c58ddac42ee2114da84f63ab2d5d238c6d017fa8547c0e99037dd8' },
        @{ relative = 'src/PiSharp.AI/Protocols/AnthropicMessages/AnthropicMessagesSimpleRequestFactory.cs'; bytes = 9024; sha256 = 'd699c33da5236b1295033f1652f0b61283a7097ea8d76677d1a3c23f58b761d0' },
        @{ relative = 'tests/PiSharp.AnthropicSimple.Tests/PiSharp.AnthropicSimple.Tests.csproj'; bytes = 208; sha256 = 'dd80069b3545d6291634e031ed2740773ad0af1535420f5ac44ef6451c0218c2' },
        @{ relative = 'tests/PiSharp.AnthropicSimple.Tests/Program.cs'; bytes = 30049; sha256 = '69ef753929e575064df63ae432d2b5458142e0e7bfeb50052d18f94308e02220' },
        @{ relative = 'tests/PiSharp.AnthropicSimple.Tests/packages.lock.json'; bytes = 443; sha256 = '2af4846d277f656313e804866b8c1adb8859481b6237ca8ad8188896090a15a9' },
        @{ relative = 'tests/PiSharp.AnthropicSimple.Tests/source-inventory.json'; bytes = 1338; sha256 = 'bfa6cb9280c250b76cb5ef9db97fb50737e62f14517d83ed614238cd6d81634c' }
    )
}

function Assert-NativeAnthropicSimpleSourcePins {
    param([string]$Repo, [object]$Source)
    foreach ($pin in (Get-NativeAnthropicSimpleSourcePins)) {
        $file = Resolve-NativeCompanionPath $Repo $pin.relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $pin.bytes -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pin.sha256) {
            throw "ANTHROPIC-SIMPLE-SOURCE: Reviewed feature bytes changed: $($pin.relative)"
        }
        if ($PSBoundParameters.ContainsKey('Source')) {
            $matching = @($Source.files | Where-Object relative -CEQ $pin.relative)
            if ($matching.Count -ne 1 -or $matching[0].bytes -ne $pin.bytes -or $matching[0].sha256 -cne $pin.sha256) {
                throw "ANTHROPIC-SIMPLE-SOURCE: Required exact feature pin absent/changed/duplicate: $($pin.relative)"
            }
        }
    }
}

function Get-NativeAzureResponsesSourcePins {
    # Exact independently reviewed source be0ae03455f37aca0252d2bdda48c356f381c08c; includes the composed shared Mistral routing where applicable.
    return @(
        @{ relative = 'compatibility/azure-responses-explicit-adapter.json'; bytes = 827; sha256 = '7b7928753e6281069bb493c286237ff59a46a039560c6e415324bfd6ba50e950' },
        @{ relative = 'docs/contracts/azure-responses-explicit-adapter.md'; bytes = 10693; sha256 = '730ed61331a599a554fef00e0bafefb21bf88022fc6a4c09557c11c79d74bf17' },
        @{ relative = 'src/PiSharp.AI/Protocols/AzureResponses/AzureResponsesOptions.cs'; bytes = 3237; sha256 = '5743cb3b31a9d33c84f8cab77f70721811dd4aa56ab7450bed8418760a4cb23a' },
        @{ relative = 'src/PiSharp.AI/Protocols/AzureResponses/AzureResponsesRequestFactory.cs'; bytes = 17443; sha256 = 'd3606f53f331156bcd88da2fcb7c1de00c51cbb7f4c8943d70942b8c6faaf06f' },
        @{ relative = 'src/PiSharp.AI/Protocols/AzureResponses/AzureResponsesTransport.cs'; bytes = 14136; sha256 = '78986051ee53abcfb5758ecb22e157750464f3f532a74b00b8d5bcce83ca0b1d' },
        @{ relative = 'tests/PiSharp.AzureResponses.Tests/PiSharp.AzureResponses.Tests.csproj'; bytes = 208; sha256 = 'dd80069b3545d6291634e031ed2740773ad0af1535420f5ac44ef6451c0218c2' },
        @{ relative = 'tests/PiSharp.AzureResponses.Tests/Program.cs'; bytes = 41056; sha256 = '428a6f44edff56ea5c5872c36616258370446580a9ba38d8985575416fdd9358' },
        @{ relative = 'tests/PiSharp.AzureResponses.Tests/packages.lock.json'; bytes = 443; sha256 = '2af4846d277f656313e804866b8c1adb8859481b6237ca8ad8188896090a15a9' },
        @{ relative = 'tests/PiSharp.AzureResponses.Tests/source-inventory.json'; bytes = 5338; sha256 = '6b86896531bb84a040584adf68bf676b83b6dc8b9e0055996c7696bbd6d104f4' }
    )
}

function Assert-NativeAzureResponsesSourcePins {
    param([string]$Repo, [object]$Source)
    foreach ($pin in (Get-NativeAzureResponsesSourcePins)) {
        $file = Resolve-NativeCompanionPath $Repo $pin.relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $pin.bytes -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pin.sha256) {
            throw "AZURE-RESPONSES-SOURCE: Reviewed feature bytes changed: $($pin.relative)"
        }
        if ($PSBoundParameters.ContainsKey('Source')) {
            $matching = @($Source.files | Where-Object relative -CEQ $pin.relative)
            if ($matching.Count -ne 1 -or $matching[0].bytes -ne $pin.bytes -or $matching[0].sha256 -cne $pin.sha256) {
                throw "AZURE-RESPONSES-SOURCE: Required exact feature pin absent/changed/duplicate: $($pin.relative)"
            }
        }
    }
}

function Get-NativeMistralTextSourcePins {
    # Exact independently reviewed source 3d4474e530392ea6ed057ad7a8d70d81ae23e701; includes the composed shared Mistral routing where applicable.
    return @(
        @{ relative = 'src/PiSharp.AI/Protocols/MistralConversations/HANDOFF.md'; bytes = 6797; sha256 = '7349d9dc105a9e3d539c1c05daa89b0b2a5823de2c6ef6e7114961df02bea7ee' },
        @{ relative = 'src/PiSharp.AI/Protocols/MistralConversations/MistralTextHttpSseTransport.cs'; bytes = 20533; sha256 = '9ba3b3876e8b364d5ae4c23639c4763b75944bbee45860b1c3b66717c22211b5' },
        @{ relative = 'src/PiSharp.AI/Protocols/MistralConversations/MistralTextOptions.cs'; bytes = 3350; sha256 = '1f33fe7969c92fcc6ebe35b970442e5204d21a0fc50d1d6c960b52bbb056d881' },
        @{ relative = 'src/PiSharp.AI/Protocols/MistralConversations/TextState.cs'; bytes = 8098; sha256 = '1c67a3f0e95eb0e6190c2154d8ff017d293ef9c607a515111f9b5608e621c9eb' },
        @{ relative = 'src/PiSharp.AI/Protocols/MistralConversations/integration.proposed.json'; bytes = 3273; sha256 = '597371cc7759bf7364e7395f5153d52b70a2ab01b89dacc5de9e748eab0c2e91' },
        @{ relative = 'src/PiSharp.AI/Protocols/MistralConversations/source-inventory.json'; bytes = 7822; sha256 = '03c8bcc30506bb106dfb1aa4aee2efb8d62383f5f65ac873f1a2d29f2f641510' },
        @{ relative = 'src/PiSharp.AI/Streaming/ChatRun.cs'; bytes = 29223; sha256 = '11eb6d6bc714b20ec40bbb8815057679f6a134485f9f26fdaa7d7b2c633ca646' },
        @{ relative = 'src/PiSharp.Contracts/Streaming/NativeChatDiagnostic.cs'; bytes = 753; sha256 = 'f28ac557a79af342a2f97b3f5bd570406469678565c9a247a235fd79e90dfac6' },
        @{ relative = 'tests/PiSharp.MistralConversations.Tests/PiSharp.MistralConversations.Tests.csproj'; bytes = 202; sha256 = '5b39ed009ad7fad5a5972ae64105cad9242c73b3a0a050dac5b0f972f3594582' },
        @{ relative = 'tests/PiSharp.MistralConversations.Tests/Program.cs'; bytes = 33082; sha256 = '12039a755daa8e6293b00661f1ab4d7f61793ab95ada5c3a823987cca1af7d9a' },
        @{ relative = 'tests/PiSharp.MistralConversations.Tests/packages.lock.json'; bytes = 163; sha256 = '56f1771a69e65e2d5fe591f44a8b0344755bebc7ff94255c2c622ae969801f41' }
    )
}

function Assert-NativeMistralTextSourcePins {
    param([string]$Repo, [object]$Source)
    foreach ($pin in (Get-NativeMistralTextSourcePins)) {
        $file = Resolve-NativeCompanionPath $Repo $pin.relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $pin.bytes -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pin.sha256) {
            throw "MISTRAL-TEXT-SOURCE: Reviewed feature bytes changed: $($pin.relative)"
        }
        if ($PSBoundParameters.ContainsKey('Source')) {
            $matching = @($Source.files | Where-Object relative -CEQ $pin.relative)
            if ($matching.Count -ne 1 -or $matching[0].bytes -ne $pin.bytes -or $matching[0].sha256 -cne $pin.sha256) {
                throw "MISTRAL-TEXT-SOURCE: Required exact feature pin absent/changed/duplicate: $($pin.relative)"
            }
        }
    }
}

function Get-NativeAuthenticationSourcePins {
    # Exact reviewed auth 94596916, merged at 0274fbff; all nine source/provenance files remain frozen.
    return @(
        @{ relative = 'src/PiSharp.AI/Authentication/AuthenticationResolution.cs'; bytes = 2134; sha256 = '50ad2fb5cba43742ed9ce47b25c7c42ffe250444dea4629e8513459e716e919c' },
        @{ relative = 'src/PiSharp.AI/Authentication/HANDOFF.md'; bytes = 12108; sha256 = '005670036a4b03a9fc95b47b4de922c5218d28021eb2a050e86ce3abf4bd4720' },
        @{ relative = 'src/PiSharp.AI/Authentication/InjectedAuthenticationResolver.cs'; bytes = 9107; sha256 = '2d43d713f61f806afff0c011e97adaf606b288eed47588158dd39c32ae50e019' },
        @{ relative = 'src/PiSharp.AI/Authentication/ProviderEnvironmentSnapshot.cs'; bytes = 1566; sha256 = 'f84a7899d641844f4474248f361180b5080420874c18b3db61e5086c69784996' },
        @{ relative = 'src/PiSharp.AI/Authentication/integration-additions.proposed.json'; bytes = 7946; sha256 = '4b503b3e21a1fdaad3c6a9b4482983905f7d0da82fefee27d361f9f8f2f68c81' },
        @{ relative = 'src/PiSharp.AI/Authentication/source-inventory.json'; bytes = 1329; sha256 = '09cb5fcf91ad5f205748ef9875eae4ddaed4b3f70752f9d5701ae1d73fb5cf88' },
        @{ relative = 'tests/PiSharp.Authentication.Tests/PiSharp.Authentication.Tests.csproj'; bytes = 202; sha256 = '5b39ed009ad7fad5a5972ae64105cad9242c73b3a0a050dac5b0f972f3594582' },
        @{ relative = 'tests/PiSharp.Authentication.Tests/Program.cs'; bytes = 18142; sha256 = '683a46f88be3a71d813e14caef152c370b319c535b5fedf5ce4e1322609f3cce' },
        @{ relative = 'tests/PiSharp.Authentication.Tests/packages.lock.json'; bytes = 235; sha256 = '3e16ab11cc165b059ba0f89bcc544826628139b0300de0e22b2c6893b9f3e28b' }
    )
}

function Assert-NativeAuthenticationSourcePins {
    param([string]$Repo, [object]$Source)
    foreach ($pin in (Get-NativeAuthenticationSourcePins)) {
        $file = Resolve-NativeCompanionPath $Repo $pin.relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $pin.bytes -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pin.sha256) {
            throw "AUTHENTICATION-SOURCE: Reviewed feature bytes changed: $($pin.relative)"
        }
        if ($PSBoundParameters.ContainsKey('Source')) {
            $matching = @($Source.files | Where-Object relative -CEQ $pin.relative)
            if ($matching.Count -ne 1 -or $matching[0].bytes -ne $pin.bytes -or $matching[0].sha256 -cne $pin.sha256) {
                throw "AUTHENTICATION-SOURCE: Required exact feature pin absent/changed/duplicate: $($pin.relative)"
            }
        }
    }
}

function Assert-NativePreparedRootDeclaration {
    param([string[]]$RequiredRoots, [string[]]$DeclaredRoots)
    if ($RequiredRoots.Count -ne 54 -or $DeclaredRoots.Count -ne 54 -or
        @($RequiredRoots | Sort-Object -CaseSensitive -Unique).Count -ne 54 -or
        @($DeclaredRoots | Sort-Object -CaseSensitive -Unique).Count -ne 54 -or
        (($RequiredRoots | Sort-Object -CaseSensitive) -join '|') -cne (($DeclaredRoots | Sort-Object -CaseSensitive) -join '|')) {
        throw 'NATIVE-PREPARED-ROOTS: Exact unique 54-root receipt required, including Node metadata, both provider roots and every original root.'
    }
}

function Assert-NativeSelectorOwnershipSelection {
    param([string]$Repo, [object]$Plan)
    $originalDirectory = 'tests/PiSharp.Terminal.SelectList.Integration.Tests/'
    $ids = @(@('TerminalRpcAdmissionCancellationCases.cs', 'TerminalRpcReadCancellationCases.cs', 'TerminalOwnedChildShutdownCases.cs') | ForEach-Object {
        $text = Get-Content -LiteralPath (Join-Path $Repo ($originalDirectory + $_)) -Raw
        [regex]::Matches($text, '\("([a-z0-9-]+)",\s*(?:e\s*=>|[A-Z]\w*)') | ForEach-Object { $_.Groups[1].Value }
    })
    if ($Plan.schemaVersion -ne 1 -or $Plan.targetId -cne 'terminal-ownership-focused' -or $Plan.originalTargetId -cne 'terminal-select-dialog' -or
        $Plan.originalPlannedCases -ne 140 -or $Plan.selectedOriginalOwnershipCases -ne 31 -or $Plan.excludedOriginalCases -ne 109 -or
        $Plan.senderCases -ne 15 -or $Plan.readerCases -ne 14 -or $Plan.combinedOwnedChildCases -ne 2 -or
        $Plan.selectedCaseIds.Count -ne 31 -or @($Plan.selectedCaseIds | Sort-Object -Unique).Count -ne 31 -or
        ($Plan.selectedCaseIds -join '|') -cne ($ids -join '|') -or $Plan.excludedOriginalCaseIds.Count -ne 109 -or
        @($Plan.excludedOriginalCaseIds | Sort-Object -Unique).Count -ne 109 -or
        @($Plan.excludedOriginalCaseIds | Where-Object { $_ -cin $Plan.selectedCaseIds }).Count -or
        $Plan.fullSelectorSuitePassed -isnot [bool] -or $Plan.fullSelectorSuitePassed -or
        $Plan.fullNativeGatePassed -isnot [bool] -or $Plan.fullNativeGatePassed -or $Plan.nativeExecutions -ne 0 -or
        $Plan.runtimeAllocationMustIncludePhysicalOwnedChildren -ne 2 -or
        $Plan.ownedChildEntryAssembly -cne 'tests/PiSharp.CodingAgent.Tests/bin/Release/net10.0/PiSharp.CodingAgent.Tests.dll' -or
        $Plan.historicalFailure.reportSha256 -cne '171ab1f5318d468cc2fe46be98ce20fe07c2a17668599ebb677a13c44e570e9a' -or
        $Plan.historicalFailure.candidate -cne 'cfc32027591c461a1703c051e2e27fc3cb85afa7' -or
        $Plan.historicalFailure.reportBytes -ne 669736 -or $Plan.historicalFailure.completed -ne 60 -or
        $Plan.historicalFailure.incomplete -ne 1 -or $Plan.historicalFailure.unrun -ne 79 -or
        $Plan.upstream -cne 'd86654abb8862e201933517d6f1fce9f88dd117f' -or
        $Plan.historicalFailure.passed -isnot [bool] -or $Plan.historicalFailure.passed) {
        throw 'SELECTOR-OWNERSHIP: Exact31 original cases, excluded109 and preserved nonpassing history required.'
    }
}

function Assert-NativeSelectorOwnershipSource {
    param([string]$Repo, [object]$Source)
    $prefix = 'tests/PiSharp.Terminal.Ownership.Tests/'
    $inventory = Get-Content -LiteralPath (Join-Path $Repo ($prefix + 'linked-source-inventory.json')) -Raw | ConvertFrom-Json
    [xml]$project = Get-Content -LiteralPath (Join-Path $Repo ($prefix + 'PiSharp.Terminal.Ownership.Tests.csproj')) -Raw
    $links = @($project.Project.ItemGroup.Compile | ForEach-Object { 'tests/' + $_.Include.Replace('../', '') })
    if ($inventory.schemaVersion -ne 1 -or $inventory.files.Count -ne 16 -or $links.Count -ne 16 -or
        ($links -join '|') -cne ($inventory.files.relative -join '|') -or
        $project.Project.PropertyGroup.AssemblyName -cne 'PiSharp.CodingAgent.Tests' -or
        -not $project.Project.PropertyGroup.DefineConstants.Contains('SELECTOR_OWNERSHIP_FOCUSED')) { throw 'SELECTOR-OWNERSHIP: Exact linked-source project profile required.' }
    foreach ($pin in $inventory.files) {
        $file = Resolve-NativeCompanionPath $Repo $pin.relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $pin.bytes -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pin.sha256) { throw "SELECTOR-OWNERSHIP: Linked original source changed: $($pin.relative)" }
    }
    $plan = Get-Content -LiteralPath (Join-Path $Repo ($prefix + 'focused-case-plan.json')) -Raw | ConvertFrom-Json
    Assert-NativeSelectorOwnershipSelection -Repo $Repo -Plan $plan
    if ($PSBoundParameters.ContainsKey('Source')) {
        $required = @($links) + @('PiSharp.Terminal.Ownership.Tests.csproj', 'Program.cs', 'packages.lock.json', 'focused-case-plan.json', 'linked-source-inventory.json' | ForEach-Object { $prefix + $_ }) + @(
            'tests/PiSharp.Terminal.SelectList.Integration.Tests/Program.cs',
            'tests/PiSharp.Terminal.SelectList.Integration.Tests/fixtures/source-authority.json',
            'tests/PiSharp.CodingAgent.Tests/OwnedChildShutdownFixture.cs')
        foreach ($relative in $required) {
            $pins = @($Source.files | Where-Object relative -CEQ $relative); $file = Resolve-NativeCompanionPath $Repo $relative
            if ($pins.Count -ne 1 -or (Get-Item -LiteralPath $file).Length -ne $pins[0].bytes -or
                (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pins[0].sha256) { throw "SELECTOR-OWNERSHIP: Exact required source pin missing/changed: $relative" }
        }
    }
}

function Assert-NativeSelectorOwnershipScope {
    param([string]$Repo, [object]$Source, [object]$Receipt, [object]$Products)
    $scope = $Receipt.selectorOwnership; $selectionPath = 'tests/PiSharp.Terminal.Ownership.Tests/focused-case-plan.json'
    $pins = @($Source.files | Where-Object relative -CEQ $selectionPath)
    if ($null -eq $scope -or $null -eq $scope.selection -or $pins.Count -ne 1 -or
        $scope.selection.sourceRelative -cne $selectionPath -or $scope.selection.sourceSha256 -cne $pins[0].sha256 -or
        $scope.selection.selectedOriginalCases -ne 31 -or $scope.selection.originalSelectorCases -ne 140 -or
        $scope.selection.partialResultOnly -isnot [bool] -or -not $scope.selection.partialResultOnly) { throw 'SELECTOR-OWNERSHIP-SCOPE: Exact partial selection proof required.' }
    $root = 'tests/PiSharp.CodingAgent.Tests/bin/Release/net10.0'; $entry = $root + '/PiSharp.CodingAgent.Tests.dll'
    $sidecar = $scope.ownedChild
    $expected = @($Products.Values | Where-Object { $_.relative.StartsWith($root + '/', [StringComparison]::Ordinal) })
    if ($null -eq $sidecar -or $sidecar.outputRoot -cne $root -or $sidecar.entryAssembly -cne $entry -or
        -not $Products.ContainsKey($entry) -or $expected.Count -eq 0 -or $sidecar.products.Count -ne $expected.Count) {
        throw 'SELECTOR-OWNERSHIP-SCOPE: Complete original distinct CodingAgent sidecar required.'
    }
    foreach ($pin in $expected) {
        $matching = @($sidecar.products | Where-Object relative -CEQ $pin.relative)
        if ($matching.Count -ne 1 -or $matching[0].bytes -ne $pin.bytes -or $matching[0].sha256 -cne $pin.sha256) {
            throw 'SELECTOR-OWNERSHIP-SCOPE: Missing/changed/duplicate original sidecar product.'
        }
    }
}

function Get-NativePreparedArtifactRoots {
    param([object]$Registration)
    $coreProjects = @('Compatibility', 'Agent', 'Transport', 'Sessions', 'Tools', 'CodingAgent', 'Rpc', 'Extensions.ContractTests', 'ExtensionHost', 'Tui')
    $roots = @($coreProjects | ForEach-Object {
        if ($_ -eq 'Extensions.ContractTests') { 'tests/PiSharp.Extensions.ContractTests/bin/Release/net10.0' }
        else { 'tests/PiSharp.' + $_ + '.Tests/bin/Release/net10.0' }
    })
    $roots += @($Registration.targets | ForEach-Object { $_.entryAssembly.Substring(0, $_.entryAssembly.LastIndexOf('/')) })
    $roots += 'src/PiSharp.Cli/bin/Release/net10.0'
    $roots += @('one', 'two', 'fail', 'future', 'cli', 'cli-ui', 'system-import', 'image', 'todo', 'checkpoint', 'terminal-companion') | ForEach-Object { 'artifacts/extensions/published-fixtures/' + $_ }
    return @($roots | Sort-Object -Unique)
}

function Read-NativePinnedJson {
    param([string]$Path, [string]$Sha256)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        -not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'An existing artifact and its exact lowercase SHA256 are required.' }
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if ((Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Sha256) { throw "Pinned JSON artifact changed: $resolved" }
    return [pscustomobject]@{ path = $resolved; bytes = (Get-Item -LiteralPath $resolved).Length; sha256 = $Sha256;
        document = (Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json) }
}

function Get-NativeSdkSelectionEvidence {
    param([string]$Repo, [object]$Source, [object]$Receipt)
    $policyPin = @($Source.files | Where-Object relative -eq 'global.json')
    $policyFile = Resolve-NativeCompanionPath $Repo 'global.json'
    if ($policyPin.Count -ne 1 -or -not (Test-Path -LiteralPath $policyFile -PathType Leaf) -or
        (Get-Item -LiteralPath $policyFile).Length -ne $policyPin[0].bytes -or
        (Get-FileHash -LiteralPath $policyFile -Algorithm SHA256).Hash.ToLowerInvariant() -cne $policyPin[0].sha256) {
        throw 'NATIVE-SDK-POLICY-PIN: SDK policy differs from the exact source manifest.'
    }
    $policy = Get-Content -LiteralPath $policyFile -Raw | ConvertFrom-Json
    if ($policy.sdk.version -cnotmatch '^10\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or $policy.sdk.rollForward -cne 'disable') {
        throw 'NATIVE-SDK-VERSION-POLICY: Exact pinned .NET 10 SDK with rollForward disabled required.'
    }
    $selected = @($Receipt.actualPreparationSteps | Where-Object id -eq 'dotnet-sdk-version')
    if ($selected.Count -eq 0) { throw 'NATIVE-SDK-STEP-MISSING: Original dotnet-sdk-version preparation evidence required.' }
    if ($selected.Count -ne 1) { throw 'NATIVE-SDK-STEP-DUPLICATE: Exactly one original SDK selection step required.' }
    $step = $selected[0]
    if ($step.exit -ne 0 -or $step.originalChildReturned -isnot [bool] -or -not $step.originalChildReturned) {
        throw 'NATIVE-SDK-STEP-INCOMPLETE: SDK selection must succeed and its original child must return.'
    }
    if ($null -eq $step.log -or [string]::IsNullOrWhiteSpace($step.log.path) -or
        $step.log.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $null -eq $step.log.bytes -or $step.log.bytes -lt 0) {
        throw 'NATIVE-SDK-LOG-MISSING: Actual SDK selection log and exact identity required.'
    }
    $rootPath = [IO.Path]::GetFullPath($Repo).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $relative = [IO.Path]::GetRelativePath($rootPath, [IO.Path]::GetFullPath($step.log.path)).Replace('\', '/')
    $logFile = Resolve-NativeCompanionPath $Repo $relative
    if (-not (Test-Path -LiteralPath $logFile -PathType Leaf)) { throw 'NATIVE-SDK-LOG-MISSING: Original SDK selection log file is absent.' }
    if ((Get-Item -LiteralPath $logFile).Length -ne $step.log.bytes -or
        (Get-FileHash -LiteralPath $logFile -Algorithm SHA256).Hash.ToLowerInvariant() -cne $step.log.sha256) {
        throw 'NATIVE-SDK-LOG-PIN: Actual SDK selection log differs from its prepared receipt.'
    }
    $actualVersion = (Get-Content -LiteralPath $logFile -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($Receipt.sdkVersion) -or $Receipt.sdkVersion -cne $actualVersion) {
        throw 'NATIVE-SDK-VERSION-MISMATCH: Recorded SDK version must equal the trimmed actual hashed SDK selection log.'
    }
    if ($actualVersion -cnotmatch '^10\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or $actualVersion -cne $policy.sdk.version) {
        throw 'NATIVE-SDK-VERSION-POLICY: Actual selected SDK differs from the exact manifest-pinned SDK policy.'
    }
    return [pscustomobject]@{ stepId = $step.id; sdkVersion = $actualVersion; policy = 'exact-pinned-net10-sdk-rollForward-disable';
        policyFileSha256 = $policyPin[0].sha256; log = $step.log; originalChildReturned = $true; receiptIsExecutionAuthority = $false }
}

function Assert-NativeAnthropicSimplePreparedScope {
    param([string]$Repo, [object]$Source, [object]$Receipt, [object]$Registration, [object]$Products)
    $target = @($Registration.targets | Where-Object id -CEQ 'anthropic-simple')
    if ($target.Count -ne 1) { throw 'ANTHROPIC-SIMPLE-SCOPE: Exactly one admitted target required.' }
    $target = $target[0]
    $directory = $target.entryAssembly.Substring(0, $target.entryAssembly.LastIndexOf('/'))
    $scope = $Receipt.anthropicSimple
    $rootPath = [IO.Path]::GetFullPath($Repo).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $names = @('PiSharp.AnthropicSimple.Tests', 'PiSharp.AI', 'PiSharp.Agent', 'PiSharp.Contracts')
    if ($null -eq $scope -or $scope.schemaVersion -ne 1 -or $scope.project -cne $target.project -or
        $scope.entryAssembly -cne $target.entryAssembly -or $scope.candidate -cne $Source.candidate -or $scope.tree -cne $Source.tree -or
        $scope.outputRoot -cne $directory -or [string]::IsNullOrWhiteSpace($scope.reviewedRepositoryRoot) -or
        -not [IO.Path]::GetFullPath($scope.reviewedRepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar).Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        $scope.assemblies.Count -ne $names.Count) { throw 'ANTHROPIC-SIMPLE-SCOPE: Exact four-assembly candidate/root scope required.' }
    foreach ($name in $names) {
        $matching = @($scope.assemblies | Where-Object name -CEQ $name)
        $relative = $directory + '/' + $name + '.dll'; $file = Resolve-NativeCompanionPath $Repo $relative
        if ($matching.Count -ne 1 -or $matching[0].relative -cne $relative -or -not $Products.ContainsKey($relative) -or
            [string]::IsNullOrWhiteSpace($matching[0].path) -or
            -not [IO.Path]::GetFullPath($matching[0].path).Equals($file, [StringComparison]::OrdinalIgnoreCase) -or
            $matching[0].bytes -ne $Products[$relative].bytes -or $matching[0].sha256 -cne $Products[$relative].sha256 -or
            [string]::IsNullOrWhiteSpace($matching[0].informationalVersion) -or
            $matching[0].informationalVersion.IndexOf($Source.candidate, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "ANTHROPIC-SIMPLE-SCOPE: DLL differs from exact prepared output/candidate: $name"
        }
    }
    $projectPin = @($Source.files | Where-Object relative -CEQ $target.project)
    $lockPin = @($Source.files | Where-Object relative -CEQ $target.lockFile.path)
    $solutionPin = @($Source.files | Where-Object relative -CEQ 'PiSharp.slnx')
    $restore = $scope.lockedRestore; $build = $scope.solutionBuild
    if ($projectPin.Count -ne 1 -or $lockPin.Count -ne 1 -or $solutionPin.Count -ne 1 -or
        $null -eq $restore -or $restore.verified -isnot [bool] -or -not $restore.verified -or
        $restore.project -cne $target.project -or $restore.projectSha256 -cne $projectPin[0].sha256 -or
        $restore.lockFile.relative -cne $lockPin[0].relative -or $restore.lockFile.bytes -ne $lockPin[0].bytes -or
        $restore.lockFile.sha256 -cne $lockPin[0].sha256 -or $null -eq $build -or
        $build.verified -isnot [bool] -or -not $build.verified -or $build.solution -cne 'PiSharp.slnx' -or
        $build.solutionSha256 -cne $solutionPin[0].sha256) { throw 'ANTHROPIC-SIMPLE-SCOPE: Exact project/lock/solution proof required.' }
    foreach ($spec in @(@{ proof = $restore; id = 'locked-offline-restore' }, @{ proof = $build; id = 'full-solution-build' })) {
        $steps = @($Receipt.actualPreparationSteps | Where-Object id -CEQ $spec.id)
        $proof = $spec.proof
        if ($proof.stepId -cne $spec.id -or $steps.Count -ne 1 -or $steps[0].exit -ne 0 -or
            $steps[0].originalChildReturned -isnot [bool] -or -not $steps[0].originalChildReturned -or
            $null -eq $proof.log -or $proof.log.path -cne $steps[0].log.path -or
            $proof.log.bytes -ne $steps[0].log.bytes -or $proof.log.sha256 -cne $steps[0].log.sha256) {
            throw "ANTHROPIC-SIMPLE-SCOPE: Actual successful original preparation proof required: $($spec.id)"
        }
        $logRelative = [IO.Path]::GetRelativePath($rootPath, [IO.Path]::GetFullPath($proof.log.path)).Replace('\', '/')
        $log = Resolve-NativeCompanionPath $Repo $logRelative
        if (-not (Test-Path -LiteralPath $log -PathType Leaf) -or (Get-Item -LiteralPath $log).Length -ne $proof.log.bytes -or
            (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash.ToLowerInvariant() -cne $proof.log.sha256) {
            throw 'ANTHROPIC-SIMPLE-SCOPE: Actual preparation log changed.'
        }
    }
    if ($null -eq $scope.host -or $null -eq $Receipt.host -or [string]::IsNullOrWhiteSpace($scope.host.path) -or
        $scope.host.path -cne $Receipt.host.path -or $scope.host.bytes -ne $Receipt.host.bytes -or
        $scope.host.sha256 -cne $Receipt.host.sha256) { throw 'ANTHROPIC-SIMPLE-SCOPE: Exact prepared host pin required.' }
}

function Assert-NativeAzureResponsesPreparedScope {
    param([string]$Repo, [object]$Source, [object]$Receipt, [object]$Registration, [object]$Products)
    $target = @($Registration.targets | Where-Object id -CEQ 'azure-responses')
    if ($target.Count -ne 1) { throw 'AZURE-RESPONSES-SCOPE: Exactly one admitted target required.' }
    $target = $target[0]
    $directory = $target.entryAssembly.Substring(0, $target.entryAssembly.LastIndexOf('/'))
    $scope = $Receipt.azureResponses
    $rootPath = [IO.Path]::GetFullPath($Repo).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $names = @('PiSharp.AzureResponses.Tests', 'PiSharp.AI', 'PiSharp.Agent', 'PiSharp.Contracts')
    if ($null -eq $scope -or $scope.schemaVersion -ne 1 -or $scope.project -cne $target.project -or
        $scope.entryAssembly -cne $target.entryAssembly -or $scope.candidate -cne $Source.candidate -or $scope.tree -cne $Source.tree -or
        $scope.outputRoot -cne $directory -or [string]::IsNullOrWhiteSpace($scope.reviewedRepositoryRoot) -or
        -not [IO.Path]::GetFullPath($scope.reviewedRepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar).Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        $scope.assemblies.Count -ne $names.Count) { throw 'AZURE-RESPONSES-SCOPE: Exact 4-assembly candidate/root scope required.' }
    foreach ($name in $names) {
        $matching = @($scope.assemblies | Where-Object name -CEQ $name)
        $relative = $directory + '/' + $name + '.dll'; $file = Resolve-NativeCompanionPath $Repo $relative
        if ($matching.Count -ne 1 -or $matching[0].relative -cne $relative -or -not $Products.ContainsKey($relative) -or
            [string]::IsNullOrWhiteSpace($matching[0].path) -or
            -not [IO.Path]::GetFullPath($matching[0].path).Equals($file, [StringComparison]::OrdinalIgnoreCase) -or
            $matching[0].bytes -ne $Products[$relative].bytes -or $matching[0].sha256 -cne $Products[$relative].sha256 -or
            [string]::IsNullOrWhiteSpace($matching[0].informationalVersion) -or
            $matching[0].informationalVersion.IndexOf($Source.candidate, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "AZURE-RESPONSES-SCOPE: DLL differs from exact prepared output/candidate: $name"
        }
    }
    $projectPin = @($Source.files | Where-Object relative -CEQ $target.project)
    $lockPin = @($Source.files | Where-Object relative -CEQ $target.lockFile.path)
    $solutionPin = @($Source.files | Where-Object relative -CEQ 'PiSharp.slnx')
    $restore = $scope.lockedRestore; $build = $scope.solutionBuild
    if ($projectPin.Count -ne 1 -or $lockPin.Count -ne 1 -or $solutionPin.Count -ne 1 -or
        $null -eq $restore -or $restore.verified -isnot [bool] -or -not $restore.verified -or
        $restore.project -cne $target.project -or $restore.projectSha256 -cne $projectPin[0].sha256 -or
        $restore.lockFile.relative -cne $lockPin[0].relative -or $restore.lockFile.bytes -ne $lockPin[0].bytes -or
        $restore.lockFile.sha256 -cne $lockPin[0].sha256 -or $null -eq $build -or
        $build.verified -isnot [bool] -or -not $build.verified -or $build.solution -cne 'PiSharp.slnx' -or
        $build.solutionSha256 -cne $solutionPin[0].sha256) { throw 'AZURE-RESPONSES-SCOPE: Exact project/lock/solution proof required.' }
    foreach ($spec in @(@{ proof = $restore; id = 'locked-offline-restore' }, @{ proof = $build; id = 'full-solution-build' })) {
        $steps = @($Receipt.actualPreparationSteps | Where-Object id -CEQ $spec.id)
        $proof = $spec.proof
        if ($proof.stepId -cne $spec.id -or $steps.Count -ne 1 -or $steps[0].exit -ne 0 -or
            $steps[0].originalChildReturned -isnot [bool] -or -not $steps[0].originalChildReturned -or
            $null -eq $proof.log -or $proof.log.path -cne $steps[0].log.path -or
            $proof.log.bytes -ne $steps[0].log.bytes -or $proof.log.sha256 -cne $steps[0].log.sha256) {
            throw "AZURE-RESPONSES-SCOPE: Actual successful original preparation proof required: $($spec.id)"
        }
        $logRelative = [IO.Path]::GetRelativePath($rootPath, [IO.Path]::GetFullPath($proof.log.path)).Replace('\', '/')
        $log = Resolve-NativeCompanionPath $Repo $logRelative
        if (-not (Test-Path -LiteralPath $log -PathType Leaf) -or (Get-Item -LiteralPath $log).Length -ne $proof.log.bytes -or
            (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash.ToLowerInvariant() -cne $proof.log.sha256) {
            throw 'AZURE-RESPONSES-SCOPE: Actual preparation log changed.'
        }
    }
    if ($null -eq $scope.host -or $null -eq $Receipt.host -or [string]::IsNullOrWhiteSpace($scope.host.path) -or
        $scope.host.path -cne $Receipt.host.path -or $scope.host.bytes -ne $Receipt.host.bytes -or
        $scope.host.sha256 -cne $Receipt.host.sha256) { throw 'AZURE-RESPONSES-SCOPE: Exact prepared host pin required.' }
}

function Assert-NativeMistralTextPreparedScope {
    param([string]$Repo, [object]$Source, [object]$Receipt, [object]$Registration, [object]$Products)
    $target = @($Registration.targets | Where-Object id -CEQ 'mistral-text')
    if ($target.Count -ne 1) { throw 'MISTRAL-TEXT-SCOPE: Exactly one admitted target required.' }
    $target = $target[0]
    $directory = $target.entryAssembly.Substring(0, $target.entryAssembly.LastIndexOf('/'))
    $scope = $Receipt.mistralText
    $rootPath = [IO.Path]::GetFullPath($Repo).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $names = @('PiSharp.MistralConversations.Tests', 'PiSharp.AI', 'PiSharp.Contracts')
    if ($null -eq $scope -or $scope.schemaVersion -ne 1 -or $scope.project -cne $target.project -or
        $scope.entryAssembly -cne $target.entryAssembly -or $scope.candidate -cne $Source.candidate -or $scope.tree -cne $Source.tree -or
        $scope.outputRoot -cne $directory -or [string]::IsNullOrWhiteSpace($scope.reviewedRepositoryRoot) -or
        -not [IO.Path]::GetFullPath($scope.reviewedRepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar).Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        $scope.assemblies.Count -ne $names.Count) { throw 'MISTRAL-TEXT-SCOPE: Exact 3-assembly candidate/root scope required.' }
    foreach ($name in $names) {
        $matching = @($scope.assemblies | Where-Object name -CEQ $name)
        $relative = $directory + '/' + $name + '.dll'; $file = Resolve-NativeCompanionPath $Repo $relative
        if ($matching.Count -ne 1 -or $matching[0].relative -cne $relative -or -not $Products.ContainsKey($relative) -or
            [string]::IsNullOrWhiteSpace($matching[0].path) -or
            -not [IO.Path]::GetFullPath($matching[0].path).Equals($file, [StringComparison]::OrdinalIgnoreCase) -or
            $matching[0].bytes -ne $Products[$relative].bytes -or $matching[0].sha256 -cne $Products[$relative].sha256 -or
            [string]::IsNullOrWhiteSpace($matching[0].informationalVersion) -or
            $matching[0].informationalVersion.IndexOf($Source.candidate, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "MISTRAL-TEXT-SCOPE: DLL differs from exact prepared output/candidate: $name"
        }
    }
    $projectPin = @($Source.files | Where-Object relative -CEQ $target.project)
    $lockPin = @($Source.files | Where-Object relative -CEQ $target.lockFile.path)
    $solutionPin = @($Source.files | Where-Object relative -CEQ 'PiSharp.slnx')
    $restore = $scope.lockedRestore; $build = $scope.solutionBuild
    if ($projectPin.Count -ne 1 -or $lockPin.Count -ne 1 -or $solutionPin.Count -ne 1 -or
        $null -eq $restore -or $restore.verified -isnot [bool] -or -not $restore.verified -or
        $restore.project -cne $target.project -or $restore.projectSha256 -cne $projectPin[0].sha256 -or
        $restore.lockFile.relative -cne $lockPin[0].relative -or $restore.lockFile.bytes -ne $lockPin[0].bytes -or
        $restore.lockFile.sha256 -cne $lockPin[0].sha256 -or $null -eq $build -or
        $build.verified -isnot [bool] -or -not $build.verified -or $build.solution -cne 'PiSharp.slnx' -or
        $build.solutionSha256 -cne $solutionPin[0].sha256) { throw 'MISTRAL-TEXT-SCOPE: Exact project/lock/solution proof required.' }
    foreach ($spec in @(@{ proof = $restore; id = 'locked-offline-restore' }, @{ proof = $build; id = 'full-solution-build' })) {
        $steps = @($Receipt.actualPreparationSteps | Where-Object id -CEQ $spec.id)
        $proof = $spec.proof
        if ($proof.stepId -cne $spec.id -or $steps.Count -ne 1 -or $steps[0].exit -ne 0 -or
            $steps[0].originalChildReturned -isnot [bool] -or -not $steps[0].originalChildReturned -or
            $null -eq $proof.log -or $proof.log.path -cne $steps[0].log.path -or
            $proof.log.bytes -ne $steps[0].log.bytes -or $proof.log.sha256 -cne $steps[0].log.sha256) {
            throw "MISTRAL-TEXT-SCOPE: Actual successful original preparation proof required: $($spec.id)"
        }
        $logRelative = [IO.Path]::GetRelativePath($rootPath, [IO.Path]::GetFullPath($proof.log.path)).Replace('\', '/')
        $log = Resolve-NativeCompanionPath $Repo $logRelative
        if (-not (Test-Path -LiteralPath $log -PathType Leaf) -or (Get-Item -LiteralPath $log).Length -ne $proof.log.bytes -or
            (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash.ToLowerInvariant() -cne $proof.log.sha256) {
            throw 'MISTRAL-TEXT-SCOPE: Actual preparation log changed.'
        }
    }
    if ($null -eq $scope.host -or $null -eq $Receipt.host -or [string]::IsNullOrWhiteSpace($scope.host.path) -or
        $scope.host.path -cne $Receipt.host.path -or $scope.host.bytes -ne $Receipt.host.bytes -or
        $scope.host.sha256 -cne $Receipt.host.sha256) { throw 'MISTRAL-TEXT-SCOPE: Exact prepared host pin required.' }
}

function Assert-NativeAuthenticationPreparedScope {
    param([string]$Repo, [object]$Source, [object]$Receipt, [object]$Registration, [object]$Products)
    $target = @($Registration.targets | Where-Object id -CEQ 'authentication')
    if ($target.Count -ne 1) { throw 'AUTHENTICATION-SCOPE: Exactly one admitted target required.' }
    $target = $target[0]
    $directory = $target.entryAssembly.Substring(0, $target.entryAssembly.LastIndexOf('/'))
    $scope = $Receipt.authentication
    $rootPath = [IO.Path]::GetFullPath($Repo).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $names = @('PiSharp.Authentication.Tests', 'PiSharp.AI', 'PiSharp.Contracts')
    if ($null -eq $scope -or $scope.schemaVersion -ne 1 -or $scope.project -cne $target.project -or
        $scope.entryAssembly -cne $target.entryAssembly -or $scope.candidate -cne $Source.candidate -or $scope.tree -cne $Source.tree -or
        $scope.outputRoot -cne $directory -or [string]::IsNullOrWhiteSpace($scope.reviewedRepositoryRoot) -or
        -not [IO.Path]::GetFullPath($scope.reviewedRepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar).Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        $scope.assemblies.Count -ne $names.Count) { throw 'AUTHENTICATION-SCOPE: Exact three-assembly candidate/root scope required.' }
    foreach ($name in $names) {
        $matching = @($scope.assemblies | Where-Object name -CEQ $name)
        $relative = $directory + '/' + $name + '.dll'; $file = Resolve-NativeCompanionPath $Repo $relative
        if ($matching.Count -ne 1 -or $matching[0].relative -cne $relative -or -not $Products.ContainsKey($relative) -or
            [string]::IsNullOrWhiteSpace($matching[0].path) -or
            -not [IO.Path]::GetFullPath($matching[0].path).Equals($file, [StringComparison]::OrdinalIgnoreCase) -or
            $matching[0].bytes -ne $Products[$relative].bytes -or $matching[0].sha256 -cne $Products[$relative].sha256 -or
            [string]::IsNullOrWhiteSpace($matching[0].informationalVersion) -or
            $matching[0].informationalVersion.IndexOf($Source.candidate, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "AUTHENTICATION-SCOPE: DLL differs from exact prepared output/candidate: $name"
        }
    }
    $projectPin = @($Source.files | Where-Object relative -CEQ $target.project)
    $lockPin = @($Source.files | Where-Object relative -CEQ $target.lockFile.path)
    $solutionPin = @($Source.files | Where-Object relative -CEQ 'PiSharp.slnx')
    $restore = $scope.lockedRestore; $build = $scope.solutionBuild
    if ($projectPin.Count -ne 1 -or $lockPin.Count -ne 1 -or $solutionPin.Count -ne 1 -or
        $null -eq $restore -or $restore.verified -isnot [bool] -or -not $restore.verified -or
        $restore.project -cne $target.project -or $restore.projectSha256 -cne $projectPin[0].sha256 -or
        $restore.lockFile.relative -cne $lockPin[0].relative -or $restore.lockFile.bytes -ne $lockPin[0].bytes -or
        $restore.lockFile.sha256 -cne $lockPin[0].sha256 -or $null -eq $build -or
        $build.verified -isnot [bool] -or -not $build.verified -or $build.solution -cne 'PiSharp.slnx' -or
        $build.solutionSha256 -cne $solutionPin[0].sha256) { throw 'AUTHENTICATION-SCOPE: Exact project/lock/solution proof required.' }
    foreach ($spec in @(@{ proof = $restore; id = 'locked-offline-restore' }, @{ proof = $build; id = 'full-solution-build' })) {
        $steps = @($Receipt.actualPreparationSteps | Where-Object id -CEQ $spec.id)
        $proof = $spec.proof
        if ($proof.stepId -cne $spec.id -or $steps.Count -ne 1 -or $steps[0].exit -ne 0 -or
            $steps[0].originalChildReturned -isnot [bool] -or -not $steps[0].originalChildReturned -or
            $null -eq $proof.log -or $proof.log.path -cne $steps[0].log.path -or
            $proof.log.bytes -ne $steps[0].log.bytes -or $proof.log.sha256 -cne $steps[0].log.sha256) {
            throw "AUTHENTICATION-SCOPE: Actual successful original preparation proof required: $($spec.id)"
        }
        $logRelative = [IO.Path]::GetRelativePath($rootPath, [IO.Path]::GetFullPath($proof.log.path)).Replace('\', '/')
        $log = Resolve-NativeCompanionPath $Repo $logRelative
        if (-not (Test-Path -LiteralPath $log -PathType Leaf) -or (Get-Item -LiteralPath $log).Length -ne $proof.log.bytes -or
            (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash.ToLowerInvariant() -cne $proof.log.sha256) {
            throw 'AUTHENTICATION-SCOPE: Actual preparation log changed.'
        }
    }
    if ($null -eq $scope.host -or $null -eq $Receipt.host -or [string]::IsNullOrWhiteSpace($scope.host.path) -or
        $scope.host.path -cne $Receipt.host.path -or $scope.host.bytes -ne $Receipt.host.bytes -or
        $scope.host.sha256 -cne $Receipt.host.sha256) { throw 'AUTHENTICATION-SCOPE: Exact prepared host pin required.' }
}

function Get-NativePreparedValidation {
    param([string]$Repo, [string]$SourceManifest, [string]$SourceManifestSha256, [string]$BuildReceipt, [string]$BuildReceiptSha256)
    # This is evidence admission, not execution authority. A future lead-owned
    # policy-permitted build/signing window must already exist outside this script.
    $registration = Get-NativeCompanionRegistration -Repo $Repo -RequireLockFiles
    $manifest = Read-NativePinnedJson -Path $SourceManifest -Sha256 $SourceManifestSha256
    $build = Read-NativePinnedJson -Path $BuildReceipt -Sha256 $BuildReceiptSha256
    $rootPath = [IO.Path]::GetFullPath($Repo).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $source = $manifest.document; $receipt = $build.document
    if ($source.schemaVersion -ne 1 -or $source.candidate -cnotmatch '^[0-9a-f]{40}$' -or $source.tree -cnotmatch '^[0-9a-f]{40}$' -or
        -not $source.files.Count -or [string]::IsNullOrWhiteSpace($source.repository)) { throw 'Invalid immutable candidate source manifest.' }
    if ($receipt.schemaVersion -ne 1 -or $receipt.policyPermittedNativeBoundary -isnot [bool] -or -not $receipt.policyPermittedNativeBoundary -or
        $receipt.candidate -cne $source.candidate -or $receipt.tree -cne $source.tree -or
        $receipt.sourceManifestSha256 -cne $SourceManifestSha256 -or [string]::IsNullOrWhiteSpace($receipt.reviewedRepositoryRoot) -or
        -not [IO.Path]::GetFullPath($receipt.reviewedRepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar).Equals($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'External permitted build receipt does not associate this executing checkout and pinned source candidate.'
    }
    $closure = Assert-NativeSourceClosure -Repo $Repo -Source $source
    Assert-NativeAnthropicSimpleSourcePins -Repo $Repo -Source $source
    Assert-NativeAuthenticationSourcePins -Repo $Repo -Source $source
    Assert-NativeMistralTextSourcePins -Repo $Repo -Source $source
    Assert-NativeAzureResponsesSourcePins -Repo $Repo -Source $source
    Assert-NativeAzureResponsesSourcePins -Repo $Repo
    Assert-NativeMistralTextSourcePins -Repo $Repo
    Assert-NativeSelectorOwnershipSource -Repo $Repo -Source $source
    if ($null -eq $receipt.sourceAdmission -or $receipt.sourceAdmission.schemaVersion -ne 1 -or
        $receipt.sourceAdmission.policy -cne $closure.policy -or $receipt.sourceAdmission.freshCheckoutVerified -isnot [bool] -or
        -not $receipt.sourceAdmission.freshCheckoutVerified -or
        ($receipt.sourceAdmission.generatedRoots -join '|') -cne ($closure.generatedRoots -join '|') -or
        ($receipt.sourceAdmission.protectedArtifactSourceRoots -join '|') -cne ($closure.protectedArtifactSourceRoots -join '|') -or
        -not $receipt.sourceAdmission.generatedFiles.Count) {
        throw 'Fresh exact-source preparation and complete generated-input receipt required.'
    }
    Assert-NativeGeneratedInputPins -Closure $closure -Expected $receipt.sourceAdmission.generatedFiles
    $sourcePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($expected in $source.files) { [void]$sourcePaths.Add($expected.relative) }
    foreach ($relative in @('PiSharp.slnx', 'Directory.Build.props', 'Directory.Build.targets', 'global.json', 'NuGet.Config',
        'tools/test-native.ps1', 'tools/native-source-admission.ps1', 'tools/prepare-native-products.ps1',
        'tools/native-companion-registration.ps1', 'tools/native-companion-targets.json',
        'tests/PiSharp.Tui.KittyAlternate.Tests/Program.cs', 'tests/PiSharp.Tui.KittyAlternate.Tests/HeldIoEvidence.cs',
        'tests/PiSharp.Tui.KittyAlternate.Tests/HeldIoTests.cs', 'tests/PiSharp.Tui.KittyAlternate.Tests/fixtures/held-io-source-boundary.json',
        'tests/PiSharp.GoogleGenerativeAI.Tests/Program.cs', 'tests/PiSharp.GoogleGenerativeAI.Tests/PiSharp.GoogleGenerativeAI.Tests.csproj',
        'tests/PiSharp.GoogleGenerativeAI.Tests/packages.lock.json')) {
        if (-not $sourcePaths.Contains($relative)) { throw "Required source is absent from pinned manifest: $relative" }
    }
    $sdkSelection = Get-NativeSdkSelectionEvidence -Repo $Repo -Source $source -Receipt $receipt
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots $receipt.productRoots
    $products = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    if (-not $receipt.products.Count) { throw 'Full prepared validation requires externally pinned complete output files in products.' }
    foreach ($expected in $receipt.products) {
        $relative = [string]$expected.relative; $file = Resolve-NativeCompanionPath $Repo $relative
        if (-not @($roots | Where-Object { $relative.StartsWith($_ + '/', [StringComparison]::OrdinalIgnoreCase) }).Count -or
            -not $products.TryAdd($relative, $expected) -or $expected.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
            -not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $expected.bytes -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected.sha256) {
            throw "Prepared output changed or lies outside required product roots: $relative"
        }
    }
    foreach ($relativeRoot in $roots) {
        $directory = Resolve-NativeCompanionPath $Repo $relativeRoot
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Required prepared output directory missing: $relativeRoot" }
        $outputFiles = @(Get-ChildItem -LiteralPath $directory -Recurse -File -Force)
        if (-not $outputFiles.Count) { throw "Required prepared output directory empty: $relativeRoot" }
        foreach ($file in $outputFiles) {
            $relative = $file.FullName.Substring($rootPath.Length + 1).Replace('\', '/')
            if (-not $products.ContainsKey($relative)) { throw "Prepared product receipt omits actual output file: $relative" }
        }
    }
    $entries = @($registration.targets.entryAssembly)
    foreach ($id in @('Compatibility', 'Agent', 'Transport', 'Sessions', 'Tools', 'CodingAgent', 'Rpc', 'ExtensionHost', 'Tui')) {
        $entries += 'tests/PiSharp.' + $id + '.Tests/bin/Release/net10.0/PiSharp.' + $id + '.Tests.dll'
    }
    $entries += @('tests/PiSharp.Extensions.ContractTests/bin/Release/net10.0/PiSharp.Extensions.ContractTests.dll', 'src/PiSharp.Cli/bin/Release/net10.0/PiSharp.Cli.dll')
    foreach ($entry in $entries) { if (-not $products.ContainsKey($entry)) { throw "Prepared receipt omits required executable assembly: $entry" } }
    if ($receipt.dlls.Count -ne 3) { throw 'Held-I/O build receipt requires exactly three actual loaded assembly entries.' }
    $kitty = $registration.targets | Where-Object id -eq 'kitty-alternate'
    $kittyDirectory = $kitty.entryAssembly.Substring(0, $kitty.entryAssembly.LastIndexOf('/'))
    foreach ($name in @('PiSharp.Cli', 'PiSharp.Tui', 'PiSharp.CodingAgent.Tests')) {
        $matching = @($receipt.dlls | Where-Object name -eq $name)
        $relative = $kittyDirectory + '/' + $name + '.dll'; $file = Resolve-NativeCompanionPath $Repo $relative
        if ($matching.Count -ne 1 -or -not $products.ContainsKey($relative) -or
            [string]::IsNullOrWhiteSpace($matching[0].path) -or
            -not [IO.Path]::GetFullPath($matching[0].path).Equals($file, [StringComparison]::OrdinalIgnoreCase) -or
            $matching[0].bytes -ne $products[$relative].bytes -or $matching[0].sha256 -cne $products[$relative].sha256 -or
            [string]::IsNullOrWhiteSpace($matching[0].informationalVersion)) { throw "Held-I/O DLL entry differs from prepared consumer output: $name" }
    }
    # Keep the Kitty three-DLL view intact. The same complete receipt separately
    # binds the keybindings consumer's twelve products and exact friend DLL root.
    $keybindings = $registration.targets | Where-Object id -eq 'terminal-keybindings'
    $bindingDirectory = $keybindings.entryAssembly.Substring(0, $keybindings.entryAssembly.LastIndexOf('/'))
    $bindingNames = @('PiSharp.Agent', 'PiSharp.AI', 'PiSharp.Cli', 'PiSharp.CodingAgent', 'PiSharp.Contracts',
        'PiSharp.Extensions.Abstractions', 'PiSharp.Extensions.Agent', 'PiSharp.Extensions.Runtime', 'PiSharp.Rpc',
        'PiSharp.Sessions', 'PiSharp.Tools', 'PiSharp.Tui', 'PiSharp.CodingAgent.Tests')
    if ($receipt.executionPermitted -isnot [bool] -or -not $receipt.executionPermitted -or
        [string]::IsNullOrWhiteSpace($receipt.reviewRoot) -or
        -not [IO.Path]::GetFullPath($receipt.reviewRoot).TrimEnd([IO.Path]::DirectorySeparatorChar).Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        $receipt.assemblies.Count -ne $bindingNames.Count) { throw 'Exact keybindings candidate/root and thirteen-DLL receipt required.' }
    foreach ($name in $bindingNames) {
        $matching = @($receipt.assemblies | Where-Object name -eq $name)
        $relative = $bindingDirectory + '/' + $name + '.dll'
        if ($matching.Count -ne 1 -or $matching[0].relative -cne $relative -or -not $products.ContainsKey($relative) -or
            $matching[0].bytes -ne $products[$relative].bytes -or $matching[0].sha256 -cne $products[$relative].sha256 -or
            [string]::IsNullOrWhiteSpace($matching[0].informationalVersion) -or
            $matching[0].informationalVersion.IndexOf($source.candidate, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Keybindings DLL entry differs from prepared consumer output/candidate: $name"
        }
    }
    # Google has its own four-DLL view. Existing Kitty/keybindings arrays retain
    # their exact names, counts and isolated roots.
    $googleTarget = $registration.targets | Where-Object id -eq 'google-generative-ai'
    $googleDirectory = $googleTarget.entryAssembly.Substring(0, $googleTarget.entryAssembly.LastIndexOf('/'))
    $google = $receipt.google
    $googleNames = @('PiSharp.GoogleGenerativeAI.Tests', 'PiSharp.AI', 'PiSharp.Agent', 'PiSharp.Contracts')
    if ($null -eq $google -or $google.schemaVersion -ne 1 -or $google.candidate -cne $source.candidate -or
        $google.tree -cne $source.tree -or $google.outputRoot -cne $googleDirectory -or
        [string]::IsNullOrWhiteSpace($google.reviewedRepositoryRoot) -or
        -not [IO.Path]::GetFullPath($google.reviewedRepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar).Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        $google.assemblies.Count -ne $googleNames.Count) { throw 'Exact Google candidate/root and four-DLL scope required.' }
    foreach ($name in $googleNames) {
        $matching = @($google.assemblies | Where-Object name -eq $name)
        $relative = $googleDirectory + '/' + $name + '.dll'; $file = Resolve-NativeCompanionPath $Repo $relative
        if ($matching.Count -ne 1 -or $matching[0].relative -cne $relative -or -not $products.ContainsKey($relative) -or
            [string]::IsNullOrWhiteSpace($matching[0].path) -or
            -not [IO.Path]::GetFullPath($matching[0].path).Equals($file, [StringComparison]::OrdinalIgnoreCase) -or
            $matching[0].bytes -ne $products[$relative].bytes -or $matching[0].sha256 -cne $products[$relative].sha256 -or
            [string]::IsNullOrWhiteSpace($matching[0].informationalVersion) -or
            $matching[0].informationalVersion.IndexOf($source.candidate, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Google DLL entry differs from prepared consumer output/candidate: $name"
        }
    }
    $restore = $google.lockedRestore
    $restoreSteps = @($receipt.actualPreparationSteps | Where-Object id -eq 'locked-offline-restore')
    $projectPin = @($source.files | Where-Object relative -eq $googleTarget.project)
    $lockPin = @($source.files | Where-Object relative -eq $googleTarget.lockFile.path)
    if ($null -eq $restore -or $restore.stepId -cne 'locked-offline-restore' -or $restoreSteps.Count -ne 1 -or
        $restoreSteps[0].exit -ne 0 -or $restoreSteps[0].originalChildReturned -isnot [bool] -or -not $restoreSteps[0].originalChildReturned -or
        $restore.project -cne $googleTarget.project -or $projectPin.Count -ne 1 -or $restore.projectSha256 -cne $projectPin[0].sha256 -or
        $lockPin.Count -ne 1 -or $restore.lockFile.relative -cne $lockPin[0].relative -or
        $restore.lockFile.bytes -ne $lockPin[0].bytes -or $restore.lockFile.sha256 -cne $lockPin[0].sha256 -or
        $null -eq $restore.log -or $restore.log.path -cne $restoreSteps[0].log.path -or
        $restore.log.bytes -ne $restoreSteps[0].log.bytes -or $restore.log.sha256 -cne $restoreSteps[0].log.sha256) {
        throw 'Google requires actual successful original solution locked-restore evidence bound to its project and real lock.'
    }
    $restoreLogRelative = [IO.Path]::GetRelativePath($rootPath, [IO.Path]::GetFullPath($restore.log.path)).Replace('\', '/')
    $restoreLog = Resolve-NativeCompanionPath $Repo $restoreLogRelative
    if (-not (Test-Path -LiteralPath $restoreLog -PathType Leaf) -or (Get-Item -LiteralPath $restoreLog).Length -ne $restore.log.bytes -or
        (Get-FileHash -LiteralPath $restoreLog -Algorithm SHA256).Hash.ToLowerInvariant() -cne $restore.log.sha256) {
        throw 'Actual Google locked-restore log differs from the prepared receipt.'
    }
    # Selector consumers require the complete settled preparation history and
    # their own isolated scoped views, never an unscoped receipt subset.
    $preparedIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($step in $receipt.actualPreparationSteps) {
        if ([string]::IsNullOrWhiteSpace($step.id) -or -not $preparedIds.Add($step.id) -or $step.exit -ne 0 -or
            $step.originalChildReturned -isnot [bool] -or -not $step.originalChildReturned -or
            $null -eq $step.log -or $step.log.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Actual preparation step is failed, duplicate or unsettled.' }
        $logRelative = [IO.Path]::GetRelativePath($rootPath, [IO.Path]::GetFullPath($step.log.path)).Replace('\', '/')
        $logFile = Resolve-NativeCompanionPath $Repo $logRelative
        if (-not (Test-Path -LiteralPath $logFile -PathType Leaf) -or (Get-Item -LiteralPath $logFile).Length -ne $step.log.bytes -or
            (Get-FileHash -LiteralPath $logFile -Algorithm SHA256).Hash.ToLowerInvariant() -cne $step.log.sha256) { throw 'Actual preparation log differs from its receipt.' }
    }
    $requiredSteps = @('dotnet-sdk-version', 'dotnet-host-info', 'locked-offline-restore', 'full-solution-build')
    foreach ($fixture in @('one', 'two', 'fail', 'future', 'cli', 'cli-ui', 'system-import', 'image', 'todo', 'checkpoint', 'terminal-companion')) {
        $requiredSteps += @(('restore-fixture-' + $fixture), ('publish-fixture-' + $fixture))
    }
    foreach ($id in $requiredSteps) { if (-not $preparedIds.Contains($id)) { throw "Incomplete actual preparation history: $id" } }
    foreach ($spec in @(@{ field = 'selector'; id = 'terminal-select-list'; names = @('PiSharp.Tui', 'PiSharp.Tui.SelectList.Tests') },
        @{ field = 'selectorIntegration'; id = 'terminal-select-dialog'; names = $bindingNames },
        @{ field = 'selectorOwnership'; id = 'terminal-ownership-focused'; names = $bindingNames })) {
        $target = $registration.targets | Where-Object id -eq $spec.id
        $scope = $receipt.($spec.field); $directory = $target.entryAssembly.Substring(0, $target.entryAssembly.LastIndexOf('/'))
        if ($null -eq $scope -or $scope.schemaVersion -ne 1 -or $scope.project -cne $target.project -or
            $scope.entryAssembly -cne $target.entryAssembly -or $scope.candidate -cne $source.candidate -or $scope.tree -cne $source.tree -or
            $scope.outputRoot -cne $directory -or [string]::IsNullOrWhiteSpace($scope.reviewedRepositoryRoot) -or
            -not [IO.Path]::GetFullPath($scope.reviewedRepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar).Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
            $scope.assemblies.Count -ne $spec.names.Count) { throw "Exact selector scope required: $($spec.field)" }
        foreach ($name in $spec.names) {
            $matching = @($scope.assemblies | Where-Object name -eq $name)
            $relative = $directory + '/' + $name + '.dll'; $file = Resolve-NativeCompanionPath $Repo $relative
            if ($matching.Count -ne 1 -or $matching[0].relative -cne $relative -or -not $products.ContainsKey($relative) -or
                [string]::IsNullOrWhiteSpace($matching[0].path) -or
                -not [IO.Path]::GetFullPath($matching[0].path).Equals($file, [StringComparison]::OrdinalIgnoreCase) -or
                $matching[0].bytes -ne $products[$relative].bytes -or $matching[0].sha256 -cne $products[$relative].sha256 -or
                [string]::IsNullOrWhiteSpace($matching[0].informationalVersion) -or
                $matching[0].informationalVersion.IndexOf($source.candidate, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw "Selector DLL entry differs from isolated prepared output/candidate: $($spec.field)/$name"
            }
        }
        $restore = $scope.lockedRestore
        $projectPin = @($source.files | Where-Object relative -eq $target.project)
        $lockPin = @($source.files | Where-Object relative -eq $target.lockFile.path)
        if ($null -eq $restore -or $restore.verified -isnot [bool] -or -not $restore.verified -or
            $restore.stepId -cne 'locked-offline-restore' -or $projectPin.Count -ne 1 -or $restore.projectSha256 -cne $projectPin[0].sha256 -or
            $lockPin.Count -ne 1 -or $restore.lockFile.relative -cne $lockPin[0].relative -or
            $restore.lockFile.bytes -ne $lockPin[0].bytes -or $restore.lockFile.sha256 -cne $lockPin[0].sha256 -or
            $null -eq $restore.log -or $restore.log.path -cne $restoreSteps[0].log.path -or
            $restore.log.bytes -ne $restoreSteps[0].log.bytes -or $restore.log.sha256 -cne $restoreSteps[0].log.sha256) {
            throw "Selector scope requires actual locked restore bound to its pinned project/lock/log: $($spec.field)"
        }
    }
    Assert-NativeAnthropicSimplePreparedScope -Repo $Repo -Source $source -Receipt $receipt -Registration $registration -Products $products
    Assert-NativeAuthenticationPreparedScope -Repo $Repo -Source $source -Receipt $receipt -Registration $registration -Products $products
    Assert-NativeMistralTextPreparedScope -Repo $Repo -Source $source -Receipt $receipt -Registration $registration -Products $products
    Assert-NativeAzureResponsesPreparedScope -Repo $Repo -Source $source -Receipt $receipt -Registration $registration -Products $products
    Assert-NativeSelectorOwnershipScope -Repo $Repo -Source $source -Receipt $receipt -Products $products
    if ($null -eq $receipt.host -or [string]::IsNullOrWhiteSpace($receipt.host.path) -or
        $receipt.host.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $receipt.host.bytes -lt 1 -or
        -not (Test-Path -LiteralPath $receipt.host.path -PathType Leaf) -or
        (Get-Item -LiteralPath $receipt.host.path).Length -ne $receipt.host.bytes -or
        (Get-FileHash -LiteralPath $receipt.host.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $receipt.host.sha256) {
        throw 'Prepared keybindings runtime host pin differs from the actual host file.'
    }
    return [pscustomobject]@{ reviewedRoot = $rootPath; sourceManifest = $manifest.path; sourceManifestSha256 = $manifest.sha256;
        buildReceipt = $build.path; buildReceiptSha256 = $build.sha256; candidate = $source.candidate; tree = $source.tree;
        sourceFilesVerified = $closure.sourceFilesVerified; sourceMembershipPolicy = $closure.policy;
        generatedInputFilesVerified = $closure.generatedFiles.Count; productFilesVerified = $products.Count; productRoots = $roots;
        sdkSelection = $sdkSelection; receiptIsExecutionAuthorization = $false; actualLoadedAssemblyProofStillRequired = $true }
}

function Get-NativeLaunchHost {
    param([object]$PreparedValidation, [string]$SelectedExecutable)
    # Select the immutable receipt's host, independently of the ambient PATH.
    # Recheck its bytes at each launch boundary; validation grants no execution authority.
    $build = Read-NativePinnedJson -Path $PreparedValidation.buildReceipt -Sha256 $PreparedValidation.buildReceiptSha256
    $hostPin = $build.document.host
    if ($null -eq $hostPin -or [string]::IsNullOrWhiteSpace($hostPin.path) -or
        -not [IO.Path]::IsPathFullyQualified($hostPin.path) -or
        $hostPin.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $hostPin.bytes -lt 1) {
        throw 'NATIVE-LAUNCH-HOST-PIN: Absolute host path and exact positive byte/hash pin required.'
    }
    $hostPath = [IO.Path]::GetFullPath($hostPin.path)
    $observation = Get-NativePinObservation -Expected $hostPin -Path $hostPath
    if (-not $observation.pass) { throw 'NATIVE-LAUNCH-HOST-PIN: Recorded runtime host bytes differ from the actual file.' }
    if ($PSBoundParameters.ContainsKey('SelectedExecutable') -and
        ([string]::IsNullOrWhiteSpace($SelectedExecutable) -or
        -not [IO.Path]::IsPathFullyQualified($SelectedExecutable) -or
        -not [IO.Path]::GetFullPath($SelectedExecutable).Equals($hostPath, [StringComparison]::OrdinalIgnoreCase))) {
        throw 'NATIVE-LAUNCH-HOST-SELECTION: Selected executable differs from the recorded runtime host path.'
    }
    return $hostPath
}

function Get-NativePinObservation {
    param([object]$Expected, [string]$Path)
    $actual = $null; $errorText = $null; $pass = $false
    try {
        $resolved = [IO.Path]::GetFullPath($Path)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw 'Pinned file is missing.' }
        $actual = [pscustomobject]@{ path = $resolved; bytes = (Get-Item -LiteralPath $resolved).Length;
            sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant() }
        $pass = $null -ne $Expected -and $actual.sha256 -ceq $Expected.sha256 -and
            ($null -eq $Expected.bytes -or $actual.bytes -eq $Expected.bytes)
    } catch { $errorText = $_.Exception.ToString() }
    return [pscustomobject]@{ expected = $Expected; actual = $actual; pass = $pass; error = $errorText }
}

function Write-NativeLaunchEvidence {
    param([string]$Path, [object]$Evidence)
    $stream = $null; $writer = $null
    try {
        $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false), 4096, $true)
        $writer.WriteLine(($Evidence | ConvertTo-Json -Depth 30)); $writer.Flush(); $stream.Flush($true)
    } finally {
        if ($null -ne $writer) { $writer.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
    }
}

function Assert-NativeLaunchBoundary {
    param([string]$Repo, [object]$PreparedValidation, [string]$EntryAssembly, [string]$EvidenceFile,
        [Parameter(Mandatory)][string]$DotnetExecutable)
    $errors = [Collections.Generic.List[string]]::new()
    $observations = [Collections.Generic.List[object]]::new()
    $evidence = [pscustomobject]@{ schemaVersion = 1; checkedUtc = [DateTime]::UtcNow.ToString('o'); entryAssembly = $EntryAssembly;
        candidate = $PreparedValidation.candidate; tree = $PreparedValidation.tree; observations = $observations; errors = $errors;
        validationPassed = $false; launchStarted = $false; passingBehaviorReceipt = $false; receiptIsExecutionAuthority = $false }
    try {
        foreach ($inputPin in @(
            @{ path = $PreparedValidation.sourceManifest; sha256 = $PreparedValidation.sourceManifestSha256 },
            @{ path = $PreparedValidation.buildReceipt; sha256 = $PreparedValidation.buildReceiptSha256 })) {
            $observation = Get-NativePinObservation -Expected $inputPin -Path $inputPin.path
            $observations.Add($observation)
            if (-not $observation.pass) { $errors.Add('Immutable input artifact changed: ' + $inputPin.path) }
        }
        if ($errors.Count) { throw 'Immutable input artifacts rejected before reading their declarations.' }
        $manifest = Read-NativePinnedJson -Path $PreparedValidation.sourceManifest -Sha256 $PreparedValidation.sourceManifestSha256
        $build = Read-NativePinnedJson -Path $PreparedValidation.buildReceipt -Sha256 $PreparedValidation.buildReceiptSha256
        $evidence | Add-Member -NotePropertyName launchHost -NotePropertyValue (
            Get-NativeLaunchHost -PreparedValidation $PreparedValidation -SelectedExecutable $DotnetExecutable)
        foreach ($expected in @($manifest.document.files) + @($build.document.products)) {
            $file = Resolve-NativeCompanionPath $Repo $expected.relative
            $observation = Get-NativePinObservation -Expected $expected -Path $file
            $observations.Add($observation)
            if (-not $observation.pass) { $errors.Add('Declared source/product mismatch: ' + $expected.relative) }
        }
        $productPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($expected in $build.document.products) { [void]$productPaths.Add($expected.relative) }
        $registration = Get-NativeCompanionRegistration -Repo $Repo -RequireLockFiles
        $rootPath = [IO.Path]::GetFullPath($Repo).TrimEnd([IO.Path]::DirectorySeparatorChar)
        foreach ($relativeRoot in (Get-NativePreparedArtifactRoots -Registration $registration)) {
            $directory = Resolve-NativeCompanionPath $Repo $relativeRoot
            if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
                $errors.Add('Required product root missing: ' + $relativeRoot)
                $observations.Add([pscustomobject]@{ expected = @{ relativeRoot = $relativeRoot; exists = $true }; actual = @{ exists = $false }; pass = $false; error = $null })
                continue
            }
            foreach ($file in (Get-ChildItem -LiteralPath $directory -Recurse -File -Force)) {
                $relative = $file.FullName.Substring($rootPath.Length + 1).Replace('\', '/')
                if (-not $productPaths.Contains($relative)) {
                    $observations.Add((Get-NativePinObservation -Expected $null -Path $file.FullName))
                    $errors.Add('Additional unpinned output file: ' + $relative)
                }
            }
        }
        if (-not $productPaths.Contains($EntryAssembly)) { $errors.Add('Launch entry is not in the immutable product receipt: ' + $EntryAssembly) }
        if ($errors.Count) { throw 'Per-launch source/product pins rejected; no process started.' }
        # Also repeat schema/root/candidate association, complete root membership,
        # exact DLL identities and registration checks immediately before launch.
        Get-NativePreparedValidation -Repo $Repo -SourceManifest $PreparedValidation.sourceManifest -SourceManifestSha256 $PreparedValidation.sourceManifestSha256 -BuildReceipt $PreparedValidation.buildReceipt -BuildReceiptSha256 $PreparedValidation.buildReceiptSha256 | Out-Null
        $evidence.validationPassed = $true
    } catch { $errors.Add($_.Exception.ToString()) }
    finally { Write-NativeLaunchEvidence -Path $EvidenceFile -Evidence $evidence }
    if (-not $evidence.validationPassed) { throw "Per-launch identity rejected; expected/observed mismatch evidence: $EvidenceFile" }
    return [pscustomobject]@{ path = $EvidenceFile; bytes = (Get-Item -LiteralPath $EvidenceFile).Length;
        sha256 = (Get-FileHash -LiteralPath $EvidenceFile -Algorithm SHA256).Hash.ToLowerInvariant(); validationPassed = $true; behaviorPass = $false }
}

function Get-NativeCompanionSelection {
    param([object]$Registration, [AllowNull()][AllowEmptyCollection()][string[]]$TargetId)
    $explicitSelection = $PSBoundParameters.ContainsKey('TargetId')
    # The launcher supplies the fully admitted registry. Selection never narrows
    # registration/source/product admission or reorders its serial launch order.
    $known = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($target in $Registration.targets) {
        if ([string]::IsNullOrWhiteSpace($target.id) -or -not $known.Add($target.id)) {
            throw 'COMPANION-SELECTION: Ambiguous registered target ID.'
        }
    }
    $requested = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if ($explicitSelection) {
        if ($null -eq $TargetId -or $TargetId.Count -eq 0) { throw 'COMPANION-SELECTION: Explicit selection must contain at least one target ID.' }
        foreach ($id in $TargetId) {
            if ([string]::IsNullOrWhiteSpace($id) -or -not $known.Contains($id)) { throw "COMPANION-SELECTION: Unknown target ID: $id" }
            if (-not $requested.Add($id)) { throw "COMPANION-SELECTION: Duplicate target ID: $id" }
        }
    } else {
        foreach ($id in $known) { $requested.Add($id) | Out-Null }
    }
    $selected = @($Registration.targets | Where-Object { $requested.Contains($_.id) })
    if ($selected.Count -eq 0) { throw 'COMPANION-SELECTION: No admitted target selected.' }
    return [pscustomobject]@{ mode = $(if ($explicitSelection) { 'EXPLICIT_TARGET_IDS' } else { 'FULL_REGISTRY' });
        targets = $selected; selectedIds = @($selected | ForEach-Object id);
        unselectedIds = @($Registration.targets | Where-Object { -not $requested.Contains($_.id) } | ForEach-Object id) }
}

function New-NativeCompanionReceiptRows {
    param([object]$Registration, [object]$Selection)
    return @($Registration.targets | ForEach-Object {
        $selected = $_.id -cin $Selection.selectedIds
        [pscustomobject]@{ id = $_.id; project = $_.project; selected = $selected;
            partialSelectorOwnership = $_.id -ceq 'terminal-ownership-focused';
            status = $(if ($selected) { 'UNEXECUTED' } else { 'UNSELECTED_NEVER_RUN' }); exit = $null;
            pid = $null; processJoined = $false; outputJoined = $false; error = $null; report = $null; assemblies = @(); launchPins = $null;
            incompleteJoin = $false; passingReceipt = $false; cleanupOwner = 'NOT_STARTED'; cleanupDeadlineExpired = $false;
            stdoutSettlement = 'NOT_STARTED'; stderrSettlement = 'NOT_STARTED'; cleanupErrors = [Collections.Generic.List[string]]::new() }
    })
}

function Invoke-NativeCompanionTargets {
    param([string]$Repo, [string]$DotnetExecutable, [string]$EvidenceDirectory, [object]$PreparedValidation,
        [AllowNull()][AllowEmptyCollection()][string[]]$TargetId)
    $registration = Get-NativeCompanionRegistration -Repo $Repo -RequireLockFiles
    $selectionArguments = @{ Registration = $registration }
    if ($PSBoundParameters.ContainsKey('TargetId')) { $selectionArguments.TargetId = $TargetId }
    $selection = Get-NativeCompanionSelection @selectionArguments
    if ($null -eq $PreparedValidation) { throw 'Pinned prepared validation context required.' }
    # Rehash before this gate as well; core tests cannot silently mutate its inputs.
    $PreparedValidation = Get-NativePreparedValidation -Repo $Repo -SourceManifest $PreparedValidation.sourceManifest -SourceManifestSha256 $PreparedValidation.sourceManifestSha256 -BuildReceipt $PreparedValidation.buildReceipt -BuildReceiptSha256 $PreparedValidation.buildReceiptSha256
    if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Companion evidence directory must be fresh.' }
    New-Item -ItemType Directory -Path $EvidenceDirectory | Out-Null
    $rows = @(New-NativeCompanionReceiptRows -Registration $registration -Selection $selection)
    $receiptPath = Join-Path $EvidenceDirectory 'receipt.json'
    $journalPath = Join-Path $EvidenceDirectory 'receipt.jsonl'
    $persistenceErrors = [Collections.Generic.List[string]]::new()
    $registryHash = (Get-FileHash -LiteralPath (Join-Path $Repo 'tools/native-companion-targets.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $writeReceipt = {
        $stream = $null; $writer = $null
        try {
            $text = @{ schemaVersion = 1; writtenUtc = [DateTime]::UtcNow.ToString('o'); registrationSha256 = $registryHash;
                preparedValidation = $PreparedValidation; required = $registration.requiredCount; targets = $rows; persistenceErrors = $persistenceErrors;
                selectionMode = $selection.mode; selectedIds = $selection.selectedIds; unselectedIds = $selection.unselectedIds;
                selectedComplete = $persistenceErrors.Count -eq 0 -and @($rows | Where-Object { $_.selected -and -not $_.passingReceipt }).Count -eq 0;
                complete = $persistenceErrors.Count -eq 0 -and @($rows | Where-Object { -not $_.passingReceipt }).Count -eq 0;
                packageAcceptance = $false; phaseAcceptance = $false; behaviorReviewStillRequired = $true } | ConvertTo-Json -Depth 25 -Compress
            # Append and flush the durable journal before updating the convenience snapshot.
            $stream = [IO.FileStream]::new($journalPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
            $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false), 4096, $true)
            $writer.WriteLine($text); $writer.Flush(); $stream.Flush($true)
            $text | Set-Content -LiteralPath $receiptPath -Encoding utf8
        } catch {
            $persistenceErrors.Add($_.Exception.ToString())
            foreach ($activeRow in $rows | Where-Object { $_.status -eq 'STARTED' -or $_.status -eq 'PASSED' }) {
                $activeRow.status = 'INCOMPLETE'; $activeRow.passingReceipt = $false
            }
            try { [Console]::Error.WriteLine('Owned receipt persistence failed: ' + $_.Exception.ToString()) } catch { $persistenceErrors.Add($_.Exception.ToString()) }
        } finally {
            try { if ($null -ne $writer) { $writer.Dispose() } } catch { $persistenceErrors.Add($_.Exception.ToString()) }
            try { if ($null -ne $stream) { $stream.Dispose() } } catch { $persistenceErrors.Add($_.Exception.ToString()) }
        }
    }
    & $writeReceipt
    if ($persistenceErrors.Count) { throw 'Initial owned receipt persistence failed; no companion started.' }
    $stopped = $false
    foreach ($target in $selection.targets) {
        $row = $rows | Where-Object id -eq $target.id
        $dllPath = Resolve-NativeCompanionPath $Repo $target.entryAssembly
        $reportPath = Join-Path $EvidenceDirectory $target.reportName
        $process = [Diagnostics.Process]::new()
        $processStarted = $false
        $stdoutFile = $null; $stderrFile = $null; $stdoutCopy = $null; $stderrCopy = $null
        try {
            if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) { throw "Missing registered built companion: $dllPath" }
            foreach ($sidecar in $target.outputFixturePins) {
                $sidecarPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $dllPath) $sidecar.relative))
                if (-not (Test-Path -LiteralPath $sidecarPath -PathType Leaf) -or
                    (Get-Item -LiteralPath $sidecarPath).Length -ne $sidecar.bytes -or
                    (Get-FileHash -LiteralPath $sidecarPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sidecar.sha256) {
                    throw "Built companion sidecar differs from the pinned Source fixture: $($sidecar.relative)"
                }
            }
            $row.assemblies = @(Get-ChildItem -LiteralPath (Split-Path -Parent $dllPath) -File -Filter '*.dll' | ForEach-Object {
                @{ path = $_.FullName; bytes = $_.Length;
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant();
                    productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName).ProductVersion }
            })
            $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($DotnetExecutable)
            $process.StartInfo.WorkingDirectory = $Repo
            $process.StartInfo.UseShellExecute = $false
            $process.StartInfo.CreateNoWindow = $true
            $process.StartInfo.RedirectStandardOutput = $true
            $process.StartInfo.RedirectStandardError = $true
            $process.StartInfo.ArgumentList.Add($dllPath)
            foreach ($argument in $target.arguments) {
                switch ($argument.kind) {
                    'literal' { $value = [string]$argument.value }
                    'fixture' { $value = Resolve-NativeCompanionPath $Repo $argument.path }
                    'report' { $value = $reportPath }
                    'reviewedRoot' { $value = $PreparedValidation.reviewedRoot }
                    'sourceManifest' { $value = $PreparedValidation.sourceManifest }
                    'sourceManifestSha256' { $value = $PreparedValidation.sourceManifestSha256 }
                    'buildReceipt' { $value = $PreparedValidation.buildReceipt }
                    'buildReceiptSha256' { $value = $PreparedValidation.buildReceiptSha256 }
                }
                $process.StartInfo.ArgumentList.Add($value)
            }
            $stdoutFile = [IO.File]::Open((Join-Path $EvidenceDirectory ($target.id + '.stdout.log')), [IO.FileMode]::CreateNew)
            $stderrFile = [IO.File]::Open((Join-Path $EvidenceDirectory ($target.id + '.stderr.log')), [IO.FileMode]::CreateNew)
            $row.launchPins = Assert-NativeLaunchBoundary -Repo $Repo -PreparedValidation $PreparedValidation -DotnetExecutable $DotnetExecutable -EntryAssembly $target.entryAssembly -EvidenceFile (Join-Path $EvidenceDirectory ($target.id + '.launch-pins.json'))
            if (-not $process.Start()) { throw 'Owned companion process did not start.' }
            $processStarted = $true
            $row.pid = $process.Id; $row.status = 'STARTED'; $row.incompleteJoin = $true; $row.cleanupOwner = 'ACTIVE'
            $stdoutCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
            $stderrCopy = $process.StandardError.BaseStream.CopyToAsync($stderrFile)
            & $writeReceipt
            if ($persistenceErrors.Count) { throw 'Owned receipt persistence failed after launch.' }
            $deadline = [DateTime]::UtcNow.AddSeconds($target.timeoutSeconds)
            while (-not $process.WaitForExit(1000)) {
                if ([DateTime]::UtcNow -ge $deadline) { throw "Owned companion deadline exceeded: $($target.id)" }
            }
            $row.processJoined = $true; $row.exit = $process.ExitCode
            if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdoutCopy, $stderrCopy), 10000)) {
                throw "Owned companion output streams did not join: $($target.id)"
            }
            $row.outputJoined = $true
            $row.stdoutSettlement = 'SUCCEEDED'; $row.stderrSettlement = 'SUCCEEDED'; $row.incompleteJoin = $false
            $stdoutFile.Dispose(); $stdoutFile = $null; $stderrFile.Dispose(); $stderrFile = $null
            $policyText = (Get-Content -LiteralPath (Join-Path $EvidenceDirectory ($target.id + '.stderr.log')) -Raw) +
                (Get-Content -LiteralPath (Join-Path $EvidenceDirectory ($target.id + '.stdout.log')) -Raw)
            if (Test-Path -LiteralPath $reportPath -PathType Leaf) {
                $row.report = @{ path = $reportPath; bytes = (Get-Item -LiteralPath $reportPath).Length;
                    sha256 = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant() }
                $policyText += Get-Content -LiteralPath $reportPath -Raw
            }
            if ($policyText -match '(?i)0x800711c7|application control.*block') {
                $row.status = 'APP_CONTROL_HOLD'; $stopped = $true
            } elseif ($row.exit -ne 0 -or $null -eq $row.report) { $row.status = 'FAILED' }
            else { $row.status = 'PASSED' }
        } catch {
            $row.status = 'INCOMPLETE'; $row.error = $_.Exception.ToString(); $stopped = $true
            & $writeReceipt
        } finally {
            $row.cleanupOwner = 'RETAINED_UNTIL_ORIGINAL_SETTLEMENT'
            $row.incompleteJoin = $processStarted -and (-not $row.processJoined -or
                ($null -ne $stdoutCopy -and -not $stdoutCopy.IsCompleted) -or ($null -ne $stderrCopy -and -not $stderrCopy.IsCompleted))
            & $writeReceipt # Incomplete/nonpassing evidence precedes all cleanup waits.
            if ($processStarted -and -not $row.processJoined) {
                try {
                    if (-not $process.HasExited) { $process.Kill($true) }
                    $row.processJoined = $process.WaitForExit(10000)
                    if ($row.processJoined) { $row.exit = $process.ExitCode }
                } catch { $row.cleanupErrors.Add('Owned process cleanup: ' + $_.Exception.ToString()) }
                if (-not $row.processJoined) {
                    $row.cleanupDeadlineExpired = $true; $row.status = 'INCOMPLETE'; $stopped = $true
                    & $writeReceipt
                    # The deadline is diagnostic only. This scope still owns the exact
                    # Process and cannot return, dispose it or launch another target.
                    while (-not $row.processJoined) {
                        try {
                            $row.processJoined = $process.WaitForExit(1000)
                            if ($row.processJoined) { $row.exit = $process.ExitCode }
                        } catch {
                            $row.cleanupErrors.Add('Retained original process wait: ' + $_.Exception.ToString()); & $writeReceipt
                            [Threading.Thread]::Sleep(1000)
                        }
                    }
                }
            }
            foreach ($copy in @(@{ name = 'stdout'; task = $stdoutCopy }, @{ name = 'stderr'; task = $stderrCopy })) {
                $copyTask = $copy.task
                if ($null -eq $copyTask) { continue }
                if (-not $copyTask.IsCompleted) {
                    try { [void]$copyTask.Wait(5000) } catch { $row.cleanupErrors.Add($copy.name + ' bounded cleanup: ' + $_.Exception.ToString()) }
                    if (-not $copyTask.IsCompleted) {
                        $row.cleanupDeadlineExpired = $true; $row.status = 'INCOMPLETE'; $row.incompleteJoin = $true; $stopped = $true
                        & $writeReceipt
                        while (-not $copyTask.IsCompleted) {
                            try { [void]$copyTask.Wait(1000) }
                            catch { if (-not $copyTask.IsCompleted) { $row.cleanupErrors.Add($copy.name + ' retained cleanup: ' + $_.Exception.ToString()); & $writeReceipt } }
                        }
                    }
                }
                # Observe the exact original task after settlement, including its fault
                # or cancellation. A timeout Boolean is never a substituted join.
                $stateField = $copy.name + 'Settlement'
                try { $copyTask.GetAwaiter().GetResult(); $row.$stateField = 'SUCCEEDED' }
                catch {
                    $row.$stateField = if ($copyTask.IsCanceled) { 'CANCELED' } else { 'FAULTED' }
                    $row.cleanupErrors.Add($copy.name + ' original task settlement: ' + $_.Exception.ToString()); $row.status = 'INCOMPLETE'; $stopped = $true
                }
                & $writeReceipt
            }
            $row.outputJoined = $null -ne $stdoutCopy -and $null -ne $stderrCopy -and $stdoutCopy.IsCompleted -and $stderrCopy.IsCompleted
            $row.incompleteJoin = $processStarted -and (-not $row.processJoined -or
                ($null -ne $stdoutCopy -and -not $stdoutCopy.IsCompleted) -or ($null -ne $stderrCopy -and -not $stderrCopy.IsCompleted))
            # Disposal is reachable only after the original process and every created
            # copy task have actually settled. Keep references alive if proof is absent.
            # Both preceding retained loops must settle before this scope reaches disposal.
            try { if ($null -ne $stdoutFile) { $stdoutFile.Dispose() } } catch { $row.cleanupErrors.Add('stdout dispose: ' + $_.Exception.ToString()) }
            try { if ($null -ne $stderrFile) { $stderrFile.Dispose() } } catch { $row.cleanupErrors.Add('stderr dispose: ' + $_.Exception.ToString()) }
            try { $process.Dispose() } catch { $row.cleanupErrors.Add('process dispose: ' + $_.Exception.ToString()) }
            $row.cleanupOwner = 'ORIGINAL_OPERATIONS_SETTLED'
            if ($row.cleanupErrors.Count -or $persistenceErrors.Count) { $row.status = 'INCOMPLETE'; $stopped = $true }
            $row.passingReceipt = $row.status -eq 'PASSED' -and $row.processJoined -and $row.outputJoined -and
                $row.stdoutSettlement -eq 'SUCCEEDED' -and $row.stderrSettlement -eq 'SUCCEEDED' -and
                $row.cleanupErrors.Count -eq 0 -and $persistenceErrors.Count -eq 0
            & $writeReceipt
            if ($persistenceErrors.Count) {
                $row.status = 'INCOMPLETE'; $row.passingReceipt = $false; $stopped = $true
                & $writeReceipt
            }
        }
        if ($stopped) { break }
    }
    if ($persistenceErrors.Count -or @($rows | Where-Object { $_.selected -and -not $_.passingReceipt }).Count -ne 0) {
        $scope = if ($selection.mode -eq 'FULL_REGISTRY') { 'Full companion gate' } else { 'Selected companion batch' }
        throw "$scope failed or incomplete; all observed failures and unexecuted targets remain in $receiptPath"
    }
}
