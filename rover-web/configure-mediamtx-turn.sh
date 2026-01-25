#!/bin/bash
# Configure mediamtx with TURN servers for NAT traversal
# Run this on the Raspberry Pi

MEDIAMTX_CONFIG="/etc/mediamtx/mediamtx.yml"

# Check if config exists in common locations
if [ ! -f "$MEDIAMTX_CONFIG" ]; then
  MEDIAMTX_CONFIG="/usr/local/etc/mediamtx.yml"
fi
if [ ! -f "$MEDIAMTX_CONFIG" ]; then
  MEDIAMTX_CONFIG="$HOME/mediamtx.yml"
fi
if [ ! -f "$MEDIAMTX_CONFIG" ]; then
  echo "ERROR: Cannot find mediamtx.yml"
  echo "Please specify the path: $0 /path/to/mediamtx.yml"
  exit 1
fi

# Allow override
if [ -n "$1" ]; then
  MEDIAMTX_CONFIG="$1"
fi

echo "Configuring mediamtx at: $MEDIAMTX_CONFIG"

# Backup original
cp "$MEDIAMTX_CONFIG" "${MEDIAMTX_CONFIG}.backup.$(date +%Y%m%d%H%M%S)"

# Check if webrtcICEServers2 already has TURN
if grep -q "turn:global.relay.metered.ca" "$MEDIAMTX_CONFIG"; then
  echo "TURN servers already configured"
  exit 0
fi

# Add TURN configuration after webrtcICEServers2 line
# Using metered.ca free Open Relay servers
cat >> "$MEDIAMTX_CONFIG" << 'EOF'

# TURN servers for NAT traversal (added by configure-mediamtx-turn.sh)
webrtcICEServers2:
  - url: stun:stun.l.google.com:19302
  - url: turn:global.relay.metered.ca:80
    username: e8dd65b92f7b2ae09c6ce5f5
    password: uJHoAaKKzxdjt7GW
  - url: turn:global.relay.metered.ca:80?transport=tcp
    username: e8dd65b92f7b2ae09c6ce5f5
    password: uJHoAaKKzxdjt7GW
  - url: turn:global.relay.metered.ca:443
    username: e8dd65b92f7b2ae09c6ce5f5
    password: uJHoAaKKzxdjt7GW
  - url: turns:global.relay.metered.ca:443?transport=tcp
    username: e8dd65b92f7b2ae09c6ce5f5
    password: uJHoAaKKzxdjt7GW
EOF

echo "TURN servers added to mediamtx config"
echo ""
echo "Restart mediamtx to apply changes:"
echo "  sudo systemctl restart mediamtx"
echo "  # or if running manually:"
echo "  # kill mediamtx and restart it"
