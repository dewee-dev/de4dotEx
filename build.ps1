#Requires -Version 5.1
<#
.SYNOPSIS
    构建 de4dotEx 的 net48 与 net10.0 产物。

.DESCRIPTION
    不带参数时只构建，行为与历史版本一致。
    -Package  额外把产物打成发布用 zip 放到 dist\。
    -Publish  打包后调用 gh 创建/更新 GitHub Release 并上传附件。
    -SkipBuild 跳过编译，直接用现有产物打包/发布。

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -Package

.EXAMPLE
    .\build.ps1 -Publish -Tag v3.11.0
#>
param(
    [switch]$Package,
    [switch]$Publish,
    [switch]$SkipBuild,
    [string]$Tag,
    [switch]$Draft
)

$ErrorActionPreference = 'Stop'
# Compress-Archive 的进度条会把日志刷满，这里关掉
$ProgressPreference = 'SilentlyContinue'
$root = $PSScriptRoot
Set-Location $root

if (-not $SkipBuild) {
    dotnet build -c Release -f net48 de4dot.netframework.sln
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Remove-Item Release\net48\*.pdb, Release\net48\*.xml, Release\net48\Test.Rename.*

    dotnet publish -c Release -f net10.0 -o publish-net10.0 de4dot
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Remove-Item publish-net10.0\*.pdb, publish-net10.0\*.xml

    dotnet publish -c Release -f net10.0 -o publish-net10.0-mcp de4dot.mcp
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Remove-Item publish-net10.0-mcp\*.pdb, publish-net10.0-mcp\*.xml
}

if (-not ($Package -or $Publish)) { return }

# --- 版本号与 tag -------------------------------------------------------
$props = Get-Content De4DotCommon.props -Raw
if ($props -notmatch '<VersionPrefix>([^<]+)</VersionPrefix>') {
    throw 'De4DotCommon.props 里找不到 VersionPrefix'
}
$version = $Matches[1]

$tagName = if ($Tag) { $Tag } else { "v$version-" + (Get-Date).ToUniversalTime().ToString('MMdd') }
if (-not $tagName.StartsWith('v')) { $tagName = "v$tagName" }
$version = $tagName.Substring(1)

# --- 打包（命名与 CI 产物一致：de4dotEx-<version>-<suffix>.zip）--------
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Remove-Item "$dist\*" -Force -ErrorAction SilentlyContinue

$assets = @(
    @{ Dir = 'Release\net48';   Suffix = 'net48' },
    @{ Dir = 'publish-net10.0'; Suffix = 'net10.0-win-x64' }
)

Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($a in $assets) {
    if (-not (Test-Path $a.Dir)) { throw "缺少构建产物目录：$($a.Dir)" }
    $zip = Join-Path $dist "de4dotEx-$version-$($a.Suffix).zip"
    # 用 ZipFile 而不是 Compress-Archive：快得多，也不会刷进度条
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        (Resolve-Path $a.Dir).Path,
        $zip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
    Get-Item $zip | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
}

if (-not $Publish) {
    Write-Host "`n打包完成：$dist" -ForegroundColor Green
    return
}

# --- 发布到 GitHub Releases --------------------------------------------
# 需要已登录的 gh（gh auth login）；仓库由当前目录的 git remote 决定。
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw '找不到 gh 命令，请先安装 GitHub CLI 并执行 gh auth login'
}

$repo = gh repo view --json nameWithOwner -q .nameWithOwner
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$notes = Join-Path $env:TEMP "de4dotEx-notes-$version.md"
$lines = @(
    "基于提交 ``$(git rev-parse --short HEAD)`` 的本地构建产物。",
    '',
    '| 文件 | 大小 |',
    '| --- | --- |'
)
foreach ($f in Get-ChildItem $dist -File) {
    $lines += "| ``$($f.Name)`` | $([math]::Round($f.Length / 1MB, 1)) MB |"
}
$lines | Set-Content $notes -Encoding utf8

$files = (Get-ChildItem $dist -File).FullName

# 探测是否已存在同名 Release。gh 失败时会往 stderr 写内容，
# 在 ErrorActionPreference=Stop 下会被当成终止错误，这里临时放宽。
$prev = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$null = gh release view $tagName -R $repo 2>&1
$exists = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prev

if ($exists) {
    # 同一 tag 重发：覆盖附件，不重复建 Release
    gh release upload $tagName $files -R $repo --clobber
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Write-Host "已更新现有 Release：$tagName" -ForegroundColor Green
} else {
    $createArgs = @(
        $tagName,
        '--target', (git rev-parse HEAD),
        '--title', "de4dotEx $tagName",
        '--notes-file', $notes,
        '-R', $repo
    )
    if ($Draft) {
        $createArgs += '--draft'
    } elseif ($tagName -match '^v?\d+\.\d+\.\d+-[A-Za-z]') {
        # v3.11.0-beta1 这类才是预发布；v3.11.0-1020 这种日期快照仍算正式版
        $createArgs += '--prerelease'
    } else {
        $createArgs += '--latest'
    }
    gh release create @createArgs $files
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Write-Host "已创建 Release：https://github.com/$repo/releases/tag/$tagName" -ForegroundColor Green
}
