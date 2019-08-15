using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace MyWebApi.Controllers
{
    public class ChatWebSocketMiddleware
    {
        private static ConcurrentDictionary<string, WebSocket> _sockets = new ConcurrentDictionary<string, WebSocket>();

        private readonly RequestDelegate _next;

        public ChatWebSocketMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task Invoke(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                await _next.Invoke(context);
                return;
            }

            CancellationToken ct = context.RequestAborted;
            WebSocket currentSocket = await context.WebSockets.AcceptWebSocketAsync();

            // handle some kind of authentication username:secret
            string initialMessage = await ReceiveStringAsync(currentSocket, ct), userSecret;
            string[] auth = initialMessage.Split(':', 2);

            if (auth.Length >= 2 && AuthController.users.TryGetValue(auth[0], out userSecret))
                if (auth[1] == userSecret)
                {
                    // invalidates session when connecting to avoid double connection
                    string newPass = Guid.NewGuid().ToString();
                    AuthController.users.TryUpdate(auth[0], newPass, userSecret);
                    await HandleWebSocket(currentSocket, auth[0], ct);
                }

            await currentSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
            currentSocket.Dispose();
        }

        private static async Task HandleWebSocket(WebSocket currentSocket, string username, CancellationToken ct = default(CancellationToken))
        {
            var socketId = Guid.NewGuid().ToString();
            _sockets.TryAdd(socketId, currentSocket);

            await SendStringToAllAsync($"*{username} joined the chat*", ct);

            while (true)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    var response = await ReceiveStringAsync(currentSocket, ct);
                    if (string.IsNullOrEmpty(response))
                    {
                        if (currentSocket.State != WebSocketState.Open)
                            break;

                        continue;
                    }

                    await SendStringToAllAsync($"{username}: {response}", ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            await SendStringToAllAsync($"*{username} left the chat*");

            WebSocket dummy;
            _sockets.TryRemove(socketId, out dummy);

            // Removes session
            string key;
            AuthController.users.TryRemove(username, out key);
        }

        private static Task SendStringAsync(WebSocket socket, string data, CancellationToken ct = default(CancellationToken))
        {
            var buffer = Encoding.UTF8.GetBytes(data);
            var segment = new ArraySegment<byte>(buffer);
            return socket.SendAsync(segment, WebSocketMessageType.Text, true, ct);
        }

        private static async Task SendStringToAllAsync(string data, CancellationToken ct = default(CancellationToken))
        {
            foreach (var socket in _sockets)
            {
                if (socket.Value.State != WebSocketState.Open)
                {
                    continue;
                }

                await SendStringAsync(socket.Value, data, ct);
            }

        }

        private static async Task<string> ReceiveStringAsync(WebSocket socket, CancellationToken ct = default(CancellationToken))
        {
            var buffer = new ArraySegment<byte>(new byte[8192]);
            using (var ms = new MemoryStream())
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    ms.Write(buffer.Array, buffer.Offset, result.Count);
                }
                while (!result.EndOfMessage);

                ms.Seek(0, SeekOrigin.Begin);
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    return null;
                }

                // Encoding UTF8: https://tools.ietf.org/html/rfc6455#section-5.6
                using (var reader = new StreamReader(ms, Encoding.UTF8))
                {
                    return await reader.ReadToEndAsync();
                }
            }
        }
    }
}