using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Linq;
using System.Net.Http;

static class VideoProxyEndpoint
{
    public static void MapVideoProxyEndpoint(this IEndpointRouteBuilder app)
    {
        app.Map("/cam/{*path}", async (HttpContext context, IHttpClientFactory clientFactory) =>
        {
            var client = clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromHours(1); // Allow long-running streams

            var path = context.GetRouteValue("path")?.ToString() ?? "";
            var targetUrl = $"http://127.0.0.1:8889/cam/{path}{context.Request.QueryString}";

            var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUrl);

            // Forward the request body (important for WHEP/WebRTC POST requests)
            if (HttpMethods.IsPost(context.Request.Method) ||
                HttpMethods.IsPut(context.Request.Method) ||
                HttpMethods.IsPatch(context.Request.Method))
            {
                var memoryStream = new MemoryStream();
                await context.Request.Body.CopyToAsync(memoryStream);
                memoryStream.Position = 0;
                request.Content = new StreamContent(memoryStream);
            }

            foreach (var header in context.Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue; // Handled by StreamContent

                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                {
                    request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            var hopByHopHeaders = new[] { "Connection", "Transfer-Encoding", "Keep-Alive", "Upgrade", "Proxy-Authenticate", "Proxy-Authorization", "Trailer", "TE" };

            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                Console.WriteLine($"Proxy: {context.Request.Method} {path} -> {response.StatusCode}");
                context.Response.StatusCode = (int)response.StatusCode;

                foreach (var header in response.Headers)
                {
                    if (hopByHopHeaders.Any(h => h.Equals(header.Key, StringComparison.OrdinalIgnoreCase))) continue;
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }
                foreach (var header in response.Content.Headers)
                {
                    if (hopByHopHeaders.Any(h => h.Equals(header.Key, StringComparison.OrdinalIgnoreCase))) continue;
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"Video proxy error: {ex.Message}");
            }
        });
    }
}
