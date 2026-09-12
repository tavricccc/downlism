# One version for the whole product: the installer is never released separately from the
# app it installs, so a second number would only ever be a thing to keep in sync.
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.3.0'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = Join-Path $projectRoot 'artifacts/installer/current'
$work = Join-Path $projectRoot ('artifacts/installer/.build-' + [guid]::NewGuid().ToString('N'))
$stagedRelease = Join-Path $work 'release'
function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
function Assert-SafeTree([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $allowed = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/installer')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe build path: $resolved" }
    for ($ancestor = $resolved; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Build path contains a reparse point.' }
    }
    if (Test-Path -LiteralPath $resolved) {
        foreach ($item in Get-ChildItem -LiteralPath $resolved -Recurse -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Build contents contain a reparse point.' }
        }
    }
}
Push-Location $projectRoot
try {
    Assert-SafeTree $releaseRoot
    Assert-SafeTree $work

    & (Join-Path $PSScriptRoot 'New-BrandIcon.ps1') | Out-Null
    Invoke-Dotnet @('test', 'tests/Downlism.Tests/Downlism.Tests.csproj', '-c', 'Release')

    # The extension ships inside the installation folder so it can be loaded unpacked from a
    # stable path. Its embedded key fixes the ID, which is what the host registration names.
    Push-Location (Join-Path $projectRoot 'extension')
    try {
        & npm install --silent --no-fund --no-audit
        if ($LASTEXITCODE -ne 0) { throw 'npm install failed' }
        & npx tsc --noEmit
        if ($LASTEXITCODE -ne 0) { throw 'Extension typecheck failed' }
        & npm test --silent
        if ($LASTEXITCODE -ne 0) { throw 'Extension tests failed' }
        & node build.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Extension build failed' }
    } finally {
        Pop-Location
    }
    $app = Join-Path $work 'app'
    $setup = Join-Path $work 'setup'
    $launcher = Join-Path $work 'launcher'
    Invoke-Dotnet @('publish', 'src/Downlism.App/Downlism.App.csproj', '-c', 'Release', '-p:Platform=x64', "-p:AppVersion=$Version", '-o', $app)
    # The native messaging host ships beside the app: browsers launch it by absolute path,
    # so it must live in the installation folder and never in a user-writable location.
    Invoke-Dotnet @('publish', 'src/Downlism.Host/Downlism.Host.csproj', '-c', 'Release', '-p:Platform=x64', "-p:AppVersion=$Version", '-o', $app)
    Invoke-Dotnet @('publish', 'src/Downlism.Setup/Downlism.Setup.csproj', '-c', 'Release', '-p:Platform=x64', "-p:AppVersion=$Version", '-o', $setup)
    Invoke-Dotnet @('publish', 'src/Downlism.Bootstrap/Downlism.Bootstrap.csproj', '-c', 'Release', "-p:AppVersion=$Version", '-o', $launcher)
    foreach ($name in @('Downlism.Install.dll', 'Downlism.Install.deps.json', 'Downlism.Install.runtimeconfig.json')) {
        Copy-Item -LiteralPath (Join-Path $launcher $name) -Destination $app
    }
    # Shared runtime files must be identical. Never silently choose conflicting dependencies.
    foreach ($file in Get-ChildItem -LiteralPath $setup -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($setup, $file.FullName)
        $destination = Join-Path $app $relative
        if (Test-Path -LiteralPath $destination) {
            if ((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Conflicting shared file: $relative" }
        } else {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
    Copy-Item -Path (Join-Path $projectRoot 'extension/dist') -Destination (Join-Path $app 'extension') -Recurse

    $files = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $app -File -Recurse | Sort-Object FullName) {
        $files[[IO.Path]::GetRelativePath($app, $file.FullName)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    foreach ($required in @('Downlism.App.exe', 'Downlism.Setup.exe', 'Downlism.Host.exe', 'coreclr.dll', 'Microsoft.UI.Xaml.dll')) {
        if (!$files.Contains($required)) { throw "Missing $required" }
    }
    # coreclr.dll being present is not proof that each executable carries its own runtime: the
    # shared files are merged from several publishes, so one framework-dependent executable can
    # sit among them and only fail on a machine without the matching .NET installed.
    foreach ($runtimeConfig in Get-ChildItem -LiteralPath $app -Filter 'Downlism.*.runtimeconfig.json' -File) {
        $options = (Get-Content -LiteralPath $runtimeConfig.FullName -Raw | ConvertFrom-Json).runtimeOptions
        if ($options.PSObject.Properties.Name -contains 'framework' -or
            $options.PSObject.Properties.Name -contains 'frameworks') {
            throw "$($runtimeConfig.Name) is framework-dependent; publish it self-contained."
        }
    }
    # Finish the entire release before touching current. Old installers are never pruned.
    @{ Product = 'Downlism'; Version = $Version; Files = $files } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $app 'downlism-install.json') -Encoding utf8
    [IO.Directory]::CreateDirectory($stagedRelease) | Out-Null
    $stagedResources = Join-Path $stagedRelease 'resources'
    Assert-SafeTree $app
    Assert-SafeTree $stagedResources
    Move-Item -LiteralPath $app -Destination $stagedResources
    # Generate a native .NET apphost whose managed entry point lives in resources.
    # It resolves the self-contained runtime there, without another copy or a shell wrapper.
    $dotnetRoot = Split-Path (Get-Command dotnet).Source
    $sdk = (& dotnet --version).Trim()
    Add-Type -Path (Join-Path $dotnetRoot "sdk/$sdk/Microsoft.NET.HostModel.dll")
    $hostPack = Get-ChildItem -LiteralPath (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Host.win-x64') -Directory |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    $hostTemplate = Join-Path $hostPack.FullName 'runtimes/win-x64/native/apphost.exe'
    [Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost($hostTemplate, (Join-Path $stagedRelease 'Downlism.Setup.exe'),
        'resources/Downlism.Install.dll', $true, (Join-Path $launcher 'Downlism.Install.exe'), $false, $false, $null)
    $archive = $null
    $emptyRelease = (Test-Path -LiteralPath $releaseRoot) -and !(Get-ChildItem -LiteralPath $releaseRoot -File -Recurse | Where-Object { $_.FullName -ne (Join-Path $releaseRoot 'Downlism.Setup.exe') } | Select-Object -First 1)
    if ((Test-Path -LiteralPath $releaseRoot) -and !$emptyRelease) {
        $oldManifest = Join-Path $releaseRoot 'resources/downlism-install.json'
        if (!(Test-Path -LiteralPath $oldManifest)) { $oldManifest = Join-Path $releaseRoot 'downlism-install.json' }
        $previousVersion = 'unknown'
        if (Test-Path -LiteralPath $oldManifest) {
            $previous = Get-Content -LiteralPath $oldManifest -Raw | ConvertFrom-Json
            if ($previous.Version -match '^\d+\.\d+\.\d+$') { $previousVersion = $previous.Version }
        }
        $archive = Join-Path $projectRoot ('artifacts/installer/history/' + $previousVersion + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
        Assert-SafeTree $releaseRoot
        Assert-SafeTree $archive
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($archive)) | Out-Null
        # Rename atomically: Move-Item can partially move a tree before a locked directory fails.
        [IO.Directory]::Move($releaseRoot, $archive)
    }
    try {
        Assert-SafeTree $stagedRelease
        Assert-SafeTree $releaseRoot
        if ($emptyRelease) {
            # Recover an empty directory left by an older partial Move-Item operation.
            Copy-Item -Path (Join-Path $stagedRelease '*') -Destination $releaseRoot -Recurse -Force
        } else {
            [IO.Directory]::Move($stagedRelease, $releaseRoot)
        }
    } catch {
        if ($archive -and !(Test-Path -LiteralPath $releaseRoot)) {
            Assert-SafeTree $archive
            Assert-SafeTree $releaseRoot
            Move-Item -LiteralPath $archive -Destination $releaseRoot
        }
        throw
    }
    Write-Output "Installer folder: $releaseRoot"
    Write-Output "Run Downlism.Setup.exe (version $Version)."
    if ($archive) { Write-Output "Previous installer preserved: $archive" }
} finally {
    Pop-Location
    if (Test-Path -LiteralPath $work) {
        Assert-SafeTree $work
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
