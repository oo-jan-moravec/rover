const CLIENT_VERSION = "1.5.0"; // Local operator control only

console.log(`Mode: LOCAL`);

const statusEl = document.getElementById("status");
const leftValueEl = document.getElementById("leftValue");
const rightValueEl = document.getElementById("rightValue");

// Diagnostic elements
const cpuTempEl = document.getElementById("cpuTemp");
const wifiRssiEl = document.getElementById("wifiRssi");
const pingMsEl = document.getElementById("pingMs");
const wifiBssidEl = document.getElementById("wifiBssid");
const wifiFreqEl = document.getElementById("wifiFreq");
const wifiBitrateEl = document.getElementById("wifiBitrate");

// Wi-Fi state elements
const wifiStateBanner = document.getElementById("wifiStateBanner");
const wifiStateHeader = document.getElementById("wifiStateHeader");
const wifiStateIcon = document.getElementById("wifiStateIcon");
const wifiStateLabel = document.getElementById("wifiStateLabel");
const wifiStateDetail = document.getElementById("wifiStateDetail");
const wifiApCount = document.getElementById("wifiApCount");
const motorInhibitedOverlay = document.getElementById("motorInhibitedOverlay");
const motorInhibitedText = document.getElementById("motorInhibitedText");
const nearbyApsList = document.getElementById("nearbyApsList");

// Wi-Fi banner expand/collapse
if (wifiStateHeader) {
  wifiStateHeader.addEventListener('click', () => {
    wifiStateBanner?.classList.toggle('expanded');
  });
}

// Wi-Fi safety state tracking
let wifiState = "OFFLINE";
let motorsInhibited = false;
let lastStopReason = "";

// Knob elements
const maxVelValueEl = document.getElementById("maxVelValue");
const spinSpeedValueEl = document.getElementById("spinSpeedValue");
const turnValueEl = document.getElementById("turnValue");
const spinValueEl = document.getElementById("spinValue");
const fwdValueEl = document.getElementById("fwdValue");
const revValueEl = document.getElementById("revValue");
const maxVelIndicator = document.getElementById("maxVelIndicator");
const spinSpeedIndicator = document.getElementById("spinSpeedIndicator");
const turnIndicator = document.getElementById("turnIndicator");
const spinIndicator = document.getElementById("spinIndicator");
const fwdIndicator = document.getElementById("fwdIndicator");
const revIndicator = document.getElementById("revIndicator");
const maxVelKnob = document.getElementById("maxVelKnob");
const spinSpeedKnob = document.getElementById("spinSpeedKnob");
const turnKnob = document.getElementById("turnKnob");
const spinKnob = document.getElementById("spinKnob");
const fwdKnob = document.getElementById("fwdKnob");
const revKnob = document.getElementById("revKnob");

let ws;
let held = null; // Not used anymore but kept for compatibility
let joystickActive = false;
let joystickLeft = 0;  // -255 to 255
let joystickRight = 0; // -255 to 255
let reconnectAttempts = 0;
let reconnectTimer = null;
let pingInterval = null;
let lastPongTime = Date.now();

// Tuning parameters (stored in localStorage)
let maxVelocityPercent = parseFloat(localStorage.getItem('maxVelocity') || '100');
let spinSpeedPercent = parseFloat(localStorage.getItem('spinSpeed') || '50');
let turnThresholdPercent = parseFloat(localStorage.getItem('turnThreshold') || '15');
let spinZonePercent = parseFloat(localStorage.getItem('spinZone') || '15');
let forwardTrim = Math.max(-10, Math.min(10, parseInt(localStorage.getItem('forwardTrim') || '0')));
let reverseTrim = Math.max(-10, Math.min(10, parseInt(localStorage.getItem('reverseTrim') || '0')));
let headlightOn = false;
let irLedOn = false;

// Operator/Spectator state (server-managed)
let isOperator = false;
let myName = "";
let operatorName = "";
let pendingRequestFrom = null;
let requestPending = false;
let requestCooldownEnd = 0;
let cooldownInterval = null;
const REQUEST_COOLDOWN_MS = 30000; // 30 seconds

const SEND_HZ = 25;
const MAX_RECONNECT_DELAY = 5000;
const PING_INTERVAL = 3000;
const PONG_TIMEOUT = 10000;

function checkConnection() {
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  
  const timeSinceLastPong = Date.now() - lastPongTime;
  if (timeSinceLastPong > PONG_TIMEOUT) {
    console.log("Connection appears dead, reconnecting...");
    ws.close();
  }
}

// Handle new telemetry format from backend
function handleTelemetry(telem) {
  // Update Wi-Fi state
  if (telem.wifi) {
    // Debug: log if state is OFFLINE but we have valid data
    if (telem.wifi.state === "OFFLINE" && telem.wifi.bssid) {
      console.warn("State OFFLINE but have BSSID:", telem.wifi.bssid, "connected:", telem.wifi.connected);
    }
    
    // Use actual state, or override to OK if we clearly have a connection
    wifiState = telem.wifi.state || "OFFLINE";
    
    // Update RSSI display
    if (wifiRssiEl) {
      wifiRssiEl.textContent = telem.wifi.rssiDbm || "--";
    }
    
    // Update BSSID display (shortened)
    if (wifiBssidEl) {
      const bssid = telem.wifi.bssid || "";
      // Show last 8 chars for readability
      wifiBssidEl.textContent = bssid ? bssid.slice(-8) : "--:--:--";
    }
    
    // Update frequency
    if (wifiFreqEl) {
      wifiFreqEl.textContent = telem.wifi.freqMhz || "--";
    }
    
    // Update bitrates
    if (wifiBitrateEl) {
      const tx = telem.wifi.txBitrateMbps ? Math.round(telem.wifi.txBitrateMbps) : "--";
      const rx = telem.wifi.rxBitrateMbps ? Math.round(telem.wifi.rxBitrateMbps) : "--";
      wifiBitrateEl.textContent = `${tx}/${rx}`;
    }
    
    // Update Wi-Fi state banner
    updateWifiStateBanner(wifiState, telem.wifi.rssiDbm, telem.wifi.bssid);
    
    // Update nearby APs list
    if (telem.wifi.nearbyAps) {
      updateNearbyApsList(telem.wifi.nearbyAps, telem.wifi.betterApAvailable);
    }
  }
  
  // Update motor inhibition state
  if (telem.motors) {
    motorsInhibited = telem.motors.inhibited || false;
    lastStopReason = telem.motors.lastStopReason || "";
    updateMotorInhibitDisplay();
  }
  
  // Update system diagnostics
  if (telem.system) {
    if (cpuTempEl && telem.system.cpuTempC !== undefined) {
      cpuTempEl.textContent = telem.system.cpuTempC.toFixed(1);
    }
    if (pingMsEl) {
      pingMsEl.textContent = telem.system.pingMs === -1 ? "ERR" : telem.system.pingMs;
    }
  }
}

