#!/usr/bin/env pwsh

param(
    [string]$RpiUser = $env:RPI_USER ?? "hanzzo",
    [string]$RpiHost = $env:RPI_HOST ?? "rpi-rover-brain2.local",
    [string]$RpiDeployDir = $env:RPI_DEPLOY_DIR ?? "/home/hanzzo/rover-web",
    [int]$RpiPort = $env:RPI_PORT ?? 8080
)

$ErrorActionPreference = "Stop"
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$envPath = Join-Path $scriptPath ".env"
$clientVersion = "1.5.0"
$issues = New-Object System.Collections.Generic.List[string]

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host ("==== {0} ====" -f $Title) -ForegroundColor Cyan
}

function Write-KV {
    param([string]$Label, [string]$Value)
    Write-Host ("  {0,-18}: {1}" -f $Label, $Value)
}

function Add-Issue {
    param([string]$Message)
    $issues.Add($Message)
    Write-Host ("  ⚠ {0}" -f $Message) -ForegroundColor Red
}

function Invoke-RemoteCommand {
    param(
        [string]$Command,
        [switch]$ReturnLines,
        [int]$ConnectTimeoutSec = 10
    )

    $escaped = $Command -replace "'", "'\''"
    $remote = "bash -lc '$escaped'"
    $output = & ssh -o "ConnectTimeout=$ConnectTimeoutSec" "$RpiUser@$RpiHost" $remote 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "SSH command failed ($exitCode): $($output -join "`n")"
    }

    if ($ReturnLines) { return , $output }
    else { return ($output -join "`n").Trim() }
}

function Try-RemoteCommand {
    param(
        [string]$Command,
        [switch]$ReturnLines
    )
    try {
        Invoke-RemoteCommand -Command $Command -ReturnLines:$ReturnLines
    }
    catch {
        Add-Issue($_.Exception.Message)
        return $null
    }
}

function Get-DotEnvValues {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return @{}
    }

    $values = @{}
    Get-Content $Path | ForEach-Object {
        $line = $_.Trim()
        if ($line -and -not $line.StartsWith("#") -and $line -match '^\s*([^#=]+)=(.*)$') {
            $val = $matches[2].Trim()
            if ($val.Length -ge 2 -and $val.StartsWith('"') -and $val.EndsWith('"')) {
                $val = $val.Substring(1, $val.Length - 2)
            }
            $values[$matches[1].Trim()] = $val
        }
    }
    return $values
}

function Receive-WebSocketText {
    param(
        [System.Net.WebSockets.ClientWebSocket]$WebSocket,
        [int]$TimeoutMs = 2000
    )

    $buffer = New-Object byte[] 4096
    $memory = New-Object System.IO.MemoryStream

    while ($true) {
        $segment = [System.ArraySegment[byte]]::new($buffer)
        $cts = New-Object System.Threading.CancellationTokenSource
        $cts.CancelAfter($TimeoutMs)

        try {
            $task = $WebSocket.ReceiveAsync($segment, $cts.Token)
            $task.Wait()
            $result = $task.Result
        }
        catch [System.AggregateException] {
            if ($_.Exception.InnerException -is [System.OperationCanceledException]) {
                $memory.Dispose()
                return $null
            }
            throw
        }
        catch [System.OperationCanceledException] {
            $memory.Dispose()
            return $null
        }

        if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
            $memory.Dispose()
            return $null
        }

        $memory.Write($segment.Array, $segment.Offset, $result.Count)
        if ($result.EndOfMessage) { break }
    }

    $text = [System.Text.Encoding]::UTF8.GetString($memory.ToArray())
    $memory.Dispose()
    return $text
}

