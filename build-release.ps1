[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '1.0.34',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [string]$WebView2InstallerPath,
    [string]$SigningCertificateThumbprint,
    [string]$TimestampServerUrl
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$publishRoot = Join-Path $artifactsRoot 'publish'
$desktopPublish = Join-Path $publishRoot 'desktop'
$servicePublish = Join-Path $publishRoot 'service'
$portablePublish = Join-Path $publishRoot 'package'
$installerOutput = Join-Path $artifactsRoot 'installer'
$releaseOutput = Join-Path $projectRoot 'release'
$uiSource = Join-Path $projectRoot 'src\DynaK.Service\wwwroot'
$appIcon = Join-Path $projectRoot 'src\DynaK.WetLeakTest.Desktop\Assets\dynak-parc-app-icon.ico'
$signingEnabled = -not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)
$buildDateUtc = (Get-Date).ToUniversalTime()
$versionParts = @($Version.Split('.'))
while ($versionParts.Count -lt 4) {
    $versionParts += '0'
}
$fileVersion = $versionParts[0..3] -join '.'

function Get-SourceRevision {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot '.git')) -or
        -not (Get-Command git.exe -ErrorAction SilentlyContinue)) {
        return 'not available'
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    try {
        $revision = & git.exe -C $projectRoot rev-parse --short HEAD 2>$null
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($revision)) {
        return 'not available'
    }

    return $revision.Trim()
}

function Get-UiSourceFiles {
    return @(Get-ChildItem -LiteralPath $uiSource -File -Recurse | Sort-Object FullName)
}

