using System;
using System.Collections.Concurrent;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using Fleck;
using UnityEngine;

namespace BepinExJS.Plugin
{
    [BepInPlugin("com.bepinexjs.plugin", "BepinExJS Plugin", "1.0.0")]
    public class BepinExJSPlugin : BaseUnityPlugin
    {
        private ConfigEntry<int>? _portConfig;
        private ConfigEntry<string>? _hostConfig;
        private ConfigEntry<string>? _autoLoadScriptConfig;

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
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        private void Awake()
        {
            _portConfig = Config.Bind("Server", "Port", 9092, "WebSocket server port for hot-reload CLI");
            _hostConfig = Config.Bind("Server", "Host", "127.0.0.1", "WebSocket server host");
            _autoLoadScriptConfig = Config.Bind("Scripts", "AutoLoad", "BepInEx/scripts/bundle.js", "Path to script to autoload on game start");

            Logger.LogInfo("Initializing BepinExJS Runtime...");

            _runtimeManager = new JsRuntimeManager(Logger, (level, message) =>
            {
                // Relay log to connected CLI clients
                var payload = $"{{\"type\":\"log\",\"level\":\"{EscapeJson(level)}\",\"message\":\"{EscapeJson(message)}\"}}";
                _wsServer?.Broadcast(payload);
            });

            try
            {
                _wsServer = new WsServer(_hostConfig.Value, _portConfig.Value, HandleClientMessage, msg => Logger.LogInfo(msg));
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to start WebSocket server: {ex.Message}");
            }

            // Check if autoload script exists
            var fullPath = Path.Combine(Paths.GameRootPath, _autoLoadScriptConfig.Value);
            if (File.Exists(fullPath))
            {
                try
                {
                    var code = File.ReadAllText(fullPath);
                    _runtimeManager.Reload(code, Path.GetFileName(fullPath));
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to load autoload script: {ex.Message}");
                }
            }
        }

        private void HandleClientMessage(string rawMessage, IWebSocketConnection socket)
        {
            // Simple parsing to avoid extra dependencies
            if (rawMessage.Contains("\"type\":\"reload\"") || rawMessage.Contains("\"type\": \"reload\""))
            {
                var code = ExtractJsonField(rawMessage, "code");
                var path = ExtractJsonField(rawMessage, "path") ?? "mod.bundle.js";

                _mainThreadQueue.Enqueue(() =>
                {
                    _runtimeManager?.Reload(code, path);
                    socket.Send("{\"type\":\"reload_ack\",\"status\":\"ok\"}");
                });
            }
            else if (rawMessage.Contains("\"type\":\"eval\"") || rawMessage.Contains("\"type\": \"eval\""))
            {
                var code = ExtractJsonField(rawMessage, "code");
                var id = ExtractJsonField(rawMessage, "id") ?? "";

                _mainThreadQueue.Enqueue(() =>
                {
                    var result = _runtimeManager?.ExecuteRepl(code) ?? "Error: Runtime not initialized";
                    socket.Send($"{{\"type\":\"eval_result\",\"id\":\"{EscapeJson(id)}\",\"result\":\"{EscapeJson(result)}\"}}");
                });
            }
            else if (rawMessage.Contains("\"type\":\"dump_types\"") || rawMessage.Contains("\"type\": \"dump_types\""))
            {
                var assemblyName = ExtractJsonField(rawMessage, "assembly");
                _mainThreadQueue.Enqueue(() =>
                {
                    try
                    {
                        Logger.LogInfo("[TypeGen] Extracting C# types for Assembly-CSharp...");
                        var dts = string.IsNullOrEmpty(assemblyName)
                            ? TypeDefGenerator.GenerateDtsForLoadedAssemblies("Assembly-CSharp")
                            : TypeDefGenerator.GenerateDtsForLoadedAssemblies(assemblyName);

                        Logger.LogInfo($"[TypeGen] Generated {dts.Length} characters of type definitions. Sending to CLI...");
                        socket.Send($"{{\"type\":\"dump_types_result\",\"dts\":\"{EscapeJson(dts)}\"}}");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"[TypeGen] Error dumping types: {ex}");
                        socket.Send($"{{\"type\":\"dump_types_result\",\"error\":\"{EscapeJson(ex.Message)}\"}}");
                    }
                });
            }
            else if (rawMessage.Contains("\"type\":\"unload\"") || rawMessage.Contains("\"type\": \"unload\""))
            {
                _mainThreadQueue.Enqueue(() =>
                {
                    Logger.LogInfo("[BepinExJS] Unload requested by CLI. Cleaning up mod...");
                    _runtimeManager?.Teardown();
                    try
                    {
                        socket.Send("{\"type\":\"unload_ack\",\"status\":\"ok\"}");
                    }
                    catch { }
                });
            }
            else if (rawMessage.Contains("\"type\":\"ping\"") || rawMessage.Contains("\"type\": \"ping\""))
            {
                socket.Send("{\"type\":\"pong\"}");
            }
        }

        private void Update()
        {
            // Execute main-thread tasks
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

            // Find first quote after colon
            var quoteStart = json.IndexOf('"', colonIdx);
            if (quoteStart == -1) return "";

            // Parse quoted string with escape handling
            var sb = new System.Text.StringBuilder();
            bool escaped = false;
            for (int i = quoteStart + 1; i < json.Length; i++)
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
    }
}
