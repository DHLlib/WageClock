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

Copy-Item (Join-Path $wv2 'lib\net462\Microsoft.Web.WebView2.Core.dll') $out -Force
Copy-Item (Join-Path $wv2 'lib\net462\Microsoft.Web.WebView2.WinForms.dll') $out -Force
Copy-Item (Join-Path $wv2 'lib\net462\Microsoft.Web.WebView2.Core.dll') $lib -Force
Copy-Item (Join-Path $wv2 'lib\net462\Microsoft.Web.WebView2.WinForms.dll') $lib -Force
Copy-Item (Join-Path $wv2 'runtimes\win-x64\native\WebView2Loader.dll') $out -Force

$refs = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Web.Extensions.dll',
    (Join-Path $lib 'Microsoft.Web.WebView2.Core.dll'),
    (Join-Path $lib 'Microsoft.Web.WebView2.WinForms.dll')
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
    (Join-Path (Get-Location) 'host\NativeSettingsForm.cs'),
    (Join-Path (Get-Location) 'host\Shot.cs')
)

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

# runtime payload
Remove-Item (Join-Path $out 'src') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $out 'assets') -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path (Get-Location) 'src') $out -Recurse -Force
Copy-Item (Join-Path (Get-Location) 'assets') $out -Recurse -Force
Remove-Item (Join-Path $out 'assets\icon-src.html') -Force -ErrorAction SilentlyContinue

Write-Output ("built: " + (Join-Path $out 'WageClock.exe') + "  " + (Get-Item (Join-Path $out 'WageClock.exe')).Length + " bytes")
