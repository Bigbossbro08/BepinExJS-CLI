using System;
using System.Diagnostics;
using System.IO;
using BepInEx.Logging;
using UnityEngine;

namespace BepinExJS.Plugin
{
    public class GameSessionRegistry : IDisposable
    {
        private readonly ManualLogSource _logger;
        private string? _sessionFilePath;
        private static readonly string SessionsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".bepinexjs",
            "sessions"
        );

        public GameSessionRegistry(ManualLogSource logger)
        {
            _logger = logger;
        }

        public void RegisterSession(int port, string host = "127.0.0.1")
        {
            try
            {
                if (!Directory.Exists(SessionsDirectory))
                {
                    Directory.CreateDirectory(SessionsDirectory);
                }

                var currentProcess = Process.GetCurrentProcess();
                var pid = currentProcess.Id;
                var processName = currentProcess.ProcessName;
                var gameTitle = processName;
                var unityVersion = "Unknown";

                try
                {
                    TryGetUnityMetadata(out var prod, out var ver);
                    if (!string.IsNullOrEmpty(prod)) gameTitle = prod!;
                    if (!string.IsNullOrEmpty(ver)) unityVersion = ver!;
                }
                catch { }

                var gamePath = Directory.GetCurrentDirectory();

                _sessionFilePath = Path.Combine(SessionsDirectory, $"{pid}.json");

                var json = $@"{{
  ""pid"": {pid},
  ""processName"": ""{EscapeJson(processName)}"",
  ""gameTitle"": ""{EscapeJson(gameTitle)}"",
  ""unityVersion"": ""{EscapeJson(unityVersion)}"",
  ""gamePath"": ""{EscapeJson(gamePath)}"",
  ""host"": ""{EscapeJson(host)}"",
  ""port"": {port},
  ""startedAt"": {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}
}}";

                File.WriteAllText(_sessionFilePath, json);
                _logger.LogInfo($"[SessionRegistry] Registered game session for '{gameTitle}' (PID: {pid}, Port: {port})");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[SessionRegistry] Failed to write session registry file: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                if (!string.IsNullOrEmpty(_sessionFilePath) && File.Exists(_sessionFilePath))
                {
                    File.Delete(_sessionFilePath);
                    _logger.LogInfo("[SessionRegistry] Cleaned up game session file.");
                }
            }
            catch { }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void TryGetUnityMetadata(out string? productName, out string? unityVersion)
        {
            try
            {
                productName = Application.productName;
                unityVersion = Application.unityVersion;
            }
            catch
            {
                productName = null;
                unityVersion = null;
            }
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
    }
}