function Get-WebSocketSnapshot {
    param(
        [string]$TargetHost,
        [int]$Port,
        [string]$Password
    )

    if (-not $Password) {
        Add-Issue("ROVER_PASSWORD missing; cannot query telemetry via WebSocket.")
        return $null
    }

    $uri = [System.Uri]::new("ws://${TargetHost}:$Port/ws")
    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $ws.Options.SetRequestHeader("Cookie", "RoverAuth=$Password")
    $ws.Options.SetRequestHeader("Origin", "http://${TargetHost}:$Port")
    $ws.Options.KeepAliveInterval = [TimeSpan]::FromSeconds(5)

    try {
        $ws.ConnectAsync($uri, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
    }
    catch {
        $errorMsg = $_.Exception.Message
        if ($errorMsg -match "401") {
            Add-Issue("WebSocket authentication failed (401). Check that ROVER_PASSWORD in .env matches the server configuration.")
        }
        else {
            Add-Issue("Unable to open WebSocket: $errorMsg")
        }
        return $null
    }

    $snapshot = [pscustomobject]@{
        ClientName   = $null
        Role         = $null
        RoleDetail   = $null
        Telemetry    = $null
        RawTelemetry = $null
    }

    try {
        $payload = [System.Text.Encoding]::UTF8.GetBytes("VERSION:$clientVersion")
        $segment = [System.ArraySegment[byte]]::new($payload)
        $ws.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()

        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        while ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open -and $stopwatch.Elapsed.TotalSeconds -lt 6) {
            $text = Receive-WebSocketText -WebSocket $ws -TimeoutMs 2000
            if (-not $text) { continue }

            if ($text.StartsWith("NAME:")) {
                $snapshot.ClientName = $text.Substring(5)
            }
            elseif ($text.StartsWith("ROLE:")) {
                $parts = $text.Substring(5).Split("|", 2, [System.StringSplitOptions]::None)
                $snapshot.Role = $parts[0]
                if ($parts.Count -gt 1) {
                    $snapshot.RoleDetail = $parts[1]
                }
            }
            elseif ($text.StartsWith("TELEM:")) {
                $snapshot.RawTelemetry = $text.Substring(6)
                try {
                    $snapshot.Telemetry = $snapshot.RawTelemetry | ConvertFrom-Json
                }
                catch {
                    Add-Issue("Unable to parse telemetry JSON: $($_.Exception.Message)")
                }
                break
            }
        }
    }
    finally {
        if ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
            $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "diag", [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
        }
        $ws.Dispose()
    }

    return $snapshot
}

function Parse-SsConnections {
    param([string[]]$Lines)
    $connections = @()
    foreach ($line in $Lines) {
        if ($line -match '^(?<state>[A-Z]+)\s+\S+\s+\S+\s+(?<local>\S+)\s+(?<remote>\S+)\s*(?<rest>.*)$') {
            $connections += [pscustomobject]@{
                State   = $Matches.state
                Local   = $Matches.local
                Remote  = $Matches.remote
                Details = $Matches.rest.Trim()
            }
        }
    }
    return $connections
}

function Split-Endpoint {
    param([string]$Endpoint)
    if ([string]::IsNullOrWhiteSpace($Endpoint)) {
        return @("", "")
    }
    if ($Endpoint.StartsWith("[")) {
        $idx = $Endpoint.LastIndexOf("]:")
        if ($idx -gt 0) {
            return @($Endpoint.Substring(1, $idx - 1), $Endpoint.Substring($idx + 2))
        }
    }
    $lastColon = $Endpoint.LastIndexOf(":")
    if ($lastColon -gt -1) {
        return @($Endpoint.Substring(0, $lastColon), $Endpoint.Substring($lastColon + 1))
    }
    return @($Endpoint, "")
}

function Format-Duration {
    param([int]$Seconds)
    if ($Seconds -lt 60) { return "$Seconds s" }
    if ($Seconds -lt 3600) {
        $m = [math]::Floor($Seconds / 60)
        $s = $Seconds % 60
        return "$m m $s s"
    }
    $h = [math]::Floor($Seconds / 3600)
    $mLeft = [math]::Floor(($Seconds % 3600) / 60)
    return "$h h $mLeft m"
}

$envVars = Get-DotEnvValues -Path $envPath
$roverPassword = $envVars["ROVER_PASSWORD"]
$sq = [char]39  # Single quote character for use in remote commands

Write-Host "========================================" -ForegroundColor Green
Write-Host "   Rover Web Remote Diagnostics" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Target: $RpiUser@${RpiHost}:$RpiPort"
Write-Host "  Deploy Dir: $RpiDeployDir"
Write-Host ""

Get-Command ssh -ErrorAction Stop | Out-Null

Write-Section "SSH Connectivity"
try {
    $sshEcho = Invoke-RemoteCommand "echo 'ok'"
    Write-Host "  ✓ SSH reachable"
}
catch {
    Add-Issue("SSH connection failed: $($_.Exception.Message)")
    Write-Host ""
    Write-Host "Unable to proceed without SSH access."
    exit 1
}

Write-Section "System Overview"
$hostname = Try-RemoteCommand "hostname"
$osName = Try-RemoteCommand "grep PRETTY_NAME /etc/os-release | cut -d= -f2- | tr -d ${sq}`"${sq}"
$kernel = Try-RemoteCommand "uname -srmo"
$uptime = Try-RemoteCommand "uptime -p"
$loadAvg = Try-RemoteCommand "uptime | awk -F${sq}load average:${sq} ${sq}{print `$2}${sq}"
$ips = Try-RemoteCommand "hostname -I"