function Get-UiSourceHash {
    $manifest = (Get-UiSourceFiles | ForEach-Object {
        $relativePath = $_.FullName.Substring($uiSource.Length).TrimStart('\')
        "$relativePath=$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    }) -join "`n"
    $bytes = [Text.Encoding]::UTF8.GetBytes($manifest)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha256.ComputeHash($bytes)).Replace('-', '')
    } finally {
        $sha256.Dispose()
    }
}

function Assert-PublishedUi([string]$PublishedServiceRoot) {
    $publishedUi = Join-Path $PublishedServiceRoot 'wwwroot'
    foreach ($sourceFile in Get-UiSourceFiles) {
        $relativePath = $sourceFile.FullName.Substring($uiSource.Length).TrimStart('\')
        $publishedFile = Join-Path $publishedUi $relativePath
        if (-not (Test-Path -LiteralPath $publishedFile)) {
            throw "STOP THE RELEASE: renderer file is missing from publish output: $relativePath"
        }

        $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
        $publishedHash = (Get-FileHash -LiteralPath $publishedFile -Algorithm SHA256).Hash
        if ($sourceHash -ne $publishedHash) {
            throw "STOP THE RELEASE: renderer file is stale or differs from source: $relativePath"
        }
    }

    foreach ($requiredAsset in @('index.html', 'app.js', 'styles.css', 'images\dynak-header.png')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishedUi $requiredAsset))) {
            throw "STOP THE RELEASE: required renderer asset is missing: $requiredAsset"
        }
    }

    if (Test-Path -LiteralPath (Join-Path $PublishedServiceRoot 'code.html')) {
        throw 'STOP THE RELEASE: legacy code.html was included in the production service package.'
    }
}

function Assert-NoMarkOfWeb([string]$Path) {
    foreach ($file in Get-ChildItem -LiteralPath $Path -File -Recurse) {
        if (Get-Item -LiteralPath $file.FullName -Stream 'Zone.Identifier' -ErrorAction SilentlyContinue) {
            throw "STOP THE RELEASE: Mark-of-the-Web is present on $($file.FullName)."
        }
    }
}

function Get-PublishedBinaries([string]$Path) {
    return @(Get-ChildItem -LiteralPath $Path -File -Recurse | Where-Object { $_.Extension -in '.exe', '.dll' })
}

function Invoke-CodeSignPublishedBinaries([string]$Path) {
    foreach ($binary in Get-PublishedBinaries $Path) {
        if ((Get-AuthenticodeSignature -LiteralPath $binary.FullName).Status -ne 'Valid') {
            Invoke-CodeSign $binary.FullName
        }
    }
}

function Assert-ServicePublishTrust([string]$Path) {
    $forbiddenSqliteFiles = @(Get-ChildItem -LiteralPath $Path -File -Recurse | Where-Object {
        $_.Name -eq 'e_sqlite3.dll' -or $_.Name -like 'SQLitePCLRaw*.dll'
    })
    if ($forbiddenSqliteFiles.Count -gt 0) {
        $names = ($forbiddenSqliteFiles.Name | Sort-Object -Unique) -join ', '
        throw "STOP THE RELEASE: loose SQLite runtime binaries remain in the service publish: $names"
    }

    $untrusted = @(Get-PublishedBinaries $Path | Where-Object {
        (Get-AuthenticodeSignature -LiteralPath $_.FullName).Status -ne 'Valid'
    })
    if ($signingEnabled -and $untrusted.Count -gt 0) {
        throw "STOP THE RELEASE: one or more service binaries do not have a valid Authenticode signature: $($untrusted.Name -join ', ')"
    }

    $unexpectedLoose = @($untrusted | Where-Object { $_.Name -ne 'DynaK.Service.exe' })
    if (-not $signingEnabled -and $unexpectedLoose.Count -gt 0) {
        throw "STOP THE RELEASE: unsigned loose service dependencies remain: $($unexpectedLoose.Name -join ', ')"
    }
}

$sourceRevision = Get-SourceRevision
$uiSourceHash = Get-UiSourceHash
$uiBuildId = '{0}-{1}' -f $buildDateUtc.ToString('yyyyMMdd-HHmmss'), $uiSourceHash.Substring(0, 12).ToLowerInvariant()

if ($signingEnabled -ne (-not [string]::IsNullOrWhiteSpace($TimestampServerUrl))) {
    throw 'SigningCertificateThumbprint and TimestampServerUrl must be supplied together.'
}

function Find-SignTool {
    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidate = Get-ChildItem -LiteralPath $sdkRoot -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $candidate) {
        throw 'Windows SDK signtool.exe was not found.'
    }

    return $candidate.FullName
}

function Find-MakeNsis {
    $command = Get-Command 'makensis.exe' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path $env:ProgramFiles 'NSIS\makensis.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe')
    )
    $candidate = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $candidate) {
        throw 'NSIS makensis.exe was not found. Install NSIS 3.x before building a release.'
    }

    return $candidate
}

function Wait-ForFileCompletion([string]$Path) {
    $deadline = (Get-Date).AddMinutes(5)
    [long]$previousLength = -1
    $stableChecks = 0
    while ($true) {
        if (Test-Path -LiteralPath $Path) {
            $length = (Get-Item -LiteralPath $Path).Length
            if ($length -gt 0 -and $length -eq $previousLength) {
                $stableChecks++
                if ($stableChecks -ge 10) {
                    return
                }
            } else {
                $stableChecks = 0
                $previousLength = $length
            }
        }

        if ((Get-Date) -ge $deadline) {
            throw "Timed out waiting for NSIS to finish writing $Path."
        }

        Start-Sleep -Milliseconds 500
    }
}

function Invoke-CodeSign([string]$Path) {
    & $script:signToolPath sign /sha1 $SigningCertificateThumbprint /fd SHA256 /tr $TimestampServerUrl /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) {
        throw "Code signing failed for $Path."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid') {
        throw "The signature on $Path is not valid: $($signature.Status)."
    }
}

if ($signingEnabled) {
    $script:signToolPath = Find-SignTool
}
$makeNsisPath = Find-MakeNsis

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

if (Test-Path -LiteralPath $installerOutput) {
    Remove-Item -LiteralPath $installerOutput -Recurse -Force
}

if (Test-Path -LiteralPath $releaseOutput) {
    Remove-Item -LiteralPath $releaseOutput -Recurse -Force
}

New-Item -ItemType Directory -Path $desktopPublish, $servicePublish, $portablePublish, $installerOutput -Force | Out-Null
New-Item -ItemType Directory -Path $releaseOutput -Force | Out-Null

dotnet publish (Join-Path $projectRoot 'src\DynaK.WetLeakTest.Desktop\DynaK.WetLeakTest.Desktop.csproj') `
    --disable-build-servers `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $desktopPublish `
    -p:PublishSingleFile=false `
    -p:BuildInParallel=false `
    -p:UseSharedCompilation=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$fileVersion `
    -p:FileVersion=$fileVersion `
    -p:InformationalVersion="$Version+$sourceRevision" `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }

dotnet publish (Join-Path $projectRoot 'src\DynaK.Service\DynaK.Service.csproj') `
    --disable-build-servers `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $servicePublish `
    -p:PublishSingleFile=true `
    -p:BuildInParallel=false `
    -p:UseSharedCompilation=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$fileVersion `
    -p:FileVersion=$fileVersion `
    -p:InformationalVersion="$Version+$sourceRevision" `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }

Assert-PublishedUi $servicePublish
Assert-NoMarkOfWeb $desktopPublish
Assert-NoMarkOfWeb $servicePublish
$buildInfo = [ordered]@{
    appVersion = $Version
    uiBuildId = $uiBuildId
    buildDate = $buildDateUtc.ToString('o')
    sourceRevision = $sourceRevision
}
$buildInfoPath = Join-Path $servicePublish 'build-info.json'
$buildInfo | ConvertTo-Json | Set-Content -LiteralPath $buildInfoPath -Encoding UTF8
$validatedBuildInfo = Get-Content -LiteralPath $buildInfoPath -Raw | ConvertFrom-Json
if ($validatedBuildInfo.appVersion -ne $Version -or $validatedBuildInfo.uiBuildId -ne $uiBuildId) {
    throw 'STOP THE RELEASE: generated build identity does not match this release.'
}

if ($signingEnabled) {
    Invoke-CodeSignPublishedBinaries $desktopPublish
    Invoke-CodeSignPublishedBinaries $servicePublish
} else {
    Write-Warning 'CURRENTLY SIGNED: NO. Supply SigningCertificateThumbprint and TimestampServerUrl to sign release binaries and installers.'
}
Assert-ServicePublishTrust $servicePublish

Copy-Item -Path (Join-Path $desktopPublish '*') -Destination $portablePublish -Recurse -Force
$portableService = Join-Path $portablePublish 'service'
New-Item -ItemType Directory -Path $portableService -Force | Out-Null
Copy-Item -Path (Join-Path $servicePublish '*') -Destination $portableService -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'installer\Start-DynaK-Portable.cmd') -Destination $portablePublish -Force
Assert-PublishedUi $portableService
Assert-NoMarkOfWeb $portablePublish
$portableBuildInfo = Get-Content -LiteralPath (Join-Path $portableService 'build-info.json') -Raw | ConvertFrom-Json
if ($portableBuildInfo.uiBuildId -ne $uiBuildId) {
    throw 'STOP THE RELEASE: portable package contains the wrong UI build identity.'
}

$setupPath = Join-Path $installerOutput "DynaK-Wet-Leak-Test-Setup-v$Version.exe"
$makeNsisArguments = @(
    "/DPRODUCT_VERSION=$Version",
    "/DPRODUCT_VERSION_FILE=$fileVersion",
    "/DDESKTOP_PUBLISH_DIR=$desktopPublish",
    "/DSERVICE_PUBLISH_DIR=$servicePublish",
    "/DSETUP_OUTPUT_PATH=$setupPath",
    "/DAPP_ICON=$appIcon"
)

if (-not [string]::IsNullOrWhiteSpace($WebView2InstallerPath)) {
    if (-not (Test-Path -LiteralPath $WebView2InstallerPath)) {
        throw "The supplied WebView2 installer was not found at $WebView2InstallerPath."
    }

    $makeNsisArguments += "/DWEBVIEW2_INSTALLER=$((Resolve-Path -LiteralPath $WebView2InstallerPath).Path)"
} else {
    Write-Warning 'WebView2 is not embedded. The installer detects an installed runtime and stops with a clear message when it is absent. Supply WebView2InstallerPath to embed the offline Microsoft EXE prerequisite.'
}

& $makeNsisPath /WX @makeNsisArguments (Join-Path $projectRoot 'installer\DynaK.Installer.nsi')
if ($LASTEXITCODE -ne 0) { throw 'NSIS installer build failed.' }

if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "The expected NSIS setup executable was not produced at $setupPath."
}
Wait-ForFileCompletion $setupPath

if (Get-ChildItem -LiteralPath $installerOutput -Filter '*.msi' -Recurse -File) {
    throw 'MSI output was produced unexpectedly. The production release path must remain direct NSIS only.'
}

if ($signingEnabled) {
    Invoke-CodeSign $setupPath
}
Assert-NoMarkOfWeb $installerOutput

$releaseSetupPath = Join-Path $releaseOutput "DynaK-Wet-Leak-Test-Setup-v$Version.exe"
Copy-Item -LiteralPath $setupPath -Destination $releaseSetupPath -Force

$portablePath = Join-Path $releaseOutput "DynaK-Wet-Leak-Test-Portable-v$Version.zip"
Compress-Archive -Path (Join-Path $portablePublish '*') -DestinationPath $portablePath -CompressionLevel Optimal -Force
Assert-NoMarkOfWeb $releaseOutput

if ((Get-ChildItem -LiteralPath $releaseOutput -File).Count -ne 2) {
    throw "Release output must contain the direct installer and portable ZIP: $releaseOutput"
}

Write-Output $releaseSetupPath
Write-Output $portablePath
Write-Output "UI Build ID: $uiBuildId"
Write-Output "Source revision: $sourceRevision"