// Update the Wi-Fi state banner based on current state
function updateWifiStateBanner(state, rssi, bssid) {
  if (!wifiStateBanner) return;
  
  // Remove all state classes
  wifiStateBanner.classList.remove('ok', 'degraded', 'roaming', 'offline');
  
  // Update icon
  if (wifiStateIcon) {
    wifiStateIcon.classList.remove('fa-wifi', 'fa-wifi-slash', 'fa-wifi-exclamation', 'fa-spinner', 'fa-spin');
  }
  
  switch (state) {
    case 'OK':
      wifiStateBanner.classList.add('ok');
      if (wifiStateIcon) {
        wifiStateIcon.classList.add('fa-wifi');
      }
      if (wifiStateLabel) {
        wifiStateLabel.textContent = 'Wi-Fi OK';
      }
      if (wifiStateDetail) {
        wifiStateDetail.textContent = `${rssi} dBm • ${bssid ? bssid.slice(-8) : 'Connected'}`;
      }
      break;
      
    case 'DEGRADED':
      wifiStateBanner.classList.add('degraded');
      if (wifiStateIcon) {
        wifiStateIcon.classList.add('fa-wifi-exclamation');
      }
      if (wifiStateLabel) {
        wifiStateLabel.textContent = 'Wi-Fi Weak';
      }
      if (wifiStateDetail) {
        wifiStateDetail.textContent = `${rssi} dBm • Signal degraded`;
      }
      break;
      
    case 'ROAMING':
      wifiStateBanner.classList.add('roaming');
      if (wifiStateIcon) {
        wifiStateIcon.classList.add('fa-spinner', 'fa-spin');
      }
      if (wifiStateLabel) {
        wifiStateLabel.textContent = 'Switching AP...';
      }
      if (wifiStateDetail) {
        wifiStateDetail.textContent = 'Rover stopped for safety';
      }
      break;
      
    case 'OFFLINE':
    default:
      wifiStateBanner.classList.add('offline');
      if (wifiStateIcon) {
        wifiStateIcon.classList.add('fa-wifi-slash');
      }
      if (wifiStateLabel) {
        wifiStateLabel.textContent = 'Disconnected';
      }
      if (wifiStateDetail) {
        wifiStateDetail.textContent = 'Rover stopped';
      }
      break;
  }
}

// Update nearby APs list
function updateNearbyApsList(nearbyAps, betterAp) {
  // Update AP count badge
  if (wifiApCount) {
    const count = nearbyAps?.length || 0;
    wifiApCount.textContent = `${count} AP${count !== 1 ? 's' : ''}`;
    
    // Highlight if better AP available
    if (betterAp) {
      wifiApCount.style.background = 'rgba(63, 185, 80, 0.3)';
      wifiApCount.style.color = '#3fb950';
    } else {
      wifiApCount.style.background = 'rgba(0,0,0,0.2)';
      wifiApCount.style.color = '#6e7681';
    }
  }
  
  if (!nearbyApsList) return;
  
  if (!nearbyAps || nearbyAps.length === 0) {
    nearbyApsList.innerHTML = '<div class="nearby-ap-item">No APs found</div>';
    return;
  }
  
  let html = '';
  
  for (const ap of nearbyAps) {
    const isCurrent = ap.isCurrent;
    const isBetter = betterAp && ap.bssid === betterAp.bssid;
    
    // Determine RSSI class
    let rssiClass = 'bad';
    if (ap.rssiDbm >= -50) rssiClass = 'good';
    else if (ap.rssiDbm >= -70) rssiClass = 'medium';
    
    // Build item classes
    let itemClass = 'nearby-ap-item';
    if (isCurrent) itemClass += ' current';
    else if (isBetter) itemClass += ' better';
    
    // Format BSSID (show last 8 chars)
    const shortBssid = ap.bssid.slice(-8);
    
    // Build indicator
    let indicator = '';
    if (isCurrent) {
      indicator = '<span class="ap-indicator current">Current</span>';
    } else if (isBetter) {
      indicator = `<span class="ap-indicator better">+${betterAp.improvement}dB</span>`;
    }
    
    html += `
      <div class="${itemClass}">
        <span class="ap-bssid">${shortBssid}</span>
        <span class="ap-rssi ${rssiClass}">${ap.rssiDbm} dBm</span>
        ${indicator}
      </div>
    `;
  }
  
  nearbyApsList.innerHTML = html;
}

// Update motor inhibition display
function updateMotorInhibitDisplay() {
  if (!motorInhibitedOverlay) return;
  
  if (motorsInhibited && isOperator) {
    motorInhibitedOverlay.classList.add('visible');
    
    // Update the reason text
    if (motorInhibitedText) {
      switch (lastStopReason) {
        case 'roaming':
          motorInhibitedText.textContent = 'STOPPED - AP SWITCH';
          break;
        case 'offline':
          motorInhibitedText.textContent = 'STOPPED - OFFLINE';
          break;
        case 'watchdog':
          motorInhibitedText.textContent = 'STOPPED - WATCHDOG';
          break;
        default:
          motorInhibitedText.textContent = 'STOPPED';
      }
    }
  } else {
    motorInhibitedOverlay.classList.remove('visible');
  }
}

// WebSocket connection
function connect() {
  // Clear any existing timers
  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
  if (pingInterval) {
    clearInterval(pingInterval);
    pingInterval = null;
  }

  connectLocal();
}

