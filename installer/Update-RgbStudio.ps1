# El proceso espera al cierre de RGB Studio antes de actualizar sus archivos.
param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedSha256,
    [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int]$WaitProcessId,
    [Parameter(Mandatory = $true)][string]$Destination,
    [switch]$NoRestart
)
$ErrorActionPreference = 'Stop'
$appExecutable = $null
try {
    $Destination = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    $appExecutable = Join-Path $Destination 'RgbStudio.exe'
    if (-not (Test-Path -LiteralPath (Join-Path $Destination 'rgbstudio-installed.marker') -PathType Leaf) -or
        -not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) { throw 'La carpeta de destino no es una instalación de RGB Studio.' }
    $cacheRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'RgbStudio\Updates')).TrimEnd('\') + '\'
    $Installer = [IO.Path]::GetFullPath($Installer)
    if (-not $Installer.StartsWith($cacheRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($Installer) -notmatch '^RGB-Studio-\d+\.\d+\.\d+-win-x64-Setup\.exe$') {
        throw 'El instalador no está en la caché de actualizaciones de RGB Studio.'
    }
    $waiting = Get-Process -Id $WaitProcessId -ErrorAction SilentlyContinue
    if ($null -ne $waiting) {
        if (-not $waiting.Path.Equals($appExecutable, [StringComparison]::OrdinalIgnoreCase)) { throw 'El proceso de origen no coincide con RGB Studio.' }
        Wait-Process -Id $WaitProcessId -Timeout 60 -ErrorAction Stop
    }
    if ((Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash -ne $ExpectedSha256) {
        throw 'La verificación SHA-256 del instalador falló.'
    }
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CLOSEAPPLICATIONS', ('/DIR="' + $Destination + '"'))
    $setup = Start-Process -FilePath $Installer -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($setup.ExitCode -ne 0) { throw "El instalador terminó con el código $($setup.ExitCode)." }
    if (-not $NoRestart) { Start-Process -FilePath $appExecutable -WorkingDirectory $Destination }
    exit 0
}
catch {
    $failureDirectory = Join-Path $env:LOCALAPPDATA 'RgbStudio\Updates'
    New-Item -ItemType Directory -Path $failureDirectory -Force | Out-Null
    $_.Exception.Message | Set-Content -LiteralPath (Join-Path $failureDirectory 'last-install-error.txt') -Encoding utf8
    if (-not $NoRestart -and $appExecutable -and (Test-Path -LiteralPath $appExecutable)) {
        Start-Process -FilePath $appExecutable -WorkingDirectory $Destination
    }
    exit 1
}
