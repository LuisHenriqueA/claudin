# Publica o Claudin como executável único, self-contained (win-x64), em artifacts\publish.
# Uso: powershell -ExecutionPolicy Bypass -File tools\publish.ps1
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = Join-Path $root 'artifacts\publish'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe" }

if (Test-Path $out) { Remove-Item -Recurse -Force $out }

& $dotnet publish (Join-Path $root 'src\Claudin\Claudin.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $out -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou ($LASTEXITCODE)" }

$exe = Join-Path $out 'Claudin.exe'
$info = (Get-Item $exe).VersionInfo
"Publicado: $exe"
"Versão: $($info.ProductVersion)  Tamanho: {0:N1} MB" -f ((Get-Item $exe).Length / 1MB)
Get-ChildItem $out | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