if ($hostname) { Write-KV "Hostname" $hostname }
if ($osName) { Write-KV "OS" $osName }
if ($kernel) { Write-KV "Kernel" $kernel }
if ($uptime) { Write-KV "Uptime" $uptime }
if ($loadAvg) { Write-KV "Load avg" ($loadAvg.Trim()) }
if ($ips) { Write-KV "IP(s)" $ips }

Write-Section "Rover Web Service"
$serviceProps = @{}
$serviceActive = $false
$serviceLines = Try-RemoteCommand "systemctl show rover-web.service --property=ActiveState,SubState,MainPID,ExecMainStartTimestamp,UnitFileState --no-page" -ReturnLines
if ($serviceLines) {
    foreach ($line in $serviceLines) {
        if ($line -match '^(?<key>[^=]+)=(?<value>.*)$') {
            $serviceProps[$Matches.key] = $Matches.value
        }
    }
    $state = $serviceProps["ActiveState"]
    $sub = $serviceProps["SubState"]
    $servicePid = $serviceProps["MainPID"]
    $serviceActive = ($state -eq "active" -and $sub -eq "running")

    Write-KV "Active state" ("$state / $sub")
    Write-KV "Main PID" ($servicePid ?? "n/a")
    if ($serviceProps["ExecMainStartTimestamp"]) {
        Write-KV "Started" $serviceProps["ExecMainStartTimestamp"]
    }
    if ($state -ne "active") {
        Add-Issue("rover-web.service is not active (state: $state).")
    }
}
else {
    Add-Issue("Unable to fetch rover-web.service status.")
}

$deployBinaryInfo = Try-RemoteCommand "if [ -e ${sq}$RpiDeployDir/RoverWeb${sq} ]; then stat -c ${sq}%s|%y${sq} ${sq}$RpiDeployDir/RoverWeb${sq}; else echo ${sq}missing${sq}; fi"
if ($deployBinaryInfo -eq "missing") {
    Add-Issue("Executable not found at $RpiDeployDir/RoverWeb")
}
elseif ($deployBinaryInfo) {
    $parts = $deployBinaryInfo.Split("|", 2)
    if ($parts.Count -eq 2) {
        $sizeMb = [math]::Round([double]$parts[0] / 1MB, 2)
        Write-KV "Binary size" ("$sizeMb MB")
        Write-KV "Binary updated" $parts[1]
    }
}

if ($serviceActive -and $serviceProps["MainPID"] -and $serviceProps["MainPID"] -ne "0") {
    $servicePid = $serviceProps["MainPID"]
    $procInfo = Try-RemoteCommand "ps -p $servicePid -o pid,%cpu,%mem,etimes,cmd --no-headers"
    if ($procInfo) {
        $parts = $procInfo -split "\s+", 5, [System.StringSplitOptions]::RemoveEmptyEntries
        if ($parts.Count -ge 5) {
            Write-KV "Process CPU" ("{0}%" -f $parts[1])
            Write-KV "Process MEM" ("{0}%" -f $parts[2])
            $duration = Format-Duration([int]$parts[3])
            Write-KV "Uptime (proc)" $duration
        }
    }
}

Write-Section "Resources"
$diskInfo = Try-RemoteCommand "df -h ${sq}$RpiDeployDir${sq} | tail -n +2"
if ($diskInfo) {
    Write-Host "  Disk:"
    Write-Host "    $diskInfo"
}
$memInfo = Try-RemoteCommand "free -h | sed -n ${sq}2p${sq}"
if ($memInfo) {
    $tokens = $memInfo -split "\s+" | Where-Object { $_ -ne "" }
    if ($tokens.Count -ge 7) {
        Write-KV "Memory used" ("{0}/{1}" -f $tokens[2], $tokens[1])
    }
}
$cpuTemp = Try-RemoteCommand "if [ -f /sys/class/thermal/thermal_zone0/temp ]; then awk ${sq}{printf `"%.1f`", `$1/1000}${sq} /sys/class/thermal/thermal_zone0/temp; else echo `"n/a`"; fi"
if ($cpuTemp) {
    Write-KV "CPU temp" ("$cpuTemp °C")
}

Write-Section "Network & Pilots"
$listenCmd1 = "ss -tln | awk 'NR==1 || `$4 ~ /:8080$/'"
$listenLines = Try-RemoteCommand $listenCmd1 -ReturnLines
if ($listenLines) {
    Write-Host "  Port 8080 listening:"
    foreach ($line in $listenLines) {
        Write-Host "    $line"
    }
    if ($listenLines.Count -le 1) {
        Add-Issue("Port 8080 is not listening.")
    }
}