function connectLocal() {
  const proto = (location.protocol === "https:") ? "wss" : "ws";
  
  try {
    ws = new WebSocket(`${proto}://${location.host}/ws`);

    ws.onopen = () => {
      statusEl.textContent = "WS: connected";
      reconnectAttempts = 0; // Reset counter on successful connection
      lastPongTime = Date.now();
      
      // Hide disconnect overlay
      disconnectOverlay?.classList.remove('visible');
      
      // Send version handshake first
      send(`VERSION:${CLIENT_VERSION}`);
      
      // Start ping interval
      pingInterval = setInterval(checkConnection, PING_INTERVAL);
    };
    
    ws.onclose = () => { 
      if (pingInterval) {
        clearInterval(pingInterval);
        pingInterval = null;
      }
      
      // Emergency: Set to OFFLINE state immediately on disconnect
      wifiState = "OFFLINE";
      motorsInhibited = true;
      lastStopReason = "offline";
      updateWifiStateBanner("OFFLINE", -100, "");
      updateMotorInhibitDisplay();
      
      const delay = Math.min(500 * Math.pow(1.5, reconnectAttempts), MAX_RECONNECT_DELAY);
      reconnectAttempts++;
      statusEl.textContent = `WS: disconnected (retry ${reconnectAttempts} in ${(delay/1000).toFixed(1)}s)`;
      
      // Show disconnect overlay
      disconnectOverlay?.classList.add('visible');
      if (disconnectStatus) {
        disconnectStatus.textContent = `Reconnecting in ${(delay/1000).toFixed(1)}s... (attempt ${reconnectAttempts})`;
      }
      
      reconnectTimer = setTimeout(connect, delay);
    };
    
    ws.onerror = (error) => {
      console.error("WebSocket error:", error);
      statusEl.textContent = "WS: error";
      
      // Show disconnect overlay
      disconnectOverlay?.classList.add('visible');
      if (disconnectStatus) {
        disconnectStatus.textContent = "Connection error";
      }
    };
    
    ws.onmessage = (event) => {
      lastPongTime = Date.now(); // Any message counts as a "pong"
      const msg = event.data;
      
      if (msg.startsWith("VERSION_MISMATCH:")) {
        const serverVersion = msg.substring(17);
        showVersionMismatchModal(serverVersion);
        return;
      } else if (msg === "VERSION_OK") {
        console.log("Version check passed");
        return;
      } else if (msg.startsWith("TELEM:")) {
        // New telemetry format with full Wi-Fi state
        try {
          const telem = JSON.parse(msg.substring(6));
          handleTelemetry(telem);
        } catch (e) {
          console.error("Failed to parse telemetry:", e);
        }
        return;
      } else if (msg.startsWith("DIAG:")) {
        // Legacy format - still update basic fields
        const data = msg.substring(5).split('|');
        if (data.length === 3) {
          if (cpuTempEl) cpuTempEl.textContent = data[0];
          if (pingMsEl) pingMsEl.textContent = data[2] === "-1" ? "ERR" : data[2];
        }
      } else if (msg.startsWith("NAME:")) {
        myName = msg.substring(5);
        console.log("My name:", myName);
      } else if (msg.startsWith("ROLE:")) {
        const parts = msg.substring(5).split('|');
        const role = parts[0];
        const extra = parts[1] || null;
        
        if (role === "operator") {
          isOperator = true;
          operatorName = myName;
          pendingRequestFrom = extra; // Will be requester name if someone is requesting
          requestPending = false;
        } else {
          isOperator = false;
          operatorName = extra !== "none" ? extra : "";
          pendingRequestFrom = null;
        }
        updateRoleUI();
      } else if (msg.startsWith("RESCAN:")) {
        // Handle rescan response
        const result = msg.substring(7);
        handleRescanResult(result);
      } else if (msg === "GRANTED") {
        // Our request was accepted
        requestPending = false;
        console.log("Control granted!");
        // Role update will come separately
      } else if (msg === "DENIED") {
        // Our request was denied
        requestPending = false;
        console.log("Control request denied");
        updateRoleUI();
      }
    };
  } catch (error) {
    console.error("Failed to create WebSocket:", error);
    statusEl.textContent = "WS: failed to connect";
    const delay = Math.min(500 * Math.pow(1.5, reconnectAttempts), MAX_RECONNECT_DELAY);
    reconnectAttempts++;
    reconnectTimer = setTimeout(connect, delay);
  }
}

function send(msg) {
  // Local mode uses raw WebSocket
  if (ws && ws.readyState === 1) ws.send(msg);
}

// Update UI based on current motor values
function updateUI(left, right) {
  // Update value display
  leftValueEl.textContent = left;
  rightValueEl.textContent = right;
}

function updateJoystickFromMotors(left, right) {
  // Reverse the motor calculation to get joystick position
  const forward = (left + right) / 2;
  const turn = (right - left) / 2;
  
  const yPercent = forward / 255;
  const xPercent = -turn / 255;  // Negate to match the fixed joystick direction
  
  const dx = xPercent * maxDistance;
  const dy = -yPercent * maxDistance;
  
  joystickHandle.style.transform = `translate(${dx}px, ${dy}px)`;
}

function tick() {
  let left, right;
  
  if (!isOperator) {
    // Not operator - don't send any commands
    left = 0;
    right = 0;
  } else if (motorsInhibited) {
    // Motors inhibited by backend - show 0 but still send commands (backend will ignore)
    left = 0;
    right = 0;
  } else if (joystickActive) {
    left = joystickLeft;
    right = joystickRight;
  } else {
    left = 0;
    right = 0;
  }
  
  updateUI(left, right);
  
  // Only send commands if we are the operator
  // Backend will ignore these if motors are inhibited
  if (isOperator) {
    send(`M ${left} ${right}`);
  }
  
  setTimeout(tick, 1000 / SEND_HZ);
}

// Joystick implementation
const joystick = document.getElementById("joystick");
const joystickHandle = document.getElementById("joystick-handle");

let isDragging = false;
let joystickRect = null;
let joystickCenterX = 0;
let joystickCenterY = 0;
const joystickRadius = 120; // Half of 240px
const handleRadius = 40;    // Half of 80px
const maxDistance = joystickRadius - handleRadius - 10; // Max distance from center

function updateJoystickRect() {
  joystickRect = joystick.getBoundingClientRect();
  joystickCenterX = joystickRect.left + joystickRect.width / 2;
  joystickCenterY = joystickRect.top + joystickRect.height / 2;
}

