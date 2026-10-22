# Arranca RGB Studio, levantando antes OpenRGB si no esta en marcha.
#
# RGB Studio no habla con el hardware directamente: se lo pide al servidor SDK
# de OpenRGB. Sin ese servidor escuchando, la aplicacion abre pero no encuentra
# ningun dispositivo.

param([switch]$UsarOpenRgbAnterior)

$ErrorActionPreference = 'Stop'

function Obtener-OpenRgbAdministrado([string]$carpeta) {
    $manifiesto = Join-Path $carpeta 'installation.json'
    if (-not (Test-Path -LiteralPath $manifiesto -PathType Leaf)) { return $null }

    try {
        $versiones = Get-Content -LiteralPath $manifiesto -Raw | ConvertFrom-Json
        $raizVersiones = [IO.Path]::GetFullPath((Join-Path $carpeta 'versions')).TrimEnd('\') + '\'
        foreach ($version in @($versiones.Current, $versiones.Previous)) {
            if ($null -eq $version) { continue }
            $numeroVersion = $null
            if (-not [Version]::TryParse([string]$version.Version, [ref]$numeroVersion)) { continue }
            if ([string]::IsNullOrWhiteSpace($version.Directory) -or
                [string]::IsNullOrWhiteSpace($version.Executable) -or
                [IO.Path]::IsPathRooted($version.Directory) -or
                [IO.Path]::IsPathRooted($version.Executable) -or
                $version.Directory.Contains(':') -or $version.Executable.Contains(':')) { continue }
            $segmentos = @($version.Directory -split '[\\/]') + @($version.Executable -split '[\\/]')
            if (@($segmentos | Where-Object { $_ -in @('.', '..') -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count -gt 0) { continue }

            $directorio = [IO.Path]::GetFullPath((Join-Path $raizVersiones $version.Directory)).TrimEnd('\') + '\'
            $ejecutable = [IO.Path]::GetFullPath((Join-Path $directorio $version.Executable))
            if (-not $directorio.StartsWith($raizVersiones, [StringComparison]::OrdinalIgnoreCase) -or
                -not $ejecutable.StartsWith($directorio, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($ejecutable) -ine 'OpenRGB.exe') { continue }
            if (-not (Test-Path -LiteralPath $ejecutable -PathType Leaf)) { continue }
            if ($version.ExecutableSha256 -notmatch '^[A-Fa-f0-9]{64}$') { continue }
            if ((Get-FileHash -LiteralPath $ejecutable -Algorithm SHA256).Hash -eq $version.ExecutableSha256) {
                return $ejecutable
            }
        }
    }
    catch {
        Write-Host 'No se pudo leer la version administrada. Se usara la instalacion anterior.' -ForegroundColor Yellow
    }
    return $null
}

$raiz             = Split-Path -Parent $PSScriptRoot
$openRgbCorregido = Join-Path $raiz 'OpenRGB-Custom-Build-v2\OpenRGB.exe'
$openRgbOficial   = Join-Path $raiz 'OpenRGB\OpenRGB.exe'
$carpetaAdministrada = Join-Path $env:LOCALAPPDATA 'RgbStudio\OpenRGB'
$openRgbAdministrado = if (-not $UsarOpenRgbAnterior) { Obtener-OpenRgbAdministrado $carpetaAdministrada } else { $null }
$openRgb          = if ($openRgbAdministrado) { $openRgbAdministrado }
                    elseif (Test-Path $openRgbCorregido) { $openRgbCorregido }
                    else { $openRgbOficial }
$estudio          = Join-Path $PSScriptRoot 'App\bin\Release\net10.0-windows\RgbStudio.exe'

function Servidor-Escuchando {
    $null -ne (Get-NetTCPConnection -LocalPort 6742 -State Listen -ErrorAction SilentlyContinue)
}

if (-not (Test-Path $estudio)) {
    Write-Host "No se encuentra RGB Studio en:`n  $estudio" -ForegroundColor Red
    Write-Host "Compilalo con:  dotnet build App/App.csproj -c Release" -ForegroundColor Yellow
    Read-Host "`nEnter para salir"
    exit 1
}

if (Servidor-Escuchando) {
    Write-Host "Servidor de OpenRGB ya activo." -ForegroundColor Green
    if ($openRgbAdministrado) {
        Write-Host 'Las actualizaciones preparadas se usaran cuando se vuelva a iniciar OpenRGB.' -ForegroundColor DarkGray
    }
}
elseif (Test-Path $openRgb) {
    Write-Host "Arrancando OpenRGB (pedira permisos de administrador)..."
    Write-Host "La elevacion hace falta para ver la RAM, que va por el bus SMBus." -ForegroundColor DarkGray

    # -WorkingDirectory es obligatorio: OpenRGB busca sus modulos de PawnIO
    # junto al ejecutable, y sin eso no accede al bus de la memoria.
    Start-Process -FilePath $openRgb `
        -ArgumentList '--server' `
        -WorkingDirectory (Split-Path $openRgb) `
        -Verb RunAs `
        -WindowStyle Hidden

    Write-Host -NoNewline "Esperando al servidor"
    $limite = (Get-Date).AddSeconds(25)
    while (-not (Servidor-Escuchando) -and (Get-Date) -lt $limite) {
        Start-Sleep -Milliseconds 700
        Write-Host -NoNewline '.'
    }
    Write-Host ''

    if (-not (Servidor-Escuchando)) {
        Write-Host "El servidor no respondio." -ForegroundColor Yellow
        Write-Host "Comprueba que aceptaste el permiso de administrador de OpenRGB." -ForegroundColor Yellow
    }
}
else {
    Write-Host "No se encuentra OpenRGB en:`n  $openRgb" -ForegroundColor Yellow
    Write-Host "RGB Studio abrira, pero sin dispositivos." -ForegroundColor Yellow
}

Start-Process $estudio
