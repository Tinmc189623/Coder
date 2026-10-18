#Requires -Version 7.0
<#
.SYNOPSIS
    为当前登录用户注册 Coder 本地 Agent 的计划任务。
.DESCRIPTION
    Agent 需要访问当前用户的 DPAPI 凭据与工作区，因此任务以当前用户身份、
    “只在用户登录时运行”的方式启动。任务失败后每分钟重启。
#>
[CmdletBinding()]
param(
    [string]$CoderPath = "$env:LOCALAPPDATA\Coder\bin\coder.exe",
    [string]$ConfigPath = "$env:APPDATA\Coder\agent.toml",
    [string]$TaskName = 'CoderAgent'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $CoderPath -PathType Leaf)) {
    throw "找不到 $CoderPath。请先构建并把 coder.exe 复制到该路径，或通过 -CoderPath 指定。"
}
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "找不到 $ConfigPath。请先运行 coder config init 并配置 server_url 与工作区。"
}

$userId = "$env:USERDOMAIN\$env:USERNAME"
$action = New-ScheduledTaskAction -Execute $CoderPath `
    -Argument "agent run --config `"$ConfigPath`"" `
    -WorkingDirectory (Split-Path -Parent $ConfigPath)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
$principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description 'Coder local agent: accepts authorized tasks from Coder Cloud' `
    -Force | Out-Null

Start-ScheduledTask -TaskName $TaskName
Write-Host "计划任务 $TaskName 已注册并启动。日志由 Agent 写入平台数据目录下的 agent.db 与标准错误。"
