# Publica únicamente un instalador completo y verificado; no reemplaza releases.
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory = $true)][string]$Target,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot "bin/releases/$Version" }
$installer = Join-Path $OutputDirectory "RGB-Studio-$Version-win-x64-Setup.exe"
if (-not (Test-Path -LiteralPath $installer) -or -not (Test-Path -LiteralPath ($installer + '.sha256'))) { throw 'Faltan los archivos del release.' }
$notes = Join-Path $OutputDirectory 'release-notes.md'
@"
RGB Studio $Version para Windows x64, con .NET incluido.

- Descarga **RGB-Studio-$Version-win-x64-Setup.exe**, ejecútalo y abre RGB Studio desde Inicio.
- OpenRGB se descarga automáticamente desde su distribución oficial y se inicia al estar listo.
- RGB Studio avisa de nuevas versiones, descarga el instalador y permite actualizar y reiniciar.
- Las calibraciones y la configuración de tiras se conservan entre versiones.
- Para RAM por SMBus se necesita PawnIO de https://pawnio.eu y aceptar el permiso de administrador de OpenRGB.

Requiere Windows 10 22H2/Windows 11 x64 e Internet para la primera descarga de OpenRGB.
El instalador no incluye una firma de editor; Windows puede mostrar un aviso de editor desconocido.
La compatibilidad del hardware depende de OpenRGB. Consulta el README para las instrucciones y requisitos.

Incluye un archivo .sha256 para comprobar la integridad de la descarga.
"@ | Set-Content -LiteralPath $notes -Encoding utf8
& gh release create "v$Version" $installer ($installer + '.sha256') --repo corpjorge/RGB-Studio --target $Target --title "RGB Studio $Version" --notes-file $notes --latest
if ($LASTEXITCODE -ne 0) { throw 'No se pudo publicar el release.' }
