using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

static class WebSocketEndpoint
{
    private const string ClientVersion = "1.6.0"; // Local operator control only

    public static void MapWebSocketEndpoint(this IEndpointRouteBuilder app)
    {
        app.Map("/ws", async (HttpContext ctx, RoverState state, WebSocketManager wsManager, OperatorManager opManager, GpioController gpio, SafetyStateMachine safetyMachine, AudioPlaybackService audioPlayback, RoverLogService logService) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var clientId = Guid.NewGuid();
            var clientName = opManager.RegisterClient(clientId);
            wsManager.AddClient(clientId, ws);

            // Helper to send role status to a specific client
            async Task SendRoleStatus(Guid targetId)
            {
                if (opManager.IsOperator(targetId))
                {
                    var pendingName = opManager.GetPendingRequesterName();
                    if (pendingName != null)
                        await wsManager.SendToClientAsync(targetId, $"ROLE:operator|{pendingName}");
                    else
                        await wsManager.SendToClientAsync(targetId, "ROLE:operator");
                }
                else
                {
                    var operatorName = opManager.GetCurrentOperatorName();
                    if (operatorName != null)
                        await wsManager.SendToClientAsync(targetId, $"ROLE:spectator|{operatorName}");
                    else
                        await wsManager.SendToClientAsync(targetId, "ROLE:spectator|none");
                }
            }

            // Helper to broadcast role updates to all clients
            async Task BroadcastRoleUpdates()
            {
                foreach (var id in wsManager.GetAllClientIds())
                {
                    await SendRoleStatus(id);
                }
            }

            try
            {
                var buffer = new byte[256];

                // Wait for version handshake first
                var versionResult = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (versionResult.MessageType == WebSocketMessageType.Close) return;

                var versionMsg = Encoding.UTF8.GetString(buffer, 0, versionResult.Count).Trim();

                if (!versionMsg.StartsWith("VERSION:"))
                {
                    // No version provided - outdated client
                    await wsManager.SendToClientAsync(clientId, $"VERSION_MISMATCH:{ClientVersion}");
                    return;
                }

                var clientVersion = versionMsg.Substring(8);
                if (clientVersion != ClientVersion)
                {
                    // Version mismatch - client needs to reload
                    await wsManager.SendToClientAsync(clientId, $"VERSION_MISMATCH:{ClientVersion}");
                    return;
                }

                // Version OK - continue with normal setup
                await wsManager.SendToClientAsync(clientId, "VERSION_OK");
                await wsManager.SendToClientAsync(clientId, $"NAME:{clientName}");
                await SendRoleStatus(clientId);
                await logService.SendHistoryAsync(clientId);

                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    // Handle binary messages (audio from pilot)
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        // Only accept audio from operator
                        if (opManager.IsOperator(clientId))
                        {
                            var audioData = new byte[result.Count];
                            Array.Copy(buffer, 0, audioData, 0, result.Count);
                            Console.WriteLine($"Received audio chunk from operator: {result.Count} bytes");
                            audioPlayback.PlayAudioChunk(audioData);
                        }
                        else
                        {
                            // Log if non-operator tries to send audio
                            Console.WriteLine($"Non-operator {clientId} attempted to send audio");
                        }
                        continue;
                    }

                    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count).Trim();

                    // Handle operator/spectator protocol
                    if (msg == "CLAIM")
                    {
                        if (opManager.TryClaim(clientId))
                        {
                            logService.Publish("ops", $"{clientName} claimed control", null, "info");
                            await BroadcastRoleUpdates();
                        }
                        else
                        {
                            await SendRoleStatus(clientId);
                        }
                    }
                    else if (msg == "REQUEST")
                    {
                        if (opManager.RequestControl(clientId, out var operatorId))
                        {
                            // Auto-granted (no operator was present)
                            logService.Publish("ops", $"{clientName} claimed control (auto-granted)", null, "info");
                            await BroadcastRoleUpdates();
                        }
                        else if (operatorId.HasValue)
                        {
                            // Notify operator of request
                            var operatorName = opManager.GetCurrentOperatorName() ?? "operator";
                            logService.Publish("ops", $"{clientName} requested control", $"from {operatorName}", "info");
                            await BroadcastRoleUpdates();
                        }
                    }
                    else if (msg == "ACCEPT")
                    {
                        var (success, newOperatorId, oldOperatorId) = opManager.AcceptRequest(clientId);
                        if (success && newOperatorId.HasValue)
                        {
                            var newOperatorName = opManager.GetCurrentOperatorName() ?? "operator";
                            logService.Publish("ops", $"Control transferred to {newOperatorName}", "request accepted", "info");
                            await wsManager.SendToClientAsync(newOperatorId.Value, "GRANTED");
                            await BroadcastRoleUpdates();
                            // Stop the rover when control transfers
                            state.Set(0, 0);
                        }
                    }
                    else if (msg == "DENY")
                    {
                        var (success, requesterId) = opManager.DenyRequest(clientId);
                        if (success && requesterId.HasValue)
                        {
                            logService.Publish("ops", "Control request denied", $"by {clientName}", "info");
                            await wsManager.SendToClientAsync(requesterId.Value, "DENIED");
                            await BroadcastRoleUpdates();
                        }
                    }
                    else if (msg == "RELEASE")
                    {
                        if (opManager.ReleaseControl(clientId))
                        {
                            logService.Publish("ops", $"{clientName} released control", "rover stopped", "info");
                            state.Set(0, 0); // Stop rover
                            await BroadcastRoleUpdates();
                        }
                    }
                    // Handle control commands - only from operator
                    else if (opManager.IsOperator(clientId))
                    {
                        state.Touch();

                        if (msg == "S")
                        {
                            logService.Publish("safety", "Emergency stop", "operator command", "warn");
                            state.Set(0, 0);
                        }
                        else if (msg.StartsWith("M "))
                        {
                            // Block motor commands if motors are inhibited (ROAMING or OFFLINE)
                            if (safetyMachine.AreMotorsInhibited())
                            {
                                // Log when motor commands are blocked due to safety inhibit
                                var (_, _, stopReason) = safetyMachine.GetStatus();
                                logService.Publish("safety", "Motor command blocked", $"safety inhibit: {stopReason}", "warn");
                                // Silently ignore motor commands during safety inhibit
                                // The state machine already set motors to 0
                                continue;
                            }

                            var parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 3 &&
                                int.TryParse(parts[1], out var l) &&
                                int.TryParse(parts[2], out var r))
                            {
                                state.Set(Clamp(l), Clamp(r));
                            }
                        }
                        else if (msg.StartsWith("H "))
                        {
                            var parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 2 && int.TryParse(parts[1], out var state_val))
                            {
                                var on = state_val == 1;
                                logService.Publish("gpio", $"Headlight {(on ? "ON" : "OFF")}", null, "info");
                                gpio.SetHeadlight(on);
                            }
                        }
                        else if (msg.StartsWith("I "))
                        {
                            var parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 2 && int.TryParse(parts[1], out var state_val))
                            {
                                var on = state_val == 1;
                                logService.Publish("gpio", $"IR LED {(on ? "ON" : "OFF")}", null, "info");
                                gpio.SetIrLed(on);
                            }
                        }
                        else if (msg == "HORN_START")
                        {
                            // Start continuous horn sound (1000Hz sine wave)
                            try
                            {
                                logService.Publish("audio", "Horn started", null, "info");
                                audioPlayback.StartHorn();
                            }
                            catch (Exception ex)
                            {
                                logService.Publish("audio", "Horn start failed", ex.Message, "error");
                                Console.WriteLine($"Error starting horn: {ex.Message}");
                            }
                        }
                        else if (msg == "HORN_STOP")
                        {
                            // Stop horn sound
                            try
                            {
                                logService.Publish("audio", "Horn stopped", null, "info");
                                audioPlayback.StopHorn();
                            }
                            catch (Exception ex)
                            {
                                logService.Publish("audio", "Horn stop failed", ex.Message, "error");
                                Console.WriteLine($"Error stopping horn: {ex.Message}");
                            }
                        }
                    }
                }
            }
            finally
            {
                var wasOperator = opManager.IsOperator(clientId);
                opManager.UnregisterClient(clientId);
                wsManager.RemoveClient(clientId);

                // If operator disconnected, notify all clients
                if (wasOperator)
                {
                    logService.Publish("ops", $"{clientName} disconnected", "rover stopped", "warn");
                    state.Set(0, 0); // Stop rover
                    foreach (var id in wsManager.GetAllClientIds())
                    {
                        await SendRoleStatus(id);
                    }
                }

                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                catch { /* ignore */ }
            }
        });
    }

    private static int Clamp(int v) => Math.Max(-255, Math.Min(255, v));
}