function handleJoystickMove(clientX, clientY) {
  if (!isDragging || !isOperator) return;

  // Calculate offset from center
  let dx = clientX - joystickCenterX;
  let dy = clientY - joystickCenterY;

  // Limit to max distance
  const distance = Math.sqrt(dx * dx + dy * dy);
  if (distance > maxDistance) {
    dx = (dx / distance) * maxDistance;
    dy = (dy / distance) * maxDistance;
  }

  // Update handle position
  joystickHandle.style.transform = `translate(${dx}px, ${dy}px)`;

  // Calculate motor values
  // Y-axis controls forward/backward, X-axis controls turning
  const yPercent = -dy / maxDistance; // -1 (down) to 1 (up)
  let xPercent = dx / maxDistance;  // -1 (left) to 1 (right)

  // Apply turn dead zone
  const turnDeadZone = turnThresholdPercent / 100;
  if (Math.abs(xPercent) < turnDeadZone) {
    xPercent = 0; // Ignore small left/right movements
  }

  // Car-like steering: turn slows down the inside wheel
  let left, right;
  
  const spinZone = spinZonePercent / 100;
  if (Math.abs(yPercent) < spinZone) {
    // Near center vertically - spin in place
    const spinSpeed = spinSpeedPercent / 100;
    left = xPercent * 255 * spinSpeed;
    right = -xPercent * 255 * spinSpeed;
  } else {
    // Moving forward/backward - car-like steering
    const throttle = yPercent * 255;
    
    // xPercent: -1 (left) to 1 (right)
    // When turning left (xPercent < 0):
    //   - Left wheel slows down
    //   - Right wheel stays at throttle
    // When turning right (xPercent > 0):
    //   - Right wheel slows down
    //   - Left wheel stays at throttle
    
    if (xPercent < 0) {
      // Turning left: slow down left wheel
      left = throttle * (1 + xPercent);  // xPercent is negative, so this reduces speed
      right = throttle;
    } else {
      // Turning right: slow down right wheel
      left = throttle;
      right = throttle * (1 - xPercent);  // xPercent is positive, so this reduces speed
    }
  }

  // Apply max velocity limiter
  const maxVel = (maxVelocityPercent / 100) * 255;
  left = Math.max(-maxVel, Math.min(maxVel, left));
  right = Math.max(-maxVel, Math.min(maxVel, right));

  // Apply trim based on direction
  if (yPercent > 0.1) {
    // Moving forward - apply forward trim
    left += forwardTrim;
    right -= forwardTrim;
  } else if (yPercent < -0.1) {
    // Moving backward - apply reverse trim
    left += reverseTrim;
    right -= reverseTrim;
  }

  // Clamp final values
  joystickLeft = Math.round(Math.max(-255, Math.min(255, left)));
  joystickRight = Math.round(Math.max(-255, Math.min(255, right)));

  joystickActive = true;
}

function resetJoystick() {
  isDragging = false;
  joystickHandle.classList.remove("active");
  joystickHandle.style.transform = "translate(0, 0)";
  joystickLeft = 0;
  joystickRight = 0;
  joystickActive = false;
}

// Mouse events
joystickHandle.addEventListener("mousedown", (e) => {
  if (!isOperator) return;
  e.preventDefault();
  isDragging = true;
  updateJoystickRect();
  joystickHandle.classList.add("active");
  held = null; // Disable button control
});

window.addEventListener("mousemove", (e) => {
  if (isDragging) {
    handleJoystickMove(e.clientX, e.clientY);
  }
});

window.addEventListener("mouseup", () => {
  if (isDragging) resetJoystick();
});

// Touch events
joystickHandle.addEventListener("touchstart", (e) => {
  if (!isOperator) return;
  e.preventDefault();
  isDragging = true;
  updateJoystickRect();
  joystickHandle.classList.add("active");
  held = null; // Disable button control
}, { passive: false });

window.addEventListener("touchmove", (e) => {
  if (isDragging && e.touches.length > 0 && isOperator) {
    e.preventDefault();
    const touch = e.touches[0];
    handleJoystickMove(touch.clientX, touch.clientY);
  }
}, { passive: false });

window.addEventListener("touchend", (e) => {
  if (isDragging) {
    e.preventDefault();
    resetJoystick();
  }
}, { passive: false });

window.addEventListener("touchcancel", () => {
  if (isDragging) resetJoystick();
});

// Pointer events for better cross-device support
joystickHandle.addEventListener("pointerdown", (e) => {
  if (e.pointerType === "mouse" || !isOperator) return; // Already handled by mouse events
  e.preventDefault();
  isDragging = true;
  updateJoystickRect();
  joystickHandle.classList.add("active");
  held = null;
});

window.addEventListener("pointermove", (e) => {
  if (isDragging && e.pointerType !== "mouse" && isOperator) {
    handleJoystickMove(e.clientX, e.clientY);
  }
});

window.addEventListener("pointerup", (e) => {
  if (isDragging && e.pointerType !== "mouse") {
    resetJoystick();
  }
});

// Initialize
connect();
tick();

// Mobile layout: move diagnostics into video container for overlay effect
function setupMobileLayout() {
  const isMobile = window.innerWidth <= 768;
  const diagnosticsSection = document.querySelector('.diagnostics-section');
  const videoContainer = document.querySelector('.video-container');
  const mainGrid = document.querySelector('.main-grid');
  
  if (!diagnosticsSection || !videoContainer || !mainGrid) return;
  
  if (isMobile) {
    // Move diagnostics into video container for overlay
    if (diagnosticsSection.parentElement !== videoContainer) {
      videoContainer.appendChild(diagnosticsSection);
      diagnosticsSection.classList.add('mobile-overlay');
    }
  } else {
    // Move back to main grid for desktop
    if (diagnosticsSection.parentElement === videoContainer) {
      // Find camera section and insert after it
      const cameraSection = document.querySelector('.camera-section');
      if (cameraSection && cameraSection.nextSibling) {
        mainGrid.insertBefore(diagnosticsSection, cameraSection.nextSibling);
      } else {
        mainGrid.appendChild(diagnosticsSection);
      }
      diagnosticsSection.classList.remove('mobile-overlay');
    }
  }
}

