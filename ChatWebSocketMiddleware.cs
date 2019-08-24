using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using System.Linq;

namespace MyWebApi.Controllers
{

    class WebSocketChatMessage
    {
        public string username;
        public string message;
    }

    class WebSocketConnectionMessage
    {
        public string username;
        public bool left;
    }

    /// <summary>
    /// Handles incoming websocket connections
    /// 
    /// TODO: 
    ///   - implement some kind of heartbeat to prune dead clients periodically
    /// </summary>
    public class ChatWebSocketMiddleware
    {
        private static ConcurrentDictionary<string, WebSocket> _sockets = new ConcurrentDictionary<string, WebSocket>();

        private readonly RequestDelegate _next;

        private bool isLoggedIn = false;

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
            await HandleWebSocket(currentSocket, ct);

            await currentSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
            currentSocket.Dispose();
        }

        private static async Task HandleWebSocket(WebSocket currentSocket, CancellationToken ct = default(CancellationToken))
        {
            // register socket before authentication so that guests receive updates
            var socketId = Guid.NewGuid().ToString();
            _sockets.TryAdd(socketId, currentSocket);

            // however, first message HAS to be authenticating
            // handle some kind of authentication username:secret
            string initialMessage = await ReceiveStringAsync(currentSocket, ct), userSecret;
            string[] auth = initialMessage.Split(':', 2);

            if (auth.Length < 2 || !AuthController.users.TryGetValue(auth[0], out userSecret))
                return; // invalid format or unknown user

            if (auth[1] != userSecret)
                return; // wrong token

            // invalidates session when connecting to avoid double connection
            string newPass = Guid.NewGuid().ToString();
            AuthController.users.TryUpdate(auth[0], newPass, userSecret);

            string username = auth[0];

            await NotifyUserConnection(username, false, ct);

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

                    await BroadcastMessage(username, response, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            await NotifyUserConnection(username, true);

            WebSocket dummy;
            _sockets.TryRemove(socketId, out dummy);

            // Removes session
            string key;
            AuthController.users.TryRemove(username, out key);
        }

        private static Task BroadcastMessage(string username, string message, CancellationToken ct = default(CancellationToken))
        {
            return SendStringToAllAsync(JsonConvert.SerializeObject(new WebSocketChatMessage()
            {
                username = username,
                message = message,
            }), ct);
        }

        private static Task NotifyUserConnection(string username, bool left, CancellationToken ct = default(CancellationToken))
        {
            return SendStringToAllAsync(JsonConvert.SerializeObject(new WebSocketConnectionMessage()
            {
                username = username,
                left = left,
            }));
        }

        private static async Task SendStringAsync(WebSocket socket, string data, CancellationToken ct = default(CancellationToken))
        {
            var buffer = Encoding.UTF8.GetBytes(data);
            var segment = new ArraySegment<byte>(buffer);
            try
            {
                await socket.SendAsync(segment, WebSocketMessageType.Text, true, ct);
            }
            catch (WebSocketException)
            {
                // handle failure?
            }
        }

        private static Task SendStringToAllAsync(string data, CancellationToken ct = default(CancellationToken))
        {
            return Task.WhenAll(_sockets.Values.Select(socket => SendStringAsync(socket, data, ct)));
        }

        private static async Task<string> ReceiveStringAsync(WebSocket socket, CancellationToken ct = default(CancellationToken))
        {
            var buffer = new ArraySegment<byte>(new byte[8192]);
            using (var ms = new MemoryStream())
            {
                WebSocketReceiveResult result;
                try
                {
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
                }
                catch (WebSocketException)
                { // handle failure?
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