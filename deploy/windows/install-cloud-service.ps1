#Requires -Version 7.0
<#
.SYNOPSIS
    将 Coder Cloud 注册为 Windows 服务。
.DESCRIPTION
    使用 NSSM 启动 coder.exe serve；服务账户默认为低权限内置账户 LocalService。
    配置文件和二进制路径在调用时显式传入，脚本不会写入任何默认密钥。
#>
[CmdletBinding()]
param(
    [string]$NssmPath = 'C:\ProgramData\chocolatey\bin\nssm.exe',
    [string]$InstallDir = 'C:\Program Files\Coder',
    [string]$DataDir = 'C:\ProgramData\Coder',
    [string]$ConfigPath = 'C:\ProgramData\Coder\cloud.toml',
    [string]$ServiceName = 'CoderCloud',
    [string]$ServiceAccount = 'NT AUTHORITY\LocalService'
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份运行此脚本。'
}
if (-not (Test-Path -LiteralPath $NssmPath -PathType Leaf)) {
    throw "找不到 NSSM：$NssmPath。安装 NSSM 后重试，或通过 -NssmPath 指定实际路径。"
}

$coderExe = Join-Path $InstallDir 'coder.exe'
if (-not (Test-Path -LiteralPath $coderExe -PathType Leaf)) {
    throw "找不到 $coderExe。请先构建并复制 coder.exe 到 $InstallDir。"
}
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "找不到配置文件 $ConfigPath。请先创建仅管理员可读的 cloud.toml。"
}

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
& icacls.exe $DataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' "$ServiceAccount:(OI)(CI)M" | Out-Null
& icacls.exe $InstallDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)RX' 'Administrators:(OI)(CI)F' 'Users:(OI)(CI)RX' | Out-Null

$existing = & $NssmPath status $ServiceName 2>$null
if ($LASTEXITCODE -eq 0) {
    throw "服务 $ServiceName 已存在。请先运行: nssm remove $ServiceName confirm"
}

& $NssmPath install $ServiceName $coderExe serve --config $ConfigPath
if ($LASTEXITCODE -ne 0) { throw 'NSSM 安装服务失败。' }

$stdoutLog = Join-Path $DataDir 'cloud.stdout.log'
$stderrLog = Join-Path $DataDir 'cloud.stderr.log'
foreach ($logPath in @($stdoutLog, $stderrLog)) {
    if (-not (Test-Path -LiteralPath $logPath)) {
        New-Item -ItemType File -Path $logPath | Out-Null
    }
}
& icacls.exe $stdoutLog /grant:r "$ServiceAccount:M" | Out-Null
& icacls.exe $stderrLog /grant:r "$ServiceAccount:M" | Out-Null
& $NssmPath set $ServiceName AppDirectory $InstallDir
& $NssmPath set $ServiceName ObjectName $ServiceAccount
& $NssmPath set $ServiceName Start SERVICE_AUTO_START
& $NssmPath set $ServiceName AppStdout $stdoutLog
& $NssmPath set $ServiceName AppStderr $stderrLog
& $NssmPath set $ServiceName AppRotateFiles 1
& $NssmPath set $ServiceName AppRotateOnline 1
& $NssmPath set $ServiceName AppRotateBytes 10485760
& $NssmPath set $ServiceName AppStopMethodConsole 30000
& $NssmPath set $ServiceName AppStopMethodWindow 5000
& $NssmPath set $ServiceName AppStopMethodThreads 5000
& $NssmPath set $ServiceName AppExit Default Restart
& $NssmPath set $ServiceName AppRestartDelay 5000

& $NssmPath start $ServiceName
if ($LASTEXITCODE -ne 0) { throw '服务启动失败，请检查 cloud.stderr.log。' }

Write-Host "服务 $ServiceName 已启动，账户：$ServiceAccount"
Write-Host "健康检查：Invoke-RestMethod http://127.0.0.1:8080/api/v1/health"