// Run on load and resize
setupMobileLayout();
window.addEventListener('resize', setupMobileLayout);

// Mobile splitter functionality
const mobileSplitter = document.getElementById('mobileSplitter');
const mainGrid = document.querySelector('.main-grid');
const cameraFeed = document.getElementById('cameraFeed');
const disconnectOverlay = document.getElementById('disconnectOverlay');
const disconnectStatus = document.getElementById('disconnectStatus');

// Set camera feed source dynamically
if (cameraFeed) {
  // Use relative path for cloud tunnels, but direct port for local access (port 8080 or no dots)
  const isLocal = location.port === "8080" || !location.hostname.includes('.');
  cameraFeed.src = isLocal
    ? `http://${location.hostname}:8889/cam/`
    : "/cam/";
}

let isSplitterDragging = false;
let startY = 0;
let startHeight = 0;

// Check if we're on mobile
function isMobile() {
  return window.innerWidth <= 768;
}

// Load saved camera height from localStorage
function loadCameraHeight() {
  const saved = localStorage.getItem('cameraHeight');
  if (saved && isMobile()) {
    document.documentElement.style.setProperty('--camera-height', saved);
  }
}

function saveCameraHeight(height) {
  localStorage.setItem('cameraHeight', height);
}

if (mobileSplitter && isMobile()) {
  mobileSplitter.addEventListener('mousedown', (e) => {
    e.preventDefault();
    isSplitterDragging = true;
    startY = e.clientY;
    const cameraSection = document.querySelector('.camera-section');
    startHeight = cameraSection.offsetHeight;
    document.body.style.userSelect = 'none';
  });

  mobileSplitter.addEventListener('touchstart', (e) => {
    e.preventDefault();
    isSplitterDragging = true;
    startY = e.touches[0].clientY;
    const cameraSection = document.querySelector('.camera-section');
    startHeight = cameraSection.offsetHeight;
  }, { passive: false });

  window.addEventListener('mousemove', (e) => {
    if (!isSplitterDragging) return;
    e.preventDefault();
    
    const deltaY = e.clientY - startY;
    const newHeight = Math.max(150, Math.min(window.innerHeight * 0.8, startHeight + deltaY));
    const heightValue = `${newHeight}px`;
    
    document.documentElement.style.setProperty('--camera-height', heightValue);
    saveCameraHeight(heightValue);
  });

  window.addEventListener('touchmove', (e) => {
    if (!isSplitterDragging) return;
    e.preventDefault();
    
    const deltaY = e.touches[0].clientY - startY;
    const newHeight = Math.max(150, Math.min(window.innerHeight * 0.8, startHeight + deltaY));
    const heightValue = `${newHeight}px`;
    
    document.documentElement.style.setProperty('--camera-height', heightValue);
    saveCameraHeight(heightValue);
  }, { passive: false });

  window.addEventListener('mouseup', () => {
    if (isSplitterDragging) {
      isSplitterDragging = false;
      document.body.style.userSelect = '';
    }
  });

  window.addEventListener('touchend', () => {
    isSplitterDragging = false;
  });

  // Load saved height on init
  loadCameraHeight();
}

// Knob controls
function setupKnob(knobEl, indicatorEl, valueEl, getValue, setValue, minVal, maxVal, formatFn) {
  let isDragging = false;
  let startY = 0;
  let startValue = 0;
  let floatingValueEl = null;
  
  const angleRange = 270; // Total rotation range in degrees
  const minAngle = -135;  // Starting position
  
  function valueToAngle(value) {
    const percent = (value - minVal) / (maxVal - minVal);
    return minAngle + (percent * angleRange);
  }
  
  function updateDisplay() {
    const val = getValue();
    const angle = valueToAngle(val);
    indicatorEl.style.transform = `rotate(${angle}deg)`;
    valueEl.textContent = formatFn(val);
    if (floatingValueEl) {
      floatingValueEl.textContent = formatFn(val);
    }
  }
  
  function updateFloatingPosition() {
    if (!floatingValueEl) return;
    const rect = knobEl.getBoundingClientRect();
    
    // Default: Place on the left
    let left = rect.left - 15;
    let top = rect.top + rect.height / 2;
    
    floatingValueEl.style.top = `${top}px`;
    floatingValueEl.style.left = `${left}px`;
    floatingValueEl.style.transform = 'translate(-100%, -50%)';
    
    // Check if it's overflowing the left edge
    const floatRect = floatingValueEl.getBoundingClientRect();
    if (floatRect.left < 10) {
      // Not enough space on left, flip to the RIGHT of the knob
      floatingValueEl.style.left = `${rect.right + 15}px`;
      floatingValueEl.style.transform = 'translate(0, -50%)';
    }
  }

  function handleMove(y) {
    if (!isDragging || !isOperator) return;
    
    // Vertical drag: UP increases value, DOWN decreases
    const deltaY = startY - y;
    const sensitivity = 150; // pixels for full range
    const range = maxVal - minVal;
    
    const valueDelta = (deltaY / sensitivity) * range;
    const newValue = Math.max(minVal, Math.min(maxVal, startValue + valueDelta));
    
    setValue(newValue);
    updateDisplay();
    updateFloatingPosition();
  }

  function startDrag(y) {
    if (!isOperator) return;
    isDragging = true;
    startY = y;
    startValue = getValue();

    // Create floating value display attached to body to avoid inherited scaling/clipping
    floatingValueEl = document.createElement('div');
    Object.assign(floatingValueEl.style, {
      position: 'fixed',
      backgroundColor: 'rgba(13, 17, 23, 0.95)',
      color: '#58a6ff',
      padding: '4px 10px',
      borderRadius: '6px',
      border: '2px solid #30363d',
      boxShadow: '0 4px 12px rgba(0,0,0,0.5)',
      fontSize: '18px',
      fontWeight: '700',
      zIndex: '9999',
      pointerEvents: 'none',
      fontFamily: 'ui-monospace, monospace'
    });
    
    document.body.appendChild(floatingValueEl);
    
    // Initial position & content
    updateFloatingPosition();
    updateDisplay();
    
    // Visual feedback: Zoom in
    knobEl.style.cursor = 'grabbing';
    knobEl.style.transform = 'scale(1.8)';
    knobEl.style.zIndex = '1000';
    knobEl.style.transition = 'transform 0.15s cubic-bezier(0.175, 0.885, 0.32, 1.275)';
  }

  function endDrag() {
    if (!isDragging) return;
    isDragging = false;
    
    if (floatingValueEl) {
      floatingValueEl.remove();
      floatingValueEl = null;
    }

    // Visual feedback: Zoom out
    knobEl.style.cursor = 'pointer';
    knobEl.style.transform = 'scale(1)';
    knobEl.style.zIndex = '';
  }
  
  knobEl.addEventListener('mousedown', (e) => {
    e.preventDefault();
    startDrag(e.clientY);
  });
  
  window.addEventListener('mousemove', (e) => {
    if (isDragging) handleMove(e.clientY);
  });
  
  window.addEventListener('mouseup', endDrag);
  
  knobEl.addEventListener('touchstart', (e) => {
    if (!isOperator) return;
    e.preventDefault();
    startDrag(e.touches[0].clientY);
  }, { passive: false });
  
  window.addEventListener('touchmove', (e) => {
    if (isDragging && e.touches.length > 0) {
      e.preventDefault();
      handleMove(e.touches[0].clientY);
    }
  }, { passive: false });
  
  window.addEventListener('touchend', endDrag);
  
  updateDisplay();
}

