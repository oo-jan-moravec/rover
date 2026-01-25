#!/bin/bash
#
# Installer for Wi-Fi Roaming Watchdog
# Run this on the Raspberry Pi with sudo
#

set -e

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

# Check root
if [ "$EUID" -ne 0 ]; then
    log_error "This script must be run as root (use sudo)"
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WATCHDOG_SCRIPT="$SCRIPT_DIR/wifi-roam-watchdog.sh"

if [ ! -f "$WATCHDOG_SCRIPT" ]; then
    log_error "Cannot find wifi-roam-watchdog.sh in $SCRIPT_DIR"
    exit 1
fi

# Install the watchdog script
log_info "Installing watchdog script..."
cp "$WATCHDOG_SCRIPT" /usr/local/bin/wifi-roam-watchdog
chmod +x /usr/local/bin/wifi-roam-watchdog

# Create systemd service
log_info "Creating systemd service..."
cat > /etc/systemd/system/wifi-roam-watchdog.service << 'EOF'
[Unit]
Description=Wi-Fi Roaming Watchdog
Documentation=https://github.com/your/rover
After=network-online.target wpa_supplicant.service
Wants=network-online.target

[Service]
Type=simple
ExecStart=/usr/local/bin/wifi-roam-watchdog
Restart=always
RestartSec=5

# Environment variables for tuning (override in /etc/default/wifi-roam-watchdog)
EnvironmentFile=-/etc/default/wifi-roam-watchdog

# Logging
StandardOutput=journal
StandardError=journal
SyslogIdentifier=wifi-roam-watchdog

[Install]
WantedBy=multi-user.target
EOF

# Create default config file
log_info "Creating default configuration..."
cat > /etc/default/wifi-roam-watchdog << 'EOF'
# Wi-Fi Roaming Watchdog Configuration
# Uncomment and modify values to override defaults

# Network interface
#WIFI_IFACE=wlan0

# Start scanning for better APs when RSSI drops below this (dBm)
#RSSI_SCAN_THRESHOLD=-65

# Force roam when RSSI drops below this (dBm)
#RSSI_FORCE_ROAM=-70

# New AP must be this many dB better to trigger roam
#RSSI_HYSTERESIS=8

# Minimum seconds between roams (anti-flap)
#MIN_ROAM_INTERVAL=15

# How often to check current RSSI (seconds)
#CHECK_INTERVAL=2

# How often to scan when in degraded state (seconds)
#SCAN_INTERVAL=5

# Set to 1 for verbose logging
#DEBUG=0
EOF

# Reload systemd
log_info "Reloading systemd..."
systemctl daemon-reload

# Enable and restart the service (restart to ensure new script is loaded)
log_info "Enabling and starting service..."
systemctl enable wifi-roam-watchdog
systemctl restart wifi-roam-watchdog

# Check status
sleep 2
if systemctl is-active --quiet wifi-roam-watchdog; then
    log_info "Watchdog is running!"
else
    log_warn "Watchdog may not have started correctly"
    systemctl status wifi-roam-watchdog --no-pager || true
fi

echo ""
log_info "=== Installation Complete ==="
echo ""
echo "The watchdog is now monitoring Wi-Fi and will force roaming when signal degrades."
echo ""
echo "Useful commands:"
echo "  View logs:       journalctl -u wifi-roam-watchdog -f"
echo "  Check status:    systemctl status wifi-roam-watchdog"
echo "  Restart:         sudo systemctl restart wifi-roam-watchdog"
echo "  Stop:            sudo systemctl stop wifi-roam-watchdog"
echo "  Edit config:     sudo nano /etc/default/wifi-roam-watchdog"
echo ""
echo "Default thresholds:"
echo "  - Scan when RSSI <= -65 dBm"
echo "  - Force roam when RSSI <= -70 dBm"
echo "  - Require 8 dB improvement to roam"
echo "  - Minimum 15s between roams"
