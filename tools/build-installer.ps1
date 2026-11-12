$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$build = Join-Path $root 'build'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path (Join-Path $build 'WageClock.exe'))) {
    Write-Output '先编译主程序…'
    & (Join-Path $root 'tools\build.ps1')
}

# 1. 打包程序文件（不含日志与运行时数据）
$stage = Join-Path $build '_payload'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item (Join-Path $build 'WageClock.exe') $stage
Get-ChildItem $build -Filter '*.dll' -File | Copy-Item -Destination $stage
foreach ($dir in @('src', 'assets')) {
    $from = Join-Path $build $dir
    if (Test-Path $from) { Copy-Item $from $stage -Recurse }
}
$zip = Join-Path $build 'payload.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force
"{0,-28} {1,8:N0} KB" -f 'payload.zip', ((Get-Item $zip).Length / 1KB)

# 2. WebView2 引导程序（缺失时下载一次，缓存在 build 里）
$cache = Join-Path $build 'cache'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$boot = Join-Path $cache 'MicrosoftEdgeWebview2Setup.exe'
if (-not (Test-Path $boot)) {
    Write-Output '下载 WebView2 引导程序…'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $boot -UseBasicParsing -TimeoutSec 180
}
"{0,-28} {1,8:N0} KB" -f 'webview2 bootstrapper', ((Get-Item $boot).Length / 1KB)

# 3. 编译安装程序
$out = Join-Path $build 'WageClockSetup.exe'
if (Test-Path $out) { Remove-Item $out -Force }
& $csc /nologo /target:winexe /optimize+ /codepage:65001 `
    "/win32icon:$root\assets\icon.ico" `
    "/win32manifest:$root\host\app.manifest" `
    "/out:$out" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    "/resource:$zip,payload.zip" `
    "/resource:$boot,webview2setup.exe" `
    (Join-Path $root 'installer\Setup.cs') (Join-Path $root 'installer\AssemblyInfo.cs')
if ($LASTEXITCODE -ne 0) { throw "编译安装程序失败" }
"{0,-28} {1,8:N0} KB" -f 'WageClockSetup.exe', ((Get-Item $out).Length / 1KB)
Write-Output ("setup written: " + $out)
