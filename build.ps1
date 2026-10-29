# Rebuild 給料計算.exe from src\Program.cs (needs only the .NET Framework that ships with Windows).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$out = Join-Path $root '給料計算.exe'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$out" "/win32icon:$(Join-Path $root 'icon.ico')" /r:System.Web.Extensions.dll (Join-Path $root 'src\Program.cs')
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Write-Host "built: $out"