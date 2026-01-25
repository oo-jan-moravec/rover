# Rover Cloud Relay

Cloud relay layer for remote rover control over the internet. This enables controlling the rover from anywhere without being on the same local network.

## Architecture

```
┌──────────────┐         ┌─────────────────────┐         ┌─────────────┐
│   Remote     │◄──WSS──►│  RoverCloudRelay    │◄──WSS──►│   Rover     │
│   Browser    │         │  (Azure App Service) │         │   (RPi)     │
│              │         │                      │         │             │
│   - UI       │         │  - SignalR relay     │         │  - Camera   │
│   - WebRTC   │◄─TURN──►│  - WHEP proxy        │◄─HTTP──►│  - mediamtx │
│     video    │         │  - Static files      │         │             │
└──────────────┘         └─────────────────────┘         └─────────────┘
```

## Features

- **Full remote UI** - Same interface as local, served from the cloud
- **SignalR WebSocket relay** - Real-time bidirectional communication
- **WebRTC video via WHEP** - Low-latency video through Cloudflare TURN
- **Simple auth** - API key for rover, access key for clients
- **Operator/spectator model** - Only one person controls at a time
- **Telemetry relay** - Wi-Fi status, CPU temp, diagnostics

## Quick Start

### 1. Deploy to Azure

The script creates everything you need:
- App Service (for the relay)
- Azure Communication Services (for TURN/video)

```powershell
./deploy-azure.ps1 `
    -AppName "my-rover-relay" `
    -RoverApiKey "your-secret-rover-key" `
    -ClientAccessKey "your-client-access-key"
```

That's it! Azure Communication Services provides the TURN server for WebRTC video.

### 2. Configure Rover

Update `RoverWeb/appsettings.json` on the Pi:

```json
{
  "CloudRelay": {
    "Enabled": true,
    "Url": "https://my-rover-relay.azurewebsites.net/rover",
    "ApiKey": "your-secret-rover-key",
    "LocalWhepUrl": "http://127.0.0.1:8889/cam/whep"
  }
}
```

Then deploy to Pi:
```powershell
cd ../rover-web
./deploy-to-rpi.ps1
```

### 3. Connect from Anywhere

Simply browse to your Azure URL:
```
https://my-rover-relay.azurewebsites.net
```

Enter your access key when prompted, and you're in!

## Local Development

### Run Cloud Relay Locally

```bash
cd RoverCloudRelay
dotnet run
```

The relay will start on `http://localhost:5000`.

### Test with Rover

Set rover config to use local relay:

```json
{
  "CloudRelay": {
    "Enabled": true,
    "Url": "http://YOUR_DEV_IP:5000/rover",
    "ApiKey": "dev-rover-key"
  }
}
```

## Configuration

### Cloud Relay (`appsettings.json`)

| Setting | Description |
|---------|-------------|
| `RoverRelay:RoverApiKey` | Secret key for rover authentication |
| `RoverRelay:ClientAccessKey` | Key for client authentication |
| `RoverRelay:TurnServer:Urls` | TURN server URLs for WebRTC |
| `RoverRelay:TurnServer:Username` | TURN username |
| `RoverRelay:TurnServer:Credential` | TURN password |

### Rover (`appsettings.json`)

| Setting | Description |
|---------|-------------|
| `CloudRelay:Enabled` | Enable/disable cloud connection |
| `CloudRelay:Url` | Cloud relay SignalR hub URL |
| `CloudRelay:ApiKey` | API key matching relay config |

## API Endpoints

| Endpoint | Description |
|----------|-------------|
| `GET /health` | Health check |
| `GET /status` | Rover/client connection status |
| `WS /rover` | SignalR hub for rover |
| `WS /client` | SignalR hub for clients |

## SignalR Protocol

### Rover → Cloud

| Method | Description |
|--------|-------------|
| `SendTelemetry(object)` | Broadcast telemetry to all clients |
| `SendToClient(clientId, type, data)` | Send message to specific client |
| `SendWebRtcSignal(clientId, signal)` | WebRTC signaling to client |

### Cloud → Rover

| Method | Description |
|--------|-------------|
| `MotorCommand(left, right)` | Motor control command |
| `Stop()` | Emergency stop |
| `Headlight(on)` | Toggle headlight |
| `Rescan(clientId)` | Trigger Wi-Fi rescan |
| `ClientJoined(id, name)` | Client connected |
| `ClientLeft(id)` | Client disconnected |
| `OperatorChanged(name)` | New operator assigned |
| `OperatorReleased()` | Operator released control |
| `WebRtcSignal(clientId, signal)` | WebRTC signaling from client |

### Client → Cloud

| Method | Description |
|--------|-------------|
| `SendMotorCommand(left, right)` | Control motors (operator only) |
| `SendStop()` | Emergency stop (operator only) |
| `SendHeadlight(on)` | Toggle headlight (operator only) |
| `SendRescan()` | Request Wi-Fi rescan (operator only) |
| `Claim()` | Claim operator role |
| `Release()` | Release operator role |
| `SendWebRtcSignal(signal)` | WebRTC signaling to rover |

### Cloud → Client

| Method | Description |
|--------|-------------|
| `Welcome(name, roverOnline, turnConfig)` | Initial welcome message |
| `RoverStatus(online)` | Rover online/offline status |
| `Telemetry(data)` | Rover telemetry data |
| `RoleStatus(isOperator, operatorName)` | Role assignment |
| `RescanResult(result)` | Wi-Fi rescan result |
| `WebRtcSignal(signal)` | WebRTC signaling from rover |

## Video Streaming

For low-latency video over the internet, you need WebRTC with a TURN server.

### Option 1: Twilio (recommended for testing)

1. Sign up at [Twilio](https://www.twilio.com/)
2. Get Network Traversal Service credentials
3. Add to cloud relay config

### Option 2: Self-hosted coturn

Deploy coturn on a VPS:

```bash
apt install coturn
# Configure /etc/turnserver.conf
```

### Option 3: Azure Communication Services

Use ACS for managed TURN infrastructure.

## Cost Estimate

| Tier | WebSockets | Cost/month |
|------|------------|------------|
| F1 (Free) | No | $0 |
| B1 (Basic) | Yes | ~$13-15 |
| S1 (Standard) | Yes | ~$70 |

**Recommendation:** Use B1 for production. It's the cheapest tier with WebSocket support.

## Troubleshooting

### Rover won't connect

1. Check `CloudRelay:Enabled` is `true`
2. Verify API key matches
3. Check rover has internet access
4. Check cloud relay logs: `az webapp log tail --name <app> --resource-group <rg>`

### Client can't connect

1. Verify access key is correct
2. Check browser console for errors
3. Ensure WebSockets are enabled on App Service

### High latency

1. Choose Azure region closest to you
2. Consider upgrading App Service tier
3. For video, ensure TURN server is geographically close

## Security Notes

- All connections use WSS (WebSocket Secure) in production
- API keys are passed via query string (use HTTPS!)
- Consider adding rate limiting for production
- Rotate keys periodically