$connCmd = "ss -tn | awk 'NR==1 || (`$4 ~ /:8080$/ && `$1 ~ /^ESTAB$/)'"
$connLines = Try-RemoteCommand $connCmd -ReturnLines
$connections = @()
if ($connLines) {
    $connections = Parse-SsConnections -Lines $connLines
    $estab = $connections | Where-Object { $_.State -eq "ESTAB" }
    if ($estab.Count -gt 0) {
        Write-Host "  Active TCP clients:"
        foreach ($conn in $estab) {
            $remote = Split-Endpoint $conn.Remote
            Write-Host ("    {0,-12} -> {1}:{2} {3}" -f $conn.State, $remote[0], $remote[1], $conn.Details)
        }
    }
    else {
        Write-Host "  No established TCP clients on port 8080."
    }
}

Write-Section "HTTP Endpoint"
try {
    $loginUri = "http://$RpiHost`:$RpiPort/login.html"
    $response = Invoke-WebRequest -Uri $loginUri -Method Head -TimeoutSec 5
    Write-Host ("  ✓ HTTP reachable (Status {0})" -f [int]$response.StatusCode)
}
catch {
    Add-Issue("HTTP check failed: $($_.Exception.Message)")
}

Write-Section "Telemetry Snapshot"
if (-not $roverPassword) {
    Add-Issue("ROVER_PASSWORD not found in .env file. WebSocket telemetry requires authentication.")
    Write-Host "  Telemetry not available (no password configured)."
}
else {
    $telemetrySnapshot = Get-WebSocketSnapshot -TargetHost $RpiHost -Port $RpiPort -Password $roverPassword
    if ($telemetrySnapshot -and $telemetrySnapshot.Telemetry) {
        if ($telemetrySnapshot.ClientName) {
            Write-KV "Volunteer name" $telemetrySnapshot.ClientName
        }
        if ($telemetrySnapshot.Role) {
            $roleDetail = if ($telemetrySnapshot.RoleDetail) { " ($($telemetrySnapshot.RoleDetail))" } else { "" }
            Write-KV "Role" ("$($telemetrySnapshot.Role)$roleDetail")
            if ($telemetrySnapshot.Role -eq "spectator" -and $telemetrySnapshot.RoleDetail -and $telemetrySnapshot.RoleDetail -ne "none") {
                Write-Host ("  Active operator: {0}" -f $telemetrySnapshot.RoleDetail)
            }
        }

        $wifi = $telemetrySnapshot.Telemetry.wifi
        if ($wifi) {
            Write-Host "  Wi-Fi:"
            Write-Host ("    SSID       : {0}" -f $wifi.ssid)
            Write-Host ("    BSSID      : {0}" -f $wifi.bssid)
            Write-Host ("    RSSI       : {0} dBm ({1}% signal)" -f $wifi.rssiDbm, $wifi.signalPercent)
            Write-Host ("    Bitrate    : TX {0} Mbps / RX {1} Mbps" -f $wifi.txBitrateMbps, $wifi.rxBitrateMbps)
            if ($wifi.betterApAvailable) {
                Write-Host ("    Better AP  : {0} (+{1} dBm)" -f $wifi.betterApAvailable.bssid, $wifi.betterApAvailable.improvement)
            }
        }
        $system = $telemetrySnapshot.Telemetry.system
        if ($system) {
            Write-Host ("  CPU Temp  : {0} °C" -f $system.cpuTempC)
            Write-Host ("  Ping (ms) : {0}" -f $system.pingMs)
        }
        $motors = $telemetrySnapshot.Telemetry.motors
        if ($motors) {
            Write-Host ("  Motors    : {0}" -f ($(if ($motors.inhibited) { "INHIBITED ($($motors.lastStopReason))" } else { "Ready" })))
        }
    }
    else {
        Write-Host "  Telemetry not available."
    }
}

Write-Section "Recent Logs"
$logLines = Try-RemoteCommand "journalctl -u rover-web.service -n 20 --no-pager"
if ($logLines) {
    foreach ($line in $logLines) {
        Write-Host "  $line"
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
if ($issues.Count -eq 0) {
    Write-Host "Rover looks healthy. No blocking issues detected." -ForegroundColor Green
    exit 0
}
else {
    Write-Host "Diagnostics completed with $($issues.Count) issue(s):" -ForegroundColor Yellow
    foreach ($issue in $issues) {
        Write-Host (" - {0}" -f $issue)
    }
    exit 1
}
