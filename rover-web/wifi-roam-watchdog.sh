#!/bin/bash
#
# Wi-Fi Roaming Watchdog for Raspberry Pi
#
# Works around the brcmfmac driver's poor roaming behavior by actively
# monitoring RSSI and forcing roams to better APs when signal degrades.
#
# This script should be run as a systemd service on the Pi.

set -u

# === Configuration ===
IFACE="${WIFI_IFACE:-wlan0}"
RSSI_SCAN_THRESHOLD="${RSSI_SCAN_THRESHOLD:--65}"      # Start scanning when below this
RSSI_FORCE_ROAM_THRESHOLD="${RSSI_FORCE_ROAM:--70}"    # Force roam when below this
RSSI_HYSTERESIS="${RSSI_HYSTERESIS:-8}"                # New AP must be this much better (dB)
MIN_ROAM_INTERVAL="${MIN_ROAM_INTERVAL:-15}"           # Minimum seconds between roams
CHECK_INTERVAL="${CHECK_INTERVAL:-2}"                  # How often to check RSSI (seconds)
SCAN_INTERVAL="${SCAN_INTERVAL:-5}"                    # How often to scan when degraded (seconds)

# === State ===
LAST_ROAM_TIME=0
LAST_SCAN_TIME=0
CURRENT_BSSID=""
CURRENT_SSID=""
CURRENT_RSSI=-100

# === Logging ===
log() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] $1"
    logger -t wifi-roam-watchdog "$1"
}

log_debug() {
    if [ "${DEBUG:-0}" = "1" ]; then
        echo "[$(date '+%Y-%m-%d %H:%M:%S')] [DEBUG] $1"
    fi
}

# === Functions ===

# Update connection info - sets global variables directly (no subshell!)
update_connection_info() {
    local link_info
    link_info=$(/usr/sbin/iw dev "$IFACE" link 2>&1)
    
    if [ -z "$link_info" ]; then
        CURRENT_BSSID=""
        CURRENT_SSID=""
        CURRENT_RSSI=-100
        return
    fi
    
    if echo "$link_info" | grep -qi "Not connected"; then
        CURRENT_BSSID=""
        CURRENT_SSID=""
        CURRENT_RSSI=-100
        return
    fi
    
    # Parse BSSID - format: "Connected to aa:bb:cc:dd:ee:ff (on wlan0)"
    CURRENT_BSSID=$(echo "$link_info" | grep -i "Connected to" | sed -E 's/.*[Cc]onnected to ([0-9a-fA-F:]+).*/\1/' | tr '[:lower:]' '[:upper:]')
    
    # Parse SSID - format: "SSID: networkname" (may have leading whitespace)
    CURRENT_SSID=$(echo "$link_info" | grep "SSID:" | sed -E 's/.*SSID:[[:space:]]*(.*)/\1/' | head -1)
    
    # Parse signal - format: "signal: -70 dBm"
    CURRENT_RSSI=$(echo "$link_info" | grep "signal:" | sed -E 's/.*signal:[[:space:]]*(-?[0-9]+).*/\1/')
    CURRENT_RSSI="${CURRENT_RSSI:--100}"
}

scan_for_aps() {
    local now
    now=$(date +%s)
    
    # Rate limit scanning
    if (( now - LAST_SCAN_TIME < SCAN_INTERVAL )); then
        log_debug "Skipping scan, too soon ($(( now - LAST_SCAN_TIME ))s < ${SCAN_INTERVAL}s)"
        return 1
    fi
    
    LAST_SCAN_TIME=$now
    log_debug "Triggering scan..."
    
    # Trigger scan via wpa_cli (faster than iw scan)
    wpa_cli -i "$IFACE" scan >/dev/null 2>&1
    sleep 1
    
    return 0
}

find_better_ap() {
    local current_rssi="$1"
    local min_rssi_needed=$(( current_rssi + RSSI_HYSTERESIS ))
    
    log "Looking for AP with RSSI > $min_rssi_needed (current: $current_rssi, hysteresis: $RSSI_HYSTERESIS)"
    
    # Get scan results from wpa_cli
    local scan_results
    scan_results=$(wpa_cli -i "$IFACE" scan_results 2>/dev/null)
    
    if [ -z "$scan_results" ]; then
        log "No scan results available"
        return 1
    fi
    
    local best_bssid=""
    local best_rssi=-100
    local candidate_count=0
    
    # Parse scan results: bssid / frequency / signal level / flags / ssid
    # Note: fields are separated by tabs, but ssid may be empty for mesh nodes
    while IFS=$'\t' read -r bssid freq rssi flags ssid; do
        # Skip header
        [[ "$bssid" == "bssid"* ]] && continue
        [[ -z "$bssid" ]] && continue
        
        # Skip current AP
        if [ "${bssid^^}" = "${CURRENT_BSSID^^}" ]; then
            log_debug "  Skipping current AP: $bssid"
            continue
        fi
        
        # Accept AP if:
        # 1. SSID matches our current SSID, OR
        # 2. SSID is empty (common for mesh nodes with same SSID), OR  
        # 3. SSID contains our SSID (partial match for hidden networks)
        local ssid_match=false
        if [ "$ssid" = "$CURRENT_SSID" ]; then
            ssid_match=true
        elif [ -z "$ssid" ]; then
            # Empty SSID - likely a mesh node, accept it
            ssid_match=true
            log_debug "  Accepting empty SSID AP as potential mesh node: $bssid"
        fi
        
        if [ "$ssid_match" = false ]; then
            log_debug "  Skipping different SSID: $bssid ($ssid != $CURRENT_SSID)"
            continue
        fi
        
        candidate_count=$((candidate_count + 1))
        log "  Candidate $candidate_count: $bssid RSSI=$rssi SSID='${ssid:-<empty>}'"
        
        # Check if this AP is better
        if (( rssi > best_rssi )); then
            best_rssi=$rssi
            best_bssid=$bssid
        fi
    done <<< "$scan_results"
    
    log "Found $candidate_count candidates, best: $best_bssid at $best_rssi dBm"
    
    # Check if best candidate meets threshold
    if [ -n "$best_bssid" ] && (( best_rssi >= min_rssi_needed )); then
        log "Selected AP: $best_bssid with RSSI $best_rssi (improvement: $(( best_rssi - current_rssi )) dB)"
        echo "$best_bssid"
        return 0
    fi
    
    log "No suitable AP found (best $best_bssid at $best_rssi, need >= $min_rssi_needed)"
    return 1
}