// Setup Max Velocity Knob (20-100%)
setupKnob(
  maxVelKnob,
  maxVelIndicator,
  maxVelValueEl,
  () => maxVelocityPercent,
  (val) => {
    maxVelocityPercent = val;
    localStorage.setItem('maxVelocity', maxVelocityPercent.toString());
  },
  20,
  100,
  (val) => `${Math.round(val)}%`
);

// Setup Spin Speed Knob (20-100%)
setupKnob(
  spinSpeedKnob,
  spinSpeedIndicator,
  spinSpeedValueEl,
  () => spinSpeedPercent,
  (val) => {
    spinSpeedPercent = val;
    localStorage.setItem('spinSpeed', spinSpeedPercent.toString());
  },
  20,
  100,
  (val) => `${Math.round(val)}%`
);

// Setup Turn Threshold Knob (0-50%)
setupKnob(
  turnKnob,
  turnIndicator,
  turnValueEl,
  () => turnThresholdPercent,
  (val) => {
    turnThresholdPercent = val;
    localStorage.setItem('turnThreshold', turnThresholdPercent.toString());
  },
  0,
  50,
  (val) => `${Math.round(val)}%`
);

// Setup Spin Zone Knob (5-30%)
setupKnob(
  spinKnob,
  spinIndicator,
  spinValueEl,
  () => spinZonePercent,
  (val) => {
    spinZonePercent = val;
    localStorage.setItem('spinZone', spinZonePercent.toString());
  },
  5,
  30,
  (val) => `${Math.round(val)}%`
);

// Setup Forward Trim Knob (-10 to 10)
setupKnob(
  fwdKnob,
  fwdIndicator,
  fwdValueEl,
  () => forwardTrim,
  (val) => {
    forwardTrim = Math.round(val);
    localStorage.setItem('forwardTrim', forwardTrim.toString());
  },
  -10,
  10,
  (val) => {
    const rounded = Math.round(val);
    return rounded > 0 ? `+${rounded}` : rounded.toString();
  }
);

// Setup Reverse Trim Knob (-10 to 10)
setupKnob(
  revKnob,
  revIndicator,
  revValueEl,
  () => reverseTrim,
  (val) => {
    reverseTrim = Math.round(val);
    localStorage.setItem('reverseTrim', reverseTrim.toString());
  },
  -10,
  10,
  (val) => {
    const rounded = Math.round(val);
    return rounded > 0 ? `+${rounded}` : rounded.toString();
  }
);

// Light controls
const headlightBtn = document.getElementById('headlightBtn');
const irLedBtn = document.getElementById('irLedBtn');

function toggleHeadlight(e) {
  if (!isOperator) return;
  e.preventDefault();
  e.stopPropagation();
  
  headlightOn = !headlightOn;
  const icon = headlightBtn?.querySelector('i');
  if (headlightOn) {
    headlightBtn?.classList.add('active');
    if (icon) {
      icon.classList.remove('fa-lightbulb');
      icon.classList.add('fa-lightbulb-on');
    }
  } else {
    headlightBtn?.classList.remove('active');
    if (icon) {
      icon.classList.remove('fa-lightbulb-on');
      icon.classList.add('fa-lightbulb');
    }
  }
  send(`H ${headlightOn ? 1 : 0}`);
}

function toggleIrLed(e) {
  if (!isOperator) return;
  e.preventDefault();
  e.stopPropagation();
  
  irLedOn = !irLedOn;
  const icon = irLedBtn?.querySelector('i');
  if (irLedOn) {
    irLedBtn?.classList.add('active');
    if (icon) {
      icon.classList.remove('fa-eye-slash');
      icon.classList.add('fa-eye');
    }
  } else {
    irLedBtn?.classList.remove('active');
    if (icon) {
      icon.classList.remove('fa-eye');
      icon.classList.add('fa-eye-slash');
    }
  }
  send(`I ${irLedOn ? 1 : 0}`);
}

if (headlightBtn) {
  headlightBtn.addEventListener('click', toggleHeadlight);
  headlightBtn.addEventListener('touchend', toggleHeadlight, { passive: false });
}

if (irLedBtn) {
  irLedBtn.addEventListener('click', toggleIrLed);
  irLedBtn.addEventListener('touchend', toggleIrLed, { passive: false });
}

// Rescan button
const rescanBtn = document.getElementById('rescanBtn');

function triggerRescan(e) {
  if (!isOperator) return;
  e.preventDefault();
  e.stopPropagation();
  
  if (rescanBtn?.classList.contains('scanning')) return;
  
  rescanBtn?.classList.add('scanning');
  const spanEl = rescanBtn?.querySelector('span');
  if (spanEl) spanEl.textContent = 'Restarting Wi-Fi...';
  
  // Show wifi restart overlay
  showWifiRestartOverlay();
  
  send('RESCAN');
}

