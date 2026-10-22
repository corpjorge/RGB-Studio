# Prueba real del instalador solo en el runner desechable de GitHub Actions.
param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Esta prueba instala y desinstala la aplicación: ejecútala únicamente en GitHub Actions.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$install = Join-Path $repoRoot ('bin/installer-smoke/' + [Guid]::NewGuid().ToString('N'))
$arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR="' + $install + '"'))
$process = Start-Process -FilePath $Installer -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "El instalador falló: $($process.ExitCode)" }
$diagnostic = Join-Path $install 'verification.json'
$app = Start-Process -FilePath (Join-Path $install 'RgbStudio.exe') -ArgumentList @('--verify-installation', ('"' + $diagnostic + '"')) -WindowStyle Hidden -Wait -PassThru
if ($app.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $diagnostic)) { throw 'La aplicación instalada no pudo ejecutarse.' }
$result = Get-Content -LiteralPath $diagnostic -Raw | ConvertFrom-Json
if ($result.Version -ne $Version -or -not $result.SelfContained -or -not $result.Installed -or $result.Architecture -ne 'X64') { throw 'La aplicación instalada no coincide con el paquete esperado.' }
if (-not $result.RuntimeDirectory.StartsWith(($install.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'La aplicación usó un runtime global en vez del incluido en el paquete.' }
$profileDirectory = Join-Path $env:APPDATA 'RgbStudio'
New-Item -ItemType Directory -Path $profileDirectory -Force | Out-Null
$sentinel = Join-Path $profileDirectory ('installer-check-' + [Guid]::NewGuid().ToString('N') + '.txt')
Set-Content -LiteralPath $sentinel -Value 'keep-user-settings'

# Verifica el mismo mecanismo que usan las actualizaciones: SHA-256, espera al
# proceso, instalación silenciosa y conservación de la carpeta instalada.
$cache = Join-Path $env:LOCALAPPDATA "RgbStudio/Updates/$Version"
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$cachedInstaller = Join-Path $cache ([IO.Path]::GetFileName($Installer))
Copy-Item -LiteralPath $Installer -Destination $cachedInstaller
$sha = (Get-FileHash -LiteralPath $cachedInstaller).Hash
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $install 'Update-RgbStudio.ps1') `
    -Installer $cachedInstaller -ExpectedSha256 $sha -WaitProcessId 2147483647 -Destination $install -NoRestart
if ($LASTEXITCODE -ne 0) { throw 'El mecanismo de actualización del instalador falló.' }
$app = Start-Process -FilePath (Join-Path $install 'RgbStudio.exe') -ArgumentList @('--verify-installation', ('"' + $diagnostic + '"')) -WindowStyle Hidden -Wait -PassThru
if ($app.ExitCode -ne 0) { throw 'La aplicación no inició después de actualizar.' }
$uninstall = Start-Process -FilePath (Join-Path $install 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -WindowStyle Hidden -Wait -PassThru
if ($uninstall.ExitCode -ne 0) { throw 'La desinstalación de prueba falló.' }
if (-not (Test-Path -LiteralPath $sentinel)) { throw 'El instalador eliminó datos del usuario.' }
Remove-Item -LiteralPath $sentinel
Write-Output "Instalación, ejecución independiente, actualización y desinstalación verificadas: $Version"
