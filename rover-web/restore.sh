#!/bin/bash
# Master Restoration Script for Rover Pi
# This script restores all software components and configurations.

set -e

# Colors for output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }

# 1. System Updates & Dependencies
log_info "Updating system and installing dependencies..."
sudo apt update && sudo apt upgrade -y
sudo apt install -y curl wget git rsync jq iw wpa_cli

# 2. Install .NET 10 Runtime
log_info "Installing .NET 10 Runtime..."
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0 --runtime aspnetcore
# Add dotnet to PATH for the current session
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$PATH:$HOME/.dotnet

# 3. Configure Hardware & Permissions
log_info "Configuring hardware permissions..."
sudo usermod -a -G dialout $USER
# Note: You may need to manually enable Serial/GPIO via raspi-config if not already set in the OS image

# 4. MediaMTX Setup
log_info "Installing MediaMTX..."
# Download and install MediaMTX (adjust version as needed)
VERSION="v1.9.0"
wget https://github.com/bluenviron/mediamtx/releases/download/${VERSION}/mediamtx_${VERSION}_linux_arm64v8.tar.gz
mkdir -p mediamtx && tar -xzf mediamtx_${VERSION}_linux_arm64v8.tar.gz -C mediamtx
sudo mv mediamtx/mediamtx /usr/local/bin/
sudo mv mediamtx/mediamtx.yml /etc/mediamtx.yml

log_info "Configuring MediaMTX TURN servers..."
chmod +x configure-mediamtx-turn.sh
sudo ./configure-mediamtx-turn.sh /etc/mediamtx.yml

# 5. Deploy Rover Application
log_info "Please run './deploy-to-rpi.ps1' from your workstation to complete the setup."

log_info "Restoration steps complete! Please reboot your Pi."