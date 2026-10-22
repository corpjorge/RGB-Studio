# Arranca RGB Studio, levantando antes OpenRGB si no esta en marcha.
#
# RGB Studio no habla con el hardware directamente: se lo pide al servidor SDK
# de OpenRGB. Sin ese servidor escuchando, la aplicacion abre pero no encuentra
# ningun dispositivo.

$ErrorActionPreference = 'Stop'

$raiz             = Split-Path -Parent $PSScriptRoot
$openRgbCorregido = Join-Path $raiz 'OpenRGB-Custom-Build-v2\OpenRGB.exe'
$openRgbOficial   = Join-Path $raiz 'OpenRGB\OpenRGB.exe'
$openRgb          = if (Test-Path $openRgbCorregido) { $openRgbCorregido } else { $openRgbOficial }
$estudio          = Join-Path $PSScriptRoot 'App\bin\Release\net10.0-windows\RgbStudio.exe'

function Servidor-Escuchando {
    $null -ne (Get-NetTCPConnection -LocalPort 6742 -State Listen -ErrorAction SilentlyContinue)
}

if (-not (Test-Path $estudio)) {
    Write-Host "No se encuentra RGB Studio en:`n  $estudio" -ForegroundColor Red
    Write-Host "Compilalo con:  dotnet build -c Release" -ForegroundColor Yellow
    Read-Host "`nEnter para salir"
    exit 1
}

if (Servidor-Escuchando) {
    Write-Host "Servidor de OpenRGB ya activo." -ForegroundColor Green
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
