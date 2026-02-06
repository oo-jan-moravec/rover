using Microsoft.AspNetCore.Routing;
using System.Linq;

static class WifiMetricsEndpoint
{
    public static void MapWifiMetricsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/wifi/metrics", async (WifiState wifiState, WifiMonitor wifiMonitor) =>
        {
            var (rssi, bssid, ssid, freq, txMbps, rxMbps, connected, lastUpdate, lastRoam, avgRtt) = wifiState.Get();
            var nearbyAps = await wifiMonitor.GetNearbyApsAsync(ssid);

            return Results.Json(new
            {
                currentConnection = new
                {
                    ssid,
                    bssid,
                    rssiDbm = rssi,
                    freqMhz = freq,
                    txBitrateMbps = txMbps,
                    rxBitrateMbps = rxMbps,
                    connected,
                    avgRttMs = avgRtt,
                    lastUpdated = lastUpdate.ToString("o"),
                    lastRoam = lastRoam != DateTime.MinValue ? lastRoam.ToString("o") : null
                },
                nearbyAps = nearbyAps.Select(ap => new
                {
                    bssid = ap.Bssid,
                    ssid = ap.Ssid,
                    rssiDbm = ap.RssiDbm
                })
            });
        });
    }
}
