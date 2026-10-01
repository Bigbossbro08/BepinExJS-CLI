using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Fleck;

namespace BepinExJS.Plugin
{
    public class WsServer : IDisposable
    {
        private readonly WebSocketServer _server;
        private readonly List<IWebSocketConnection> _clients = new List<IWebSocketConnection>();
        private readonly Action<string, IWebSocketConnection> _onMessageReceived;
        private readonly Action<string> _log;

        public int BoundPort { get; private set; }

        public int ConnectedClientsCount
        {
            get
            {
                lock (_clients)
                {
                    return _clients.Count;
                }
            }
        }

        public WsServer(string ip, int preferredPort, Action<string, IWebSocketConnection> onMessageReceived, Action<string> log)
        {
            _onMessageReceived = onMessageReceived;
            _log = log;

            // Fleck setup
            FleckLog.LogAction = (level, message, ex) => { }; // silence internal fleck logs

            int port = preferredPort;
            const int maxAttempts = 20;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    // Pre-check if port is available on this IP
                    var parsedIp = IPAddress.Parse(ip);
                    var tester = new TcpListener(parsedIp, port);
                    tester.ExclusiveAddressUse = true;
                    tester.Start();
                    tester.Stop();

                    var server = new WebSocketServer($"ws://{ip}:{port}");
                    server.Start(socket =>
                    {
                        socket.OnOpen = () =>
                        {
                            lock (_clients)
                            {
                                _clients.Add(socket);
                            }
                            _log($"[WsServer] Client connected: {socket.ConnectionInfo.ClientIpAddress}:{socket.ConnectionInfo.ClientPort}");
                        };

                socket.OnClose = () =>
                {
                    lock (_clients)
                    {
                        _clients.Remove(socket);
                    }
                    _log($"[WsServer] Client disconnected: {socket.ConnectionInfo.ClientIpAddress}");
                };

                socket.OnMessage = message =>
                {
                    try
                    {
                        _onMessageReceived?.Invoke(message, socket);
                    }
                    catch (Exception ex)
                    {
                        _log($"[WsServer] Error handling message: {ex.Message}");
                    }
                };

                        socket.OnError = ex =>
                        {
                            _log($"[WsServer] Socket error: {ex.Message}");
                        };
                    });

                    _server = server;
                    BoundPort = port;
                    _log($"[WsServer] Started listening on ws://{ip}:{port}");
                    return;
                }
                catch (Exception ex)
                {
                    _log($"[WsServer] Port {port} unavailable: {ex.Message}. Trying next port...");
                    port++;
                }
            }

            throw new InvalidOperationException($"[WsServer] Could not bind WebSocket server to any port between {preferredPort} and {preferredPort + maxAttempts - 1}.");
        }

        public void Broadcast(string message)
        {
            List<IWebSocketConnection> snapshot;
            lock (_clients)
            {
                snapshot = new List<IWebSocketConnection>(_clients);
            }

            foreach (var client in snapshot)
            {
                try
                {
                    if (client.IsAvailable)
                    {
                        client.Send(message);
                    }
                }
                catch
                {
                    // Ignore transient send failures
                }
            }
        }

        public void Dispose()
        {
            lock (_clients)
            {
                foreach (var client in _clients)
                {
                    try { client.Close(); } catch { }
                }
                _clients.Clear();
            }

            try
            {
                _server?.Dispose();
            }
            catch { }
        }
    }
}