function showWifiRestartOverlay() {
  // Create or get overlay
  let overlay = document.getElementById('wifiRestartOverlay');
  if (!overlay) {
    overlay = document.createElement('div');
    overlay.id = 'wifiRestartOverlay';
    overlay.innerHTML = `
      <div class="wifi-restart-content">
        <i class="fa-light fa-arrows-rotate fa-spin"></i>
        <span id="wifiRestartStatus">Restarting Wi-Fi...</span>
      </div>
    `;
    overlay.style.cssText = `
      position: fixed;
      top: 0;
      left: 0;
      right: 0;
      bottom: 0;
      background: rgba(0,0,0,0.7);
      display: flex;
      align-items: center;
      justify-content: center;
      z-index: 9999;
    `;
    const content = overlay.querySelector('.wifi-restart-content');
    if (content) {
      content.style.cssText = `
        background: #161b22;
        border: 1px solid #30363d;
        border-radius: 12px;
        padding: 24px 32px;
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 16px;
        color: #e6edf3;
        font-size: 16px;
      `;
    }
    const icon = overlay.querySelector('i');
    if (icon) {
      icon.style.cssText = 'font-size: 32px; color: #58a6ff;';
    }
    document.body.appendChild(overlay);
  }
  overlay.style.display = 'flex';
}

function hideWifiRestartOverlay() {
  const overlay = document.getElementById('wifiRestartOverlay');
  if (overlay) overlay.style.display = 'none';
}

function updateWifiRestartStatus(text) {
  const statusEl = document.getElementById('wifiRestartStatus');
  if (statusEl) statusEl.textContent = text;
}

function handleRescanResult(result) {
  const spanEl = rescanBtn?.querySelector('span');
  
  if (result === 'started') {
    // Still in progress - update overlay status
    rescanBtn?.classList.add('scanning');
    if (spanEl) spanEl.textContent = 'Restarting Wi-Fi...';
    updateWifiRestartStatus('Restarting Wi-Fi interface...');
    return;
  }
  
  // Operation complete - hide overlay and show result
  rescanBtn?.classList.remove('scanning');
  hideWifiRestartOverlay();
  
  // Parse result and show feedback
  if (result.startsWith('roamed:')) {
    const parts = result.split(':');
    const bssid = parts[1]?.slice(-8) || '';
    const rssi = parts[2] || '';
    if (spanEl) spanEl.textContent = `Roamed to ${bssid}`;
    setTimeout(() => {
      if (spanEl) spanEl.textContent = 'Restart Wi-Fi & Roam';
    }, 3000);
  } else if (result.startsWith('already_best:')) {
    if (spanEl) spanEl.textContent = 'Already on best AP';
    setTimeout(() => {
      if (spanEl) spanEl.textContent = 'Restart Wi-Fi & Roam';
    }, 2000);
  } else if (result.startsWith('no_better_ap:')) {
    if (spanEl) spanEl.textContent = 'No better AP found';
    setTimeout(() => {
      if (spanEl) spanEl.textContent = 'Restart Wi-Fi & Roam';
    }, 2000);
  } else if (result === 'no_aps_found') {
    if (spanEl) spanEl.textContent = 'No APs found';
    setTimeout(() => {
      if (spanEl) spanEl.textContent = 'Restart Wi-Fi & Roam';
    }, 2000);
  } else {
    console.log('Rescan result:', result);
    if (spanEl) spanEl.textContent = 'Restart Wi-Fi & Roam';
  }
}

if (rescanBtn) {
  rescanBtn.addEventListener('click', triggerRescan);
  rescanBtn.addEventListener('touchend', triggerRescan, { passive: false });
}

// Operator/Spectator role management
const roleStatusEl = document.getElementById('roleStatus');
const roleIconEl = document.getElementById('roleIcon');
const roleTextEl = document.getElementById('roleText');
const requestFromNameEl = document.getElementById('requestFromName');
const requestBtn = document.getElementById('requestBtn');
const releaseBtn = document.getElementById('releaseBtn');
const acceptBtn = document.getElementById('acceptBtn');
const denyBtn = document.getElementById('denyBtn');
const claimBtn = document.getElementById('claimBtn');
const requestOverlay = document.getElementById('requestOverlay');
const cooldownBar = document.getElementById('cooldownBar');
const allKnobs = document.querySelectorAll('.knob');

function updateCooldownDisplay() {
  const now = Date.now();
  const remaining = requestCooldownEnd - now;
  
  if (remaining <= 0) {
    // Cooldown finished
    if (cooldownInterval) {
      clearInterval(cooldownInterval);
      cooldownInterval = null;
    }
    requestBtn?.classList.remove('cooldown');
    if (cooldownBar) cooldownBar.style.width = '0%';
    const span = requestBtn?.querySelector('span');
    if (span) span.textContent = 'Request';
    return;
  }
  
  // Update cooldown bar and text
  const percent = (remaining / REQUEST_COOLDOWN_MS) * 100;
  if (cooldownBar) cooldownBar.style.width = `${percent}%`;
  const seconds = Math.ceil(remaining / 1000);
  const span = requestBtn?.querySelector('span');
  if (span) span.textContent = `${seconds}s`;
}

function startCooldown() {
  requestCooldownEnd = Date.now() + REQUEST_COOLDOWN_MS;
  requestBtn?.classList.add('cooldown');
  updateCooldownDisplay();
  
  if (cooldownInterval) clearInterval(cooldownInterval);
  cooldownInterval = setInterval(updateCooldownDisplay, 100);
}

function isOnCooldown() {
  return Date.now() < requestCooldownEnd;
}

