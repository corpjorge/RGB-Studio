# Compilador oficial fijado y validado; solo se usa para construir releases.
param([Parameter(Mandatory = $true)][string]$ToolsDirectory)
$ErrorActionPreference = 'Stop'
$ToolsDirectory = [IO.Path]::GetFullPath($ToolsDirectory)
$compilerDirectory = Join-Path $ToolsDirectory 'inno-6.7.3'
$compiler = Join-Path $compilerDirectory 'ISCC.exe'
if (Test-Path -LiteralPath $compiler -PathType Leaf) { return $compiler }
New-Item -ItemType Directory -Path $ToolsDirectory -Force | Out-Null
$bootstrap = Join-Path $ToolsDirectory 'innosetup-6.7.3.exe'
$expected = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
if (-not (Test-Path -LiteralPath $bootstrap) -or (Get-FileHash -LiteralPath $bootstrap).Hash -ne $expected) {
    Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $bootstrap
}
if ((Get-FileHash -LiteralPath $bootstrap).Hash -ne $expected) { throw 'El compilador Inno Setup no coincide con su SHA-256 oficial.' }
$arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/NOICONS', '/MERGETASKS=!fileassoc', ('/DIR="' + $compilerDirectory + '"'))
$process = Start-Process -FilePath $bootstrap -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler)) { throw 'No se pudo preparar el compilador Inno Setup.' }
return $compiler
