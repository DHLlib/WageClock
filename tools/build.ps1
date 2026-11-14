$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location (Split-Path -Parent $root)

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wv2 = Join-Path (Get-Location) 'tools\wv2'
$out = Join-Path (Get-Location) 'build'
$lib = $out

if (-not (Test-Path (Join-Path (Get-Location) 'assets\icon.ico'))) {
    & (Join-Path $root 'make-icon.ps1')
}

New-Item -ItemType Directory -Force -Path $out | Out-Null

# 不再需要 WebView2Loader.dll

$refs = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Web.Extensions.dll'
) | ForEach-Object { '/r:"' + $_ + '"' }

$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/langversion:5',
    ('/out:"' + (Join-Path $out 'WageClock.exe') + '"'),
    ('/win32icon:"' + (Join-Path (Get-Location) 'assets\icon.ico') + '"'),
    ('/win32manifest:"' + (Join-Path (Get-Location) 'host\app.manifest') + '"')
) + $refs + @(
    (Join-Path (Get-Location) 'host\AssemblyInfo.cs'),
    (Join-Path (Get-Location) 'host\WageClock.cs'),
    (Join-Path (Get-Location) 'host\Schedule.cs'),
    (Join-Path (Get-Location) 'host\SelfTest.cs'),
    (Join-Path (Get-Location) 'host\Theme.cs'),
    (Join-Path (Get-Location) 'host\CardWindow.cs'),
    (Join-Path (Get-Location) 'host\NativeSettingsForm.cs')
)

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

# runtime payload
Remove-Item (Join-Path $out 'src') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $out 'assets') -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path (Get-Location) 'assets') $out -Recurse -Force
Remove-Item (Join-Path $out 'assets\icon-src.html') -Force -ErrorAction SilentlyContinue

Write-Output ("built: " + (Join-Path $out 'WageClock.exe') + "  " + (Get-Item (Join-Path $out 'WageClock.exe')).Length + " bytes")
