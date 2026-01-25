const statusEl = document.getElementById("status");
const leftValueEl = document.getElementById("leftValue");
const rightValueEl = document.getElementById("rightValue");

// Diagnostic elements
const cpuTempEl = document.getElementById("cpuTemp");
const wifiSignalEl = document.getElementById("wifiSignal");
const pingMsEl = document.getElementById("pingMs");

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
let forwardTrim = parseInt(localStorage.getItem('forwardTrim') || '0');
let reverseTrim = parseInt(localStorage.getItem('reverseTrim') || '0');
let headlightOn = false;
let spectatorMode = false;

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

  const proto = (location.protocol === "https:") ? "wss" : "ws";
  
  try {
    ws = new WebSocket(`${proto}://${location.host}/ws`);

    ws.onopen = () => {
      statusEl.textContent = "WS: connected";
      reconnectAttempts = 0; // Reset counter on successful connection
      lastPongTime = Date.now();
      
      // Start ping interval
      pingInterval = setInterval(checkConnection, PING_INTERVAL);
    };
    
    ws.onclose = () => { 
      if (pingInterval) {
        clearInterval(pingInterval);
        pingInterval = null;
      }
      
      const delay = Math.min(500 * Math.pow(1.5, reconnectAttempts), MAX_RECONNECT_DELAY);
      reconnectAttempts++;
      statusEl.textContent = `WS: disconnected (retry ${reconnectAttempts} in ${(delay/1000).toFixed(1)}s)`;
      reconnectTimer = setTimeout(connect, delay);
    };
    
    ws.onerror = (error) => {
      console.error("WebSocket error:", error);
      statusEl.textContent = "WS: error";
    };
    
    ws.onmessage = (event) => {
      lastPongTime = Date.now(); // Any message counts as a "pong"
      const msg = event.data;
      if (msg.startsWith("DIAG:")) {
        const data = msg.substring(5).split('|');
        if (data.length === 3) {
          if (cpuTempEl) cpuTempEl.textContent = data[0];
          if (wifiSignalEl) wifiSignalEl.textContent = data[1];
          if (pingMsEl) pingMsEl.textContent = data[2] === "-1" ? "ERR" : data[2];
        }
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
  
  if (spectatorMode) {
    // In spectator mode, don't send any commands
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
  
  // Only send commands if not in spectator mode
  if (!spectatorMode) {
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
  if (!isDragging || spectatorMode) return;

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
  if (spectatorMode) return;
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
  if (spectatorMode) return;
  e.preventDefault();
  isDragging = true;
  updateJoystickRect();
  joystickHandle.classList.add("active");
  held = null; // Disable button control
}, { passive: false });

window.addEventListener("touchmove", (e) => {
  if (isDragging && e.touches.length > 0 && !spectatorMode) {
    e.preventDefault();
    const touch = e.touches[0];
    handleJoystickMove(touch.clientX, touch.clientY);
  }
}, { passive: false });

window.addEventListener("touchend", (e) => {
  if (isDragging || spectatorMode) {
    e.preventDefault();
    resetJoystick();
  }
}, { passive: false });

window.addEventListener("touchcancel", () => {
  if (isDragging || spectatorMode) resetJoystick();
});

// Pointer events for better cross-device support
joystickHandle.addEventListener("pointerdown", (e) => {
  if (e.pointerType === "mouse" || spectatorMode) return; // Already handled by mouse events
  e.preventDefault();
  isDragging = true;
  updateJoystickRect();
  joystickHandle.classList.add("active");
  held = null;
});

window.addEventListener("pointermove", (e) => {
  if (isDragging && e.pointerType !== "mouse" && !spectatorMode) {
    handleJoystickMove(e.clientX, e.clientY);
  }
});

window.addEventListener("pointerup", (e) => {
  if (isDragging && e.pointerType !== "mouse" || spectatorMode) {
    resetJoystick();
  }
});

// Initialize
connect();
tick();

// Mobile splitter functionality
const mobileSplitter = document.getElementById('mobileSplitter');
const mainGrid = document.querySelector('.main-grid');
const cameraFeed = document.getElementById('cameraFeed');

// Set camera feed source dynamically
if (cameraFeed) {
  cameraFeed.src = `http://${location.hostname}:8889/cam`;
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
  let startAngle = 0;
  let startValue = 0;
  
  const angleRange = 270; // Total rotation range in degrees
  const minAngle = -135;  // Starting position
  
  function valueToAngle(value) {
    const percent = (value - minVal) / (maxVal - minVal);
    return minAngle + (percent * angleRange);
  }
  
  function updateDisplay() {
    const angle = valueToAngle(getValue());
    indicatorEl.style.transform = `rotate(${angle}deg)`;
    valueEl.textContent = formatFn(getValue());
  }
  
  function getAngleFromEvent(e) {
    const rect = knobEl.getBoundingClientRect();
    const centerX = rect.left + rect.width / 2;
    const centerY = rect.top + rect.height / 2;
    const dx = e.clientX - centerX;
    const dy = e.clientY - centerY;
    return Math.atan2(dy, dx) * (180 / Math.PI);
  }
  
  function handleMove(e) {
    if (!isDragging || spectatorMode) return;
    
    const currentAngle = getAngleFromEvent(e);
    let angleDelta = currentAngle - startAngle;
    
    // Handle wrapping
    if (angleDelta > 180) angleDelta -= 360;
    if (angleDelta < -180) angleDelta += 360;
    
    const valueDelta = (angleDelta / angleRange) * (maxVal - minVal);
    const newValue = Math.max(minVal, Math.min(maxVal, startValue + valueDelta));
    
    setValue(newValue);
    updateDisplay();
  }
  
  knobEl.addEventListener('mousedown', (e) => {
    if (spectatorMode) return;
    e.preventDefault();
    isDragging = true;
    startAngle = getAngleFromEvent(e);
    startValue = getValue();
    knobEl.style.cursor = 'grabbing';
  });
  
  window.addEventListener('mousemove', handleMove);
  
  window.addEventListener('mouseup', () => {
    isDragging = false;
    knobEl.style.cursor = 'pointer';
  });
  
  knobEl.addEventListener('touchstart', (e) => {
    if (spectatorMode) return;
    e.preventDefault();
    isDragging = true;
    const touch = e.touches[0];
    startAngle = getAngleFromEvent(touch);
    startValue = getValue();
  }, { passive: false });
  
  window.addEventListener('touchmove', (e) => {
    if (isDragging && e.touches.length > 0) {
      handleMove(e.touches[0]);
    }
  }, { passive: false });
  
  window.addEventListener('touchend', () => {
    isDragging = false;
  });
  
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

// Setup Forward Trim Knob (-50 to 50)
setupKnob(
  fwdKnob,
  fwdIndicator,
  fwdValueEl,
  () => forwardTrim,
  (val) => {
    forwardTrim = Math.round(val);
    localStorage.setItem('forwardTrim', forwardTrim.toString());
  },
  -50,
  50,
  (val) => {
    const rounded = Math.round(val);
    return rounded > 0 ? `+${rounded}` : rounded.toString();
  }
);

// Setup Reverse Trim Knob (-50 to 50)
setupKnob(
  revKnob,
  revIndicator,
  revValueEl,
  () => reverseTrim,
  (val) => {
    reverseTrim = Math.round(val);
    localStorage.setItem('reverseTrim', reverseTrim.toString());
  },
  -50,
  50,
  (val) => {
    const rounded = Math.round(val);
    return rounded > 0 ? `+${rounded}` : rounded.toString();
  }
);

// Headlight control
const headlightBtn = document.getElementById('headlightBtn');
const headlightIcon = headlightBtn?.querySelector('.control-icon');

function toggleHeadlight(e) {
  if (spectatorMode) return;
  e.preventDefault();
  e.stopPropagation();
  
  headlightOn = !headlightOn;
  if (headlightOn) {
    headlightBtn.classList.add('active');
    if (headlightIcon) {
      headlightIcon.classList.remove('fa-lightbulb');
      headlightIcon.classList.add('fa-lightbulb-on');
    }
  } else {
    headlightBtn.classList.remove('active');
    if (headlightIcon) {
      headlightIcon.classList.remove('fa-lightbulb-on');
      headlightIcon.classList.add('fa-lightbulb');
    }
  }
  send(`H ${headlightOn ? 1 : 0}`);
}

if (headlightBtn) {
  // Handle both click and touch events
  headlightBtn.addEventListener('click', toggleHeadlight);
  headlightBtn.addEventListener('touchend', toggleHeadlight, { passive: false });
}

// Spectator mode control
const spectatorBtn = document.getElementById('spectatorBtn');
const spectatorIcon = spectatorBtn?.querySelector('.control-icon');
const allKnobs = document.querySelectorAll('.knob');

function updateSpectatorMode() {
  if (spectatorMode) {
    spectatorBtn?.classList.add('active');
    headlightBtn?.classList.add('disabled');
    joystick?.classList.add('disabled');
    joystickHandle?.classList.add('disabled');
    allKnobs.forEach(knob => knob.classList.add('disabled'));
    
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
    
    // Send stop command
    send('S');
  } else {
    spectatorBtn?.classList.remove('active');
    headlightBtn?.classList.remove('disabled');
    joystick?.classList.remove('disabled');
    joystickHandle?.classList.remove('disabled');
    allKnobs.forEach(knob => knob.classList.remove('disabled'));
  }
}

function toggleSpectatorMode(e) {
  e.preventDefault();
  e.stopPropagation();
  spectatorMode = !spectatorMode;
  updateSpectatorMode();
}

if (spectatorBtn) {
  // Handle both click and touch events
  spectatorBtn.addEventListener('click', toggleSpectatorMode);
  spectatorBtn.addEventListener('touchend', toggleSpectatorMode, { passive: false });
}