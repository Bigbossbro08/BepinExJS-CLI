using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using Jint;
using Jint.Native;

namespace BepinExJS.Plugin
{
    public class ModConfigManager
    {
        private readonly string _gameRoot;
        private readonly ManualLogSource _logger;
        private readonly string _configFilePath;

        public bool Enabled { get; private set; } = true;
        public int Port { get; private set; } = 9092;
        public string Host { get; private set; } = "127.0.0.1";
        public List<string> AutoloadDirectories { get; } = new List<string>();
        public List<string> StartupModFiles { get; } = new List<string>();

        public ModConfigManager(string gameRoot, ManualLogSource logger)
        {
            _gameRoot = gameRoot;
            _logger = logger;
            _configFilePath = Path.Combine(_gameRoot, "BepInExJS.json");

            LoadOrCreateConfig();
        }

        private void LoadOrCreateConfig()
        {
            if (!File.Exists(_configFilePath))
            {
                CreateDefaultConfig();
            }

            try
            {
                var jsonText = File.ReadAllText(_configFilePath);
                var parserEngine = new Engine();
                var result = parserEngine.Evaluate($"({jsonText})");

                if (result.IsObject())
                {
                    var obj = result.AsObject();

                    var enabledVal = obj.Get("enabled");
                    if (enabledVal.IsBoolean())
                    {
                        Enabled = enabledVal.AsBoolean();
                    }

                    var portVal = obj.Get("port");
                    if (portVal.IsNumber())
                    {
                        Port = (int)portVal.AsNumber();
                    }

                    var hostVal = obj.Get("host");
                    if (hostVal.IsString())
                    {
                        Host = hostVal.AsString();
                    }

                    // Autoload directories
                    var dirsVal = obj.Get("autoloadDirectories");
                    if (dirsVal.IsArray())
                    {
                        var arr = dirsVal.AsArray();
                        for (uint i = 0; i < arr.Length; i++)
                        {
                            var item = arr.Get(i.ToString());
                            if (item.IsString())
                            {
                                AutoloadDirectories.Add(item.AsString());
                            }
                        }
                    }

                    // Explicit startup mods
                    var modsVal = obj.Get("startupMods");
                    if (modsVal.IsArray())
                    {
                        var arr = modsVal.AsArray();
                        for (uint i = 0; i < arr.Length; i++)
                        {
                            var item = arr.Get(i.ToString());
                            if (item.IsString())
                            {
                                StartupModFiles.Add(item.AsString());
                            }
                            else if (item.IsObject())
                            {
                                var itemObj = item.AsObject();
                                var pathVal = itemObj.Get("path");
                                var itemEnabled = itemObj.Get("enabled");
                                var isEnabled = !itemEnabled.IsBoolean() || itemEnabled.AsBoolean();

                                if (isEnabled && pathVal.IsString())
                                {
                                    StartupModFiles.Add(pathVal.AsString());
                                }
                            }
                        }
                    }
                }

                _logger.LogInfo($"[Config] Loaded BepInExJS.json (Autoload dirs: {AutoloadDirectories.Count}, Explicit mods: {StartupModFiles.Count})");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Config] Error parsing BepInExJS.json: {ex.Message}. Falling back to default BepInExJS folder.");
                AutoloadDirectories.Clear();
                AutoloadDirectories.Add("BepInExJS");
            }
        }

        private void CreateDefaultConfig()
        {
            try
            {
                var defaultJson = @"{
  ""enabled"": true,
  ""port"": 9092,
  ""host"": ""127.0.0.1"",
  ""autoloadDirectories"": [
    ""BepInExJS""
  ],
  ""startupMods"": [
    // Add custom mod files here, e.g.:
    // { ""path"": ""MyMods/custom-script.js"", ""enabled"": true }
  ]
}";
                File.WriteAllText(_configFilePath, defaultJson);
                _logger.LogInfo($"[Config] Created default configuration at {_configFilePath}");

                // Also ensure default BepInExJS folder exists
                var defaultDir = Path.Combine(_gameRoot, "BepInExJS");
                if (!Directory.Exists(defaultDir))
                {
                    Directory.CreateDirectory(defaultDir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Config] Failed creating default config: {ex.Message}");
            }
        }

        public IEnumerable<string> ResolveAllStartupModFiles()
        {
            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Scan autoloadDirectories
            foreach (var relDir in AutoloadDirectories)
            {
                var dirPath = Path.IsPathRooted(relDir) ? relDir : Path.Combine(_gameRoot, relDir);
                if (Directory.Exists(dirPath))
                {
                    // A. Check for subdirectories (self-contained mod project folders)
                    foreach (var subDir in Directory.GetDirectories(dirPath))
                    {
                        var entry = ResolveModFolderEntry(subDir);
                        if (!string.IsNullOrEmpty(entry) && File.Exists(entry))
                        {
                            resolved.Add(Path.GetFullPath(entry));
                        }
                    }

                    // B. Also collect any loose .js files directly in this autoload root
                    var files = Directory.GetFiles(dirPath, "*.js", SearchOption.TopDirectoryOnly);
                    foreach (var file in files)
                    {
                        resolved.Add(Path.GetFullPath(file));
                    }
                }
                else
                {
                    // Create if it's the primary BepInExJS folder
                    if (relDir.Equals("BepInExJS", StringComparison.OrdinalIgnoreCase))
                    {
                        try { Directory.CreateDirectory(dirPath); } catch { }
                    }
                }
            }

            // 2. Scan explicit startupMods
            foreach (var relPath in StartupModFiles)
            {
                var targetPath = Path.IsPathRooted(relPath) ? relPath : Path.Combine(_gameRoot, relPath);
                if (Directory.Exists(targetPath))
                {
                    var entry = ResolveModFolderEntry(targetPath);
                    if (!string.IsNullOrEmpty(entry) && File.Exists(entry))
                    {
                        resolved.Add(Path.GetFullPath(entry));
                    }
                    else
                    {
                        _logger.LogWarning($"[Config] Startup mod folder specified in BepInExJS.json has no valid entry file: {relPath}");
                    }
                }
                else if (File.Exists(targetPath))
                {
                    resolved.Add(Path.GetFullPath(targetPath));
                }
                else
                {
                    _logger.LogWarning($"[Config] Startup mod file specified in BepInExJS.json not found: {relPath}");
                }
            }

            return resolved;
        }

        public static string? ResolveModFolderEntry(string folderPath)
        {
            // 1. Check mod.json for explicit "main" or "entry" field
            var manifestPath = Path.Combine(folderPath, "mod.json");
            if (!File.Exists(manifestPath))
            {
                manifestPath = Path.Combine(folderPath, "package.json");
            }

            if (File.Exists(manifestPath))
            {
                try
                {
                    var text = File.ReadAllText(manifestPath);
                    var parser = new Engine();
                    var parsed = parser.Evaluate($"({text})");
                    if (parsed.IsObject())
                    {
                        var obj = parsed.AsObject();
                        var main = obj.Get("main");
                        if (!main.IsString()) main = obj.Get("entry");
                        if (main.IsString())
                        {
                            var candidate = Path.Combine(folderPath, main.AsString());
                            if (File.Exists(candidate)) return candidate;
                        }
                    }
                }
                catch { }
            }

            // 2. Common conventional entrypoints
            string[] conventions = { "index.js", "mod.js", "main.js" };
            foreach (var conv in conventions)
            {
                var candidate = Path.Combine(folderPath, conv);
                if (File.Exists(candidate)) return candidate;
            }

            // 3. Folder named <folder>.js (e.g. MyMod/MyMod.js)
            var folderNameJs = Path.Combine(folderPath, Path.GetFileName(folderPath) + ".js");
            if (File.Exists(folderNameJs)) return folderNameJs;

            return null;
        }
    }
}