force_roam() {
    local target_bssid="$1"
    local now
    now=$(date +%s)
    
    # Anti-flap: don't roam too frequently
    if (( now - LAST_ROAM_TIME < MIN_ROAM_INTERVAL )); then
        log "Roam blocked by anti-flap ($(( now - LAST_ROAM_TIME ))s < ${MIN_ROAM_INTERVAL}s)"
        return 1
    fi
    
    log "ROAMING to $target_bssid..."
    
    # Method 1: wpa_cli roam (preferred)
    if wpa_cli -i "$IFACE" roam "$target_bssid" 2>/dev/null | grep -q "OK"; then
        LAST_ROAM_TIME=$now
        log "Roam command sent successfully"
        sleep 2
        
        # Verify roam succeeded
        local new_bssid
        new_bssid=$(iw dev "$IFACE" link 2>/dev/null | grep "Connected to" | sed -E 's/.*Connected to ([0-9a-fA-F:]+).*/\1/' | tr '[:lower:]' '[:upper:]')
        
        if [ "${new_bssid^^}" = "${target_bssid^^}" ]; then
            log "Roam successful! Now connected to $new_bssid"
            return 0
        else
            log "Roam may have failed, still on $new_bssid"
            return 1
        fi
    fi
    
    # Method 2: Fallback - reassociate
    log "Roam command failed, trying reassociate..."
    wpa_cli -i "$IFACE" reassociate >/dev/null 2>&1
    LAST_ROAM_TIME=$now
    sleep 3
    
    return 0
}

# === Main Loop ===

log "Wi-Fi Roaming Watchdog starting on $IFACE"
log "Thresholds: scan=$RSSI_SCAN_THRESHOLD, force=$RSSI_FORCE_ROAM_THRESHOLD, hysteresis=$RSSI_HYSTERESIS dB"
log "Intervals: check=${CHECK_INTERVAL}s, scan=${SCAN_INTERVAL}s, min_roam=${MIN_ROAM_INTERVAL}s"

# Initial scan to populate AP list
sleep 2
wpa_cli -i "$IFACE" scan >/dev/null 2>&1

LOOP_COUNT=0
while true; do
    LOOP_COUNT=$((LOOP_COUNT + 1))
    
    # Update connection info (sets CURRENT_BSSID, CURRENT_SSID, CURRENT_RSSI)
    update_connection_info
    
    if [ -z "$CURRENT_BSSID" ]; then
        log "Not connected, waiting..."
        sleep "$CHECK_INTERVAL"
        continue
    fi
    
    # Log status every 10 iterations (~20 seconds) or when signal is bad
    if (( LOOP_COUNT % 10 == 0 )) || (( CURRENT_RSSI <= RSSI_SCAN_THRESHOLD )); then
        log "Status: BSSID=${CURRENT_BSSID} RSSI=${CURRENT_RSSI}dBm SSID=${CURRENT_SSID}"
    fi
    
    # Check if we need to scan/roam
    if (( CURRENT_RSSI <= RSSI_FORCE_ROAM_THRESHOLD )); then
        # Critical signal - scan and roam immediately if better AP available
        log "CRITICAL RSSI ($CURRENT_RSSI dBm <= $RSSI_FORCE_ROAM_THRESHOLD), initiating roam..."
        
        if scan_for_aps; then
            sleep 1  # Wait for scan results
            
            if better_ap=$(find_better_ap "$CURRENT_RSSI"); then
                force_roam "$better_ap"
            else
                log "No better AP found, staying on current"
            fi
        fi
        
    elif (( CURRENT_RSSI <= RSSI_SCAN_THRESHOLD )); then
        # Degraded signal - scan periodically
        log "Degraded RSSI ($CURRENT_RSSI dBm <= $RSSI_SCAN_THRESHOLD), scanning..."
        
        if scan_for_aps; then
            sleep 1
            
            if better_ap=$(find_better_ap "$CURRENT_RSSI"); then
                force_roam "$better_ap"
            fi
        fi
    fi
    
    sleep "$CHECK_INTERVAL"
done
