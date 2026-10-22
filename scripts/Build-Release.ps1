# Genera un instalador independiente de .NET sin incluir OpenRGB ni controladores.
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0',
    [string]$OutputDirectory,
    [string]$IsccPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ('bin/releases/' + $Version) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
# Carpeta única para impedir que un publish previo contamine la nueva distribución.
$publish = Join-Path $repoRoot ('bin/publish/' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if (-not $IsccPath) { $IsccPath = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1') -ToolsDirectory (Join-Path $repoRoot 'bin/tools') }
& dotnet publish (Join-Path $repoRoot 'App/App.csproj') -c Release -r win-x64 --self-contained true -o $publish `
    "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false -p:PublishSingleFile=false -p:PublishTrimmed=false -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación de RGB Studio.' }
foreach ($required in @('RgbStudio.exe', 'RgbStudio.dll', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Falta un componente del paquete independiente: $required" }
}
$packagesRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$dependencyFile = Get-Content -LiteralPath (Join-Path $publish 'RgbStudio.deps.json') -Raw | ConvertFrom-Json
$packageNames = @($dependencyFile.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Name.ToLowerInvariant() })
$runtimeConfiguration = Get-Content -LiteralPath (Join-Path $publish 'RgbStudio.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtimeConfiguration.runtimeOptions.includedFrameworks) {
    $packageNames += $framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version
}
foreach ($package in $packageNames | Select-Object -Unique) {
    $source = Join-Path $packagesRoot $package
    if (-not (Test-Path -LiteralPath $source)) { continue }
    $notices = @(Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES|COPYING)' })
    if ($notices.Count) {
        $noticeDirectory = Join-Path $publish ('licenses/' + $package.Replace('/', '-'))
        New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
        foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $noticeDirectory }
    }
}
& $IsccPath "/DAppVersion=$Version" "/DPublishDir=$publish" "/DOutputDir=$OutputDirectory" (Join-Path $repoRoot 'installer/RgbStudio.iss')
if ($LASTEXITCODE -ne 0) { throw 'Falló la construcción del instalador.' }
$installer = Join-Path $OutputDirectory "RGB-Studio-$Version-win-x64-Setup.exe"
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
($hash + '  ' + [IO.Path]::GetFileName($installer)) | Set-Content -LiteralPath ($installer + '.sha256') -Encoding ascii
Write-Output "Instalador: $installer"
Write-Output "SHA-256: $hash"
