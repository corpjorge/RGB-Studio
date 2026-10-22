# Prueba solo la selección de rutas: no inicia procesos ni necesita hardware.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testRoot = Join-Path $repoRoot ('bin/launcher-checks/' + [Guid]::NewGuid().ToString('N'))
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'Iniciar.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'Iniciar.ps1 tiene errores de sintaxis.' }
$resolver = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Obtener-OpenRgbAdministrado' }, $true)
if ($null -eq $resolver) { throw 'No se encuentra la función de selección de OpenRGB.' }
. ([ScriptBlock]::Create($resolver.Extent.Text))

New-Item -ItemType Directory -Path (Join-Path $testRoot 'versions/current'), (Join-Path $testRoot 'versions/previous') -Force | Out-Null
$currentExe = Join-Path $testRoot 'versions/current/OpenRGB.exe'
$previousExe = Join-Path $testRoot 'versions/previous/OpenRGB.exe'
Set-Content -LiteralPath $currentExe -Value 'fixture-current'
Set-Content -LiteralPath $previousExe -Value 'fixture-previous'
$current = [pscustomobject]@{ Version = '2.0'; Directory = 'current'; Executable = 'OpenRGB.exe'; ExecutableSha256 = (Get-FileHash -LiteralPath $currentExe).Hash }
$previous = [pscustomobject]@{ Version = '1.1'; Directory = 'previous'; Executable = 'OpenRGB.exe'; ExecutableSha256 = (Get-FileHash -LiteralPath $previousExe).Hash }
$manifest = Join-Path $testRoot 'installation.json'
function Guardar-Fixture {
    @{ Current = $current; Previous = $previous } | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8
}
function Verificar-Fixture([string]$esperado, [string]$nombre) {
    $actual = Obtener-OpenRgbAdministrado $testRoot
    if ([string]$actual -ne $esperado) { throw "Falló ${nombre}: esperado '$esperado', obtenido '$actual'." }
    Write-Output "OK $nombre"
}
Guardar-Fixture
Verificar-Fixture $currentExe 'Elige la versión actual con hash correcto'
Set-Content -LiteralPath $currentExe -Value 'fixture-alterada'
Verificar-Fixture $previousExe 'Usa la anterior si cambia el ejecutable actual'
foreach ($path in @('../outside', 'C:\outside', 'current.', 'current/../previous')) {
    $current.Directory = $path
    Guardar-Fixture
    Verificar-Fixture $previousExe "Rechaza una carpeta no válida: $path"
}
$current.Directory = 'current'
$current.Executable = '../previous/OpenRGB.exe'
Guardar-Fixture
Verificar-Fixture $previousExe 'Rechaza un ejecutable que sale de su versión'
Set-Content -LiteralPath $manifest -Value '{invalid'
Verificar-Fixture $null 'Ignora un manifiesto corrupto'
Set-Content -LiteralPath $manifest -Value '{"Current":null}'
Verificar-Fixture $null 'Ignora un manifiesto sin instalación'
Write-Output '9 comprobaciones del lanzador correctas. No se ha iniciado OpenRGB.'
