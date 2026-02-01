#!/usr/bin/env pwsh

# Rover Web Deployment Script for Raspberry Pi
# Requires: PowerShell Core (pwsh), .NET SDK, SSH access to RPi

param(
    [string]$RpiUser = $env:RPI_USER ?? "hanzzo",
    [string]$RpiHost = $env:RPI_HOST ?? "rpi-rover-brain2.local",
    [string]$RpiDeployDir = $env:RPI_DEPLOY_DIR ?? "/home/hanzzo/rover-web",
    [string]$RpiArch = $env:RPI_ARCH ?? "linux-arm64"  # Use linux-arm for 32-bit, linux-arm64 for 64-bit
)

$ErrorActionPreference = "Stop"

# Colors
function Write-ColorOutput {
    param([string]$Message, [string]$Color = "White")
    Write-Host $Message -ForegroundColor $Color
}

Write-ColorOutput "========================================" "Green"
Write-ColorOutput "  Rover Web Deployment Script" "Green"
Write-ColorOutput "========================================" "Green"
Write-Host ""
Write-ColorOutput "Configuration:" "Cyan"
Write-Host "  User:        $RpiUser"
Write-Host "  Host:        $RpiHost"
Write-Host "  Deploy Dir:  $RpiDeployDir"
Write-Host "  Arch:        $RpiArch"
Write-Host ""

# Check SSH connectivity
Write-ColorOutput "Checking SSH connection to ${RpiUser}@${RpiHost}..." "Yellow"
$sshTest = ssh -o ConnectTimeout=5 "${RpiUser}@${RpiHost}" "echo 'SSH connection successful'" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-ColorOutput "Error: Cannot connect to Raspberry Pi via SSH" "Red"
    Write-Host "Please check:"
    Write-Host "  - RPI_HOST is correct (currently: $RpiHost)"
    Write-Host "  - RPI_USER is correct (currently: $RpiUser)"
    Write-Host "  - SSH is enabled on the Raspberry Pi"
    Write-Host "  - You can connect manually: ssh ${RpiUser}@${RpiHost}"
    exit 1
}
Write-ColorOutput "✓ SSH connection successful" "Green"
Write-Host ""

# Navigate to project directory
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = Join-Path $scriptPath "RoverWeb"
Set-Location $projectDir

Write-ColorOutput "Building for ${RpiArch}..." "Yellow"

# Build the application
$publishDir = Join-Path $projectDir "publish"
dotnet publish -c Release `
    -r $RpiArch `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $publishDir

if (-not (Test-Path $publishDir)) {
    Write-ColorOutput "Error: Build failed, publish directory not found" "Red"
    exit 1
}

Write-ColorOutput "✓ Build successful!" "Green"
Write-Host ""

# Create deployment directory on RPi
Write-ColorOutput "Creating deployment directory on Raspberry Pi..." "Yellow"
ssh "${RpiUser}@${RpiHost}" "mkdir -p $RpiDeployDir"

# Stop existing service if running
Write-ColorOutput "Stopping existing rover service (if running)..." "Yellow"
ssh "${RpiUser}@${RpiHost}" "sudo systemctl stop rover-web.service 2>/dev/null || true"

# Upload the build to RPi
Write-ColorOutput "Uploading files to Raspberry Pi..." "Yellow"
rsync -avz --delete `
    --progress `
    "$publishDir/" `
    "${RpiUser}@${RpiHost}:${RpiDeployDir}/"

if ($LASTEXITCODE -ne 0) {
    Write-ColorOutput "Error: Failed to upload files" "Red"
    exit 1
}

Write-ColorOutput "✓ Files uploaded successfully" "Green"
Write-Host ""

# Make the executable file executable
Write-ColorOutput "Setting permissions..." "Yellow"
ssh "${RpiUser}@${RpiHost}" "chmod +x ${RpiDeployDir}/RoverWeb"

# Create systemd service file
Write-ColorOutput "Creating systemd service..." "Yellow"
$serviceContent = @"
[Unit]
Description=Rover Web Application
After=network.target

[Service]
Type=simple
WorkingDirectory=$RpiDeployDir
ExecStart=$RpiDeployDir/RoverWeb
Restart=always
RestartSec=10
User=$RpiUser
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

# Ensure serial port access
SupplementaryGroups=dialout

[Install]
WantedBy=multi-user.target
"@

# Write service content to temp file and upload
$tempServiceFile = [System.IO.Path]::GetTempFileName()
Set-Content -Path $tempServiceFile -Value $serviceContent
scp $tempServiceFile "${RpiUser}@${RpiHost}:/tmp/rover-web.service"
Remove-Item $tempServiceFile

# Move service file to systemd directory
Write-ColorOutput "Installing systemd service..." "Yellow"
ssh "${RpiUser}@${RpiHost}" "sudo mv /tmp/rover-web.service /etc/systemd/system/rover-web.service"
ssh "${RpiUser}@${RpiHost}" "sudo systemctl daemon-reload"

# Enable and start the service
Write-ColorOutput "Starting rover service..." "Yellow"
ssh "${RpiUser}@${RpiHost}" "sudo systemctl enable rover-web.service"
ssh "${RpiUser}@${RpiHost}" "sudo systemctl start rover-web.service"

# Wait a moment for service to start
Start-Sleep -Seconds 2

# Check service status
Write-Host ""
Write-ColorOutput "========================================" "Green"
Write-ColorOutput "  Deployment Complete!" "Green"
Write-ColorOutput "========================================" "Green"
Write-Host ""

Write-ColorOutput "Rover Web Service Status:" "Cyan"
ssh "${RpiUser}@${RpiHost}" "sudo systemctl status rover-web.service --no-pager" 2>&1 | Out-Host

Write-Host ""
Write-ColorOutput "Useful commands:" "Green"
Write-Host ""
Write-ColorOutput "  Rover Web Service:" "Cyan"
Write-Host "    Check status:  ssh ${RpiUser}@${RpiHost} 'sudo systemctl status rover-web.service'"
Write-Host "    View logs:     ssh ${RpiUser}@${RpiHost} 'sudo journalctl -u rover-web.service -f'"
Write-Host "    Restart:       ssh ${RpiUser}@${RpiHost} 'sudo systemctl restart rover-web.service'"
Write-Host ""
Write-ColorOutput "Access the web interface at: http://${RpiHost}:8080" "Green"
Write-Host ""

# Cleanup local publish directory
Write-ColorOutput "Cleaning up..." "Yellow"
Remove-Item -Recurse -Force $publishDir

Write-ColorOutput "Done!" "Green"

