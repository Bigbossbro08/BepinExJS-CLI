using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using Fleck;
using UnityEngine;

namespace BepinExJS.Plugin
{
    [BepInPlugin("com.bepinexjs.plugin", "BepinExJS Plugin", "1.0.0")]
    public class BepinExJSPlugin : BaseUnityPlugin
    {
        static BepinExJSPlugin()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    var assemblyName = new System.Reflection.AssemblyName(args.Name).Name + ".dll";
                    var pluginDir = Path.GetDirectoryName(typeof(BepinExJSPlugin).Assembly.Location);
                    if (!string.IsNullOrEmpty(pluginDir))
                    {
                        var targetPath = Path.Combine(pluginDir, assemblyName);
                        if (File.Exists(targetPath))
                        {
                            return System.Reflection.Assembly.LoadFrom(targetPath);
                        }
                    }
                }
                catch { }
                return null;
            };
        }

        private WsServer? _wsServer;
        private JsRuntimeManager? _runtimeManager;
        private ModConfigManager? _configManager;
        private GameSessionRegistry? _sessionRegistry;
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        private void Awake()
        {
            Logger.LogInfo("Initializing BepinExJS Runtime...");

            // 1. Load BepInExJS.json from GameRootPath
            _configManager = new ModConfigManager(Paths.GameRootPath, Logger);

            if (!_configManager.Enabled)
            {
                Logger.LogWarning("[BepinExJS] Disabled via BepInExJS.json. Skipping initialization.");
                return;
            }

            _runtimeManager = new JsRuntimeManager(Logger, (level, message) =>
            {
                // Relay log to connected CLI clients
                var payload = $"{{\"type\":\"log\",\"level\":\"{EscapeJson(level)}\",\"message\":\"{EscapeJson(message)}\"}}";
                _wsServer?.Broadcast(payload);
            }, this, action => _mainThreadQueue.Enqueue(action));

            try
            {
                _wsServer = new WsServer(_configManager.Host, _configManager.Port, HandleClientMessage, msg => Logger.LogInfo(msg));

                // Register session in ~/.bepinexjs/sessions/<PID>.json
                _sessionRegistry = new GameSessionRegistry(Logger);
                _sessionRegistry.RegisterSession(_wsServer.BoundPort, _configManager.Host);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to start WebSocket server: {ex.Message}");
            }

            // 2. Execute all startup mod files resolved from BepInExJS.json
            var startupFiles = _configManager.ResolveAllStartupModFiles();
            foreach (var scriptPath in startupFiles)
            {
                try
                {
                    var modDir = Path.GetDirectoryName(scriptPath);
                    Logger.LogInfo($"[Startup] Autoloading mod: {Path.GetFileName(scriptPath)} from {modDir}");
                    var code = File.ReadAllText(scriptPath);
                    _runtimeManager.ExecuteStartupScript(code, scriptPath, modDir);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[Startup] Failed to load {Path.GetFileName(scriptPath)}: {ex.Message}");
                }
            }
        }

        private long _lastAppliedGeneration = 0;

        /// <summary>
        /// Holds the most recent pending reload request. Only the latest one is ever executed.
        /// Written from background WS threads; read and cleared on the Unity main thread in Update().
        /// </summary>
        private struct PendingReload
        {
            public string Code;
            public string Path;
            public string? ModDir;
            public long Generation;
            public IWebSocketConnection Socket;
        }
        private PendingReload? _pendingReload = null;
        private readonly object _pendingReloadLock = new object();

        private void HandleClientMessage(string rawMessage, IWebSocketConnection socket)
        {
            // Extract the "type" field once and dispatch via switch — handles any key ordering / whitespace
            var msgType = ExtractJsonField(rawMessage, "type");
            if (string.IsNullOrEmpty(msgType)) return;

            switch (msgType)
            {
                case "reload":
                {
                    var code = ExtractJsonField(rawMessage, "code");
                    var path = ExtractJsonField(rawMessage, "path");
                    if (string.IsNullOrEmpty(path)) path = "mod.bundle.js";
                    var modDir = ExtractJsonField(rawMessage, "modDir");
                    var genStr = ExtractJsonField(rawMessage, "generation");
                    long generation = 0;
                    if (!string.IsNullOrEmpty(genStr)) long.TryParse(genStr, out generation);

                    // Latest-wins: overwrite any previously queued but unexecuted reload
                    lock (_pendingReloadLock)
                    {
                        _pendingReload = new PendingReload
                        {
                            Code = code,
                            Path = path,
                            ModDir = modDir,
                            Generation = generation,
                            Socket = socket
                        };
                    }
                    break;
                }

                case "eval":
                {
                    var code = ExtractJsonField(rawMessage, "code");
                    var id = ExtractJsonField(rawMessage, "id") ?? "";
                    _mainThreadQueue.Enqueue(() =>
                    {
                        var result = _runtimeManager?.ExecuteRepl(code) ?? "Error: Runtime not initialized";
                        try
                        {
                            if (socket.IsAvailable)
                                socket.Send($"{{\"type\":\"eval_result\",\"id\":\"{EscapeJson(id)}\",\"result\":\"{EscapeJson(result)}\"}}");
                        }
                        catch { }
                    });
                    break;
                }

                case "dump_types":
                {
                    var assemblyName = ExtractJsonField(rawMessage, "assembly");
                    var assembliesField = ExtractJsonField(rawMessage, "assemblies");
                    var splitField = ExtractJsonField(rawMessage, "split");
                    bool split = splitField != null && (splitField.Equals("true", StringComparison.OrdinalIgnoreCase) || splitField.Equals("1"));

                    _mainThreadQueue.Enqueue(() =>
                    {
                        try
                        {
                            string[] targets;
                            if (!string.IsNullOrEmpty(assembliesField))
                            {
                                targets = assembliesField.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                                         .Select(s => s.Trim())
                                                         .ToArray();
                            }
                            else if (!string.IsNullOrEmpty(assemblyName))
                            {
                                targets = new[] { assemblyName.Trim() };
                            }
                            else
                            {
                                targets = new[] { "Assembly-CSharp" };
                            }

                            Logger.LogInfo($"[TypeGen] Extracting C# types for assemblies (split={split}): {string.Join(", ", targets)}...");
                            var perAssembly = TypeDefGenerator.GenerateDtsPerAssembly(targets);

                            var sb = new StringBuilder();
                            sb.Append("{\"type\":\"dump_types_result\",\"split\":true,\"files\":{");
                            int count = 0;
                            foreach (var kvp in perAssembly)
                            {
                                if (count > 0) sb.Append(",");
                                sb.Append($"\"{EscapeJson(kvp.Key)}\":\"{EscapeJson(kvp.Value)}\"");
                                count++;
                            }
                            sb.Append("},");

                            var combinedDts = TypeDefGenerator.GenerateDtsForLoadedAssemblies(targets);
                            sb.Append($"\"dts\":\"{EscapeJson(combinedDts)}\"}}");

                            Logger.LogInfo($"[TypeGen] Generated types for {perAssembly.Count} assemblies ({combinedDts.Length} chars). Sending to CLI...");
                            socket.Send(sb.ToString());
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"[TypeGen] Error dumping types: {ex}");
                            socket.Send($"{{\"type\":\"dump_types_result\",\"error\":\"{EscapeJson(ex.Message)}\"}}");
                        }
                    });
                    break;
                }

                case "list_assemblies":
                {
                    _mainThreadQueue.Enqueue(() =>
                    {
                        try
                        {
                            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                            var sb = new System.Text.StringBuilder();
                            sb.Append("{\"type\":\"list_assemblies_result\",\"assemblies\":[");

                            for (int i = 0; i < assemblies.Length; i++)
                            {
                                var a = assemblies[i];
                                var name = a.GetName().Name ?? "Unknown";
                                string location = "";
                                try { location = a.Location ?? ""; } catch { }

                                if (i > 0) sb.Append(",");
                                sb.Append($"{{\"name\":\"{EscapeJson(name)}\",\"location\":\"{EscapeJson(location)}\"}}");
                            }

                            sb.Append("]}");
                            socket.Send(sb.ToString());
                        }
                        catch (Exception ex)
                        {
                            socket.Send($"{{\"type\":\"list_assemblies_result\",\"error\":\"{EscapeJson(ex.Message)}\"}}");
                        }
                    });
                    break;
                }

                case "unload":
                    _mainThreadQueue.Enqueue(() =>
                    {
                        Logger.LogInfo("[BepinExJS] Unload requested by CLI. Cleaning up mod...");
                        _runtimeManager?.Teardown();
                        try { socket.Send("{\"type\":\"unload_ack\",\"status\":\"ok\"}"); } catch { }
                    });
                    break;

                case "ping":
                    try { socket.Send("{\"type\":\"pong\"}"); } catch { }
                    break;

                case "handshake":
                {
                    var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                    var gameTitle = Application.productName;
                    if (string.IsNullOrEmpty(gameTitle)) gameTitle = currentProcess.ProcessName;

                    var reply = $@"{{
  ""type"": ""handshake_ack"",
  ""pid"": {currentProcess.Id},
  ""gameTitle"": ""{EscapeJson(gameTitle)}"",
  ""processName"": ""{EscapeJson(currentProcess.ProcessName)}"",
  ""unityVersion"": ""{EscapeJson(Application.unityVersion)}"",
  ""bepInVersion"": ""5.4.21"",
  ""status"": ""ok""
}}";
                    socket.Send(reply);
                    break;
                }

                case "cross_game_event":
                {
                    var eventName = ExtractJsonField(rawMessage, "event") ?? "";
                    var pidStr = ExtractJsonField(rawMessage, "sourcePid");
                    int sourcePid = 0;
                    if (!string.IsNullOrEmpty(pidStr)) int.TryParse(pidStr, out sourcePid);

                    // Extract raw data object/string
                    var dataStart = rawMessage.IndexOf("\"data\":", StringComparison.OrdinalIgnoreCase);
                    string dataJson = "{}";
                    if (dataStart != -1)
                    {
                        dataStart += 7;
                        while (dataStart < rawMessage.Length && char.IsWhiteSpace(rawMessage[dataStart])) dataStart++;
                        var dataEnd = rawMessage.LastIndexOf('}');
                        if (dataEnd > dataStart)
                            dataJson = rawMessage.Substring(dataStart, dataEnd - dataStart).Trim();
                    }

                    _runtimeManager?.CrossGame?.HandleIncomingEvent(eventName, dataJson, sourcePid);
                    break;
                }

                default:
                    Logger.LogWarning($"[BepinExJS] Unknown message type: {msgType}");
                    break;
            }
        }

        private void Update()
        {
            // Execute latest-wins pending reload (coalesces multiple queued reloads into one)
            PendingReload? pending = null;
            lock (_pendingReloadLock)
            {
                if (_pendingReload.HasValue)
                {
                    pending = _pendingReload;
                    _pendingReload = null;
                }
            }

            if (pending.HasValue)
            {
                var r = pending.Value;

                if (r.Generation > 0 && r.Generation < _lastAppliedGeneration)
                {
                    Logger.LogWarning($"[BepinExJS] Discarding stale reload generation {r.Generation} (current: {_lastAppliedGeneration})");
                    try { if (r.Socket.IsAvailable) r.Socket.Send($"{{\"type\":\"reload_ack\",\"status\":\"ignored\",\"generation\":{r.Generation}}}"); } catch { }
                }
                else
                {
                    if (r.Generation > 0) _lastAppliedGeneration = r.Generation;

                    var result = _runtimeManager?.Reload(r.Code, r.Path, r.ModDir);
                    try
                    {
                        if (r.Socket.IsAvailable)
                        {
                            if (result.HasValue && result.Value.Success)
                                r.Socket.Send($"{{\"type\":\"reload_ack\",\"status\":\"ok\",\"generation\":{r.Generation}}}");
                            else
                            {
                                var errorMsg = EscapeJson(result?.Error ?? "Runtime error");
                                r.Socket.Send($"{{\"type\":\"reload_ack\",\"status\":\"error\",\"generation\":{r.Generation},\"message\":\"{errorMsg}\"}}");
                            }
                        }
                    }
                    catch (Exception ex) { Logger.LogWarning($"[BepinExJS] Failed to send reload_ack: {ex.Message}"); }
                }
            }

            // Execute main-thread tasks (eval, unload, dump_types, etc.)
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error in main thread action: {ex}");
                }
            }

            _runtimeManager?.TickUpdate();
        }

        private void FixedUpdate()
        {
            _runtimeManager?.TickFixedUpdate();
        }

        private void OnGUI()
        {
            _runtimeManager?.TickGUI();
        }

        private void OnDestroy()
        {
            _sessionRegistry?.Dispose();
            _wsServer?.Dispose();
            _runtimeManager?.Dispose();
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

        private static string ExtractJsonField(string json, string field)
        {
            var key = $"\"{field}\"";
            var idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx == -1) return "";

            var colonIdx = json.IndexOf(':', idx + key.Length);
            if (colonIdx == -1) return "";

            // Skip whitespace after colon
            int start = colonIdx + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length) return "";

            char first = json[start];

            // Numeric value (integer or float, possibly negative)
            if (char.IsDigit(first) || first == '-')
            {
                int end = start;
                while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-' || json[end] == '+' || json[end] == 'e' || json[end] == 'E'))
                    end++;
                return json.Substring(start, end - start);
            }

            // Boolean or null (true / false / null)
            if (first == 't' || first == 'f' || first == 'n')
            {
                int end = start;
                while (end < json.Length && char.IsLetter(json[end])) end++;
                return json.Substring(start, end - start);
            }

            // Quoted string — parse with escape handling
            if (first == '"')
            {
                var sb = new System.Text.StringBuilder();
                bool escaped = false;
                for (int i = start + 1; i < json.Length; i++)
                {
                    char c = json[i];
                    if (escaped)
                    {
                        switch (c)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            default: sb.Append(c); break;
                        }
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        break;
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                return sb.ToString();
            }

            return "";
        }
    }
}
