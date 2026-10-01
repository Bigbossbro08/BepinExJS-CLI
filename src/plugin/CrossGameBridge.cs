using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using Jint;
using Jint.Native;
using Jint.Native.Function;

namespace BepinExJS.Plugin
{
    public class CrossGameSessionInfo
    {
        public int Pid { get; set; }
        public string ProcessName { get; set; } = "";
        public string GameTitle { get; set; } = "";
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; }
    }

    public class CrossGameJsWrapper
    {
        private readonly CrossGameBridge _bridge;

        public CrossGameJsWrapper(CrossGameBridge bridge)
        {
            _bridge = bridge;
        }

        public void on(string eventName, JsValue callback) => _bridge.On(eventName, callback);
        public void emit(string eventName, JsValue data) => _bridge.Emit(eventName, data);
        public object[] getGames()
        {
            var sessions = _bridge.DiscoverOtherGameSessions();
            var list = new List<object>();
            foreach (var s in sessions)
            {
                list.Add(new Dictionary<string, object>
                {
                    ["pid"] = s.Pid,
                    ["gameTitle"] = s.GameTitle,
                    ["processName"] = s.ProcessName,
                    ["port"] = s.Port
                });
            }
            return list.ToArray();
        }
    }

    public class CrossGameBridge : IDisposable
    {
        private readonly ManualLogSource _logger;
        private Engine _engine;           // non-readonly so hot reloads can re-point it
        private readonly Action<Action> _runOnMainThread;
        private readonly ConcurrentDictionary<string, List<Function>> _listeners = new ConcurrentDictionary<string, List<Function>>();
        private readonly ConcurrentDictionary<int, ClientWebSocket> _activeClientSockets = new ConcurrentDictionary<int, ClientWebSocket>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly int _currentPid;

        public CrossGameBridge(ManualLogSource logger, Engine engine, Action<Action> runOnMainThread)
        {
            _logger = logger;
            _engine = engine;
            _runOnMainThread = runOnMainThread;
            _currentPid = Process.GetCurrentProcess().Id;
        }

        /// <summary>
        /// Re-points the bridge at a new Jint engine after a hot reload.
        /// JS event listener registrations are cleared since the old closures are invalid.
        /// </summary>
        public void UpdateEngine(Engine newEngine)
        {
            _engine = newEngine;
            // Clear old JS listeners — they held closures bound to the previous engine
            _listeners.Clear();
        }

        public void On(string eventName, JsValue callback)
        {
            if (callback is Function fn)
            {
                var list = _listeners.GetOrAdd(eventName, _ => new List<Function>());
                lock (list)
                {
                    list.Add(fn);
                }
            }
        }

        public void Emit(string eventName, JsValue data)
        {
            string dataJson = "null";
            try
            {
                var stringify = _engine.Evaluate("JSON.stringify") as Function;
                var res = stringify?.Call(JsValue.Undefined, new[] { data });
                if (res != null) dataJson = res.ToString();
            }
            catch
            {
                dataJson = $"\"{EscapeJson(data.ToString())}\"";
            }

            var payload = $"{{\"type\":\"cross_game_event\",\"event\":\"{EscapeJson(eventName)}\",\"sourcePid\":{_currentPid},\"data\":{dataJson}}}";

            // Broadcast to other games in background
            Task.Run(async () =>
            {
                var otherSessions = DiscoverOtherGameSessions();
                foreach (var s in otherSessions)
                {
                    try
                    {
                        await SendToGameSession(s, payload);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"[CrossGame] Failed sending to {s.GameTitle} (PID: {s.Pid}): {ex.Message}");
                    }
                }
            });
        }

        public void HandleIncomingEvent(string eventName, string dataJson, int sourcePid)
        {
            if (_listeners.TryGetValue(eventName, out var list))
            {
                _runOnMainThread(() =>
                {
                    JsValue parsedData = JsValue.Undefined;
                    try
                    {
                        var parse = _engine.Evaluate("JSON.parse") as Function;
                        var res = parse?.Call(JsValue.Undefined, new[] { (JsValue)dataJson });
                        if (res != null) parsedData = res;
                    }
                    catch
                    {
                        parsedData = (JsValue)dataJson;
                    }

                    List<Function> snapshot;
                    lock (list)
                    {
                        snapshot = new List<Function>(list);
                    }

                    foreach (var fn in snapshot)
                    {
                        try
                        {
                            fn.Call(JsValue.Undefined, new[] { parsedData, (JsValue)sourcePid });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"[CrossGame] Error in event listener for '{eventName}': {ex.Message}");
                        }
                    }
                });
            }
        }

        public List<CrossGameSessionInfo> DiscoverOtherGameSessions()
        {
            var result = new List<CrossGameSessionInfo>();
            var sessionsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bepinexjs", "sessions");
            if (!Directory.Exists(sessionsDir)) return result;

            foreach (var file in Directory.GetFiles(sessionsDir, "*.json"))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var pid = ExtractInt(text, "pid");
                    if (pid == _currentPid || pid <= 0) continue;

                    // Verify process is alive
                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        if (proc.HasExited) continue;
                    }
                    catch
                    {
                        continue;
                    }

                    var port = ExtractInt(text, "port");
                    var host = ExtractString(text, "host") ?? "127.0.0.1";
                    var gameTitle = ExtractString(text, "gameTitle") ?? "Unity Game";
                    var processName = ExtractString(text, "processName") ?? "";

                    result.Add(new CrossGameSessionInfo
                    {
                        Pid = pid,
                        GameTitle = gameTitle,
                        ProcessName = processName,
                        Host = host,
                        Port = port
                    });
                }
                catch { }
            }

            return result;
        }

        private async Task SendToGameSession(CrossGameSessionInfo session, string payload)
        {
            var socket = _activeClientSockets.GetOrAdd(session.Pid, _ => new ClientWebSocket());

            if (socket.State != WebSocketState.Open)
            {
                if (socket.State != WebSocketState.None)
                {
                    try { socket.Dispose(); } catch { }
                    socket = new ClientWebSocket();
                    _activeClientSockets[session.Pid] = socket;
                }

                var uri = new Uri($"ws://{session.Host}:{session.Port}");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await socket.ConnectAsync(uri, cts.Token);
            }

            var bytes = Encoding.UTF8.GetBytes(payload);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
        }

        private static int ExtractInt(string json, string field)
        {
            var key = $"\"{field}\":";
            var idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx == -1) return 0;

            idx += key.Length;
            while (idx < json.Length && char.IsWhiteSpace(json[idx])) idx++;

            var start = idx;
            while (idx < json.Length && (char.IsDigit(json[idx]) || json[idx] == '-')) idx++;

            if (int.TryParse(json.Substring(start, idx - start), out var val)) return val;
            return 0;
        }

        private static string? ExtractString(string json, string field)
        {
            var key = $"\"{field}\":";
            var idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx == -1) return null;

            idx = json.IndexOf('"', idx + key.Length);
            if (idx == -1) return null;

            var end = json.IndexOf('"', idx + 1);
            if (end == -1) return null;

            return json.Substring(idx + 1, end - idx - 1);
        }

        private static string EscapeJson(string str)
        {
            if (str == null) return "";
            return str
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        public void Dispose()
        {
            _cts.Cancel();
            foreach (var kvp in _activeClientSockets)
            {
                try
                {
                    kvp.Value.Dispose();
                }
                catch { }
            }
            _activeClientSockets.Clear();
            _listeners.Clear();
        }
    }
}