function updateRoleUI() {
  if (isOperator) {
    // We are the operator
    roleStatusEl?.classList.remove('spectator');
    roleStatusEl?.classList.add('operator');
    roleIconEl?.classList.remove('fa-eye');
    roleIconEl?.classList.add('fa-gamepad');
    roleTextEl.textContent = `Controlling (${myName})`;
    
    // Hide request button, show release button, hide claim button
    requestBtn?.classList.add('hidden');
    releaseBtn?.classList.remove('hidden');
    claimBtn?.classList.add('hidden');
    
    // Show/hide request overlay based on pending request
    if (pendingRequestFrom) {
      requestFromNameEl.textContent = pendingRequestFrom;
      requestOverlay?.classList.add('visible');
    } else {
      requestOverlay?.classList.remove('visible');
    }
    
    // Enable controls (unless motors are inhibited)
    headlightBtn?.classList.remove('disabled');
    irLedBtn?.classList.remove('disabled');
    joystick?.classList.remove('disabled');
    joystickHandle?.classList.remove('disabled');
    allKnobs.forEach(knob => knob.classList.remove('disabled'));
    rescanBtn?.classList.remove('disabled');
    
    // Update motor inhibit overlay
    updateMotorInhibitDisplay();
  } else {
    // We are a spectator
    roleStatusEl?.classList.remove('operator');
    roleStatusEl?.classList.add('spectator');
    roleIconEl?.classList.remove('fa-gamepad');
    roleIconEl?.classList.add('fa-eye');
    
    // Hide release button and request overlay for spectators
    releaseBtn?.classList.add('hidden');
    requestOverlay?.classList.remove('visible');
    
    if (operatorName) {
      roleTextEl.textContent = `${operatorName} controlling`;
      requestBtn?.classList.remove('hidden');
      claimBtn?.classList.add('hidden');
    } else {
      roleTextEl.textContent = `No operator`;
      requestBtn?.classList.add('hidden');
      claimBtn?.classList.remove('hidden');
    }
    
    // Update request button state based on cooldown and pending
    if (isOnCooldown()) {
      requestBtn?.classList.add('cooldown');
    } else if (requestPending) {
      requestBtn?.classList.add('pending');
      requestBtn?.classList.remove('cooldown');
      const span = requestBtn?.querySelector('span');
      if (span) span.textContent = 'Waiting...';
    } else {
      requestBtn?.classList.remove('pending');
      requestBtn?.classList.remove('cooldown');
      const span = requestBtn?.querySelector('span');
      if (span) span.textContent = 'Request';
      if (cooldownBar) cooldownBar.style.width = '0%';
    }
    
    // Disable controls
    headlightBtn?.classList.add('disabled');
    irLedBtn?.classList.add('disabled');
    joystick?.classList.add('disabled');
    joystickHandle?.classList.add('disabled');
    allKnobs.forEach(knob => knob.classList.add('disabled'));
    rescanBtn?.classList.add('disabled');
    
    // Force reset all interaction states
    isDragging = false;
    joystickActive = false;
    joystickLeft = 0;
    joystickRight = 0;
    
    // Reset joystick visual position
    if (joystickHandle) {
      joystickHandle.classList.remove('active');
      joystickHandle.style.transform = 'translate(0, 0)';
    }
  }
}

function handleClaim(e) {
  e.preventDefault();
  e.stopPropagation();
  send('CLAIM');
}

function handleRequest(e) {
  e.preventDefault();
  e.stopPropagation();
  if (requestPending || isOnCooldown()) return;
  requestPending = true;
  startCooldown();
  updateRoleUI();
  send('REQUEST');
}

function handleRelease(e) {
  e.preventDefault();
  e.stopPropagation();
  send('RELEASE');
}

function handleAccept(e) {
  e.preventDefault();
  e.stopPropagation();
  send('ACCEPT');
}

function handleDeny(e) {
  e.preventDefault();
  e.stopPropagation();
  send('DENY');
}

if (claimBtn) {
  claimBtn.addEventListener('click', handleClaim);
  claimBtn.addEventListener('touchend', handleClaim, { passive: false });
}

if (requestBtn) {
  requestBtn.addEventListener('click', handleRequest);
  requestBtn.addEventListener('touchend', handleRequest, { passive: false });
}

if (releaseBtn) {
  releaseBtn.addEventListener('click', handleRelease);
  releaseBtn.addEventListener('touchend', handleRelease, { passive: false });
}

if (acceptBtn) {
  acceptBtn.addEventListener('click', handleAccept);
  acceptBtn.addEventListener('touchend', handleAccept, { passive: false });
}

if (denyBtn) {
  denyBtn.addEventListener('click', handleDeny);
  denyBtn.addEventListener('touchend', handleDeny, { passive: false });
}

// Version mismatch modal
function showVersionMismatchModal(serverVersion) {
  // Stop reconnection attempts
  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
  if (pingInterval) {
    clearInterval(pingInterval);
    pingInterval = null;
  }
  
  // Create modal overlay
  const overlay = document.createElement('div');
  overlay.style.cssText = `
    position: fixed;
    top: 0;
    left: 0;
    right: 0;
    bottom: 0;
    background: rgba(0, 0, 0, 0.9);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 10000;
  `;
  
  const modal = document.createElement('div');
  modal.style.cssText = `
    background: #161b22;
    border: 2px solid #f85149;
    border-radius: 12px;
    padding: 32px;
    max-width: 400px;
    text-align: center;
    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.6);
  `;
  
  modal.innerHTML = `
    <div style="font-size: 48px; margin-bottom: 16px;">
      <i class="fa-light fa-triangle-exclamation" style="color: #f85149;"></i>
    </div>
    <h2 style="color: #f0f6fc; margin-bottom: 12px; font-size: 20px;">Update Required</h2>
    <p style="color: #8b949e; margin-bottom: 8px; font-size: 14px;">
      Your client is outdated and incompatible with the server.
    </p>
    <p style="color: #6e7681; margin-bottom: 24px; font-size: 12px;">
      Your version: <span style="color: #f85149;">${CLIENT_VERSION}</span><br>
      Server version: <span style="color: #3fb950;">${serverVersion}</span>
    </p>
    <button id="reloadBtn" style="
      background: #1f6feb;
      border: none;
      color: #fff;
      padding: 12px 32px;
      border-radius: 6px;
      font-size: 16px;
      font-weight: 600;
      cursor: pointer;
      display: inline-flex;
      align-items: center;
      gap: 8px;
      transition: background 0.2s;
    ">
      <i class="fa-light fa-arrows-rotate"></i>
      Reload Page
    </button>
  `;
  
  overlay.appendChild(modal);
  document.body.appendChild(overlay);
  
  // Add reload button handler
  document.getElementById('reloadBtn').addEventListener('click', () => {
    location.reload(true);
  });
  
  // Prevent any interaction with the page behind
  overlay.addEventListener('click', (e) => {
    e.stopPropagation();
  });
}