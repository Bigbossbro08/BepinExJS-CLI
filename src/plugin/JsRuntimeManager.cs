using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Native.Function;
using Jint.Runtime.Interop;
using UnityEngine;
using UnityEngine.Events;

using System.Dynamic;

namespace BepinExJS.Plugin
{
    public class ClrNamespaceInstance : DynamicObject
    {
        private readonly string _prefix;
        private readonly JsRuntimeManager _manager;
        private readonly Engine _engine;

        public ClrNamespaceInstance(Engine engine, JsRuntimeManager manager, string prefix = "")
        {
            _engine = engine;
            _manager = manager;
            _prefix = prefix;
        }

        public override bool TryGetMember(GetMemberBinder binder, out object? result)
        {
            var propName = binder.Name;
            var fullName = string.IsNullOrEmpty(_prefix) ? propName : $"{_prefix}.{propName}";
            var type = _manager.FindType(fullName);
            if (type != null)
            {
                result = TypeReference.CreateTypeReference(_engine, type);
                return true;
            }

            // Return nested namespace wrapper
            result = new ClrNamespaceInstance(_engine, _manager, fullName);
            return true;
        }
    }

    public class ConsoleBridge
    {
        private readonly Action<string, object[]> _logger;

        public ConsoleBridge(Action<string, object[]> logger)
        {
            _logger = logger;
        }

        public void log(params object[] args) => _logger("info", args);
        public void info(params object[] args) => _logger("info", args);
        public void warn(params object[] args) => _logger("warn", args);
        public void error(params object[] args) => _logger("error", args);
        public void dir(params object[] args) => _logger("info", args);
    }

    public class HarmonyJsHelper
    {
        private readonly HarmonyBridge _harmonyBridge;
        private readonly ManualLogSource _logger;
        private readonly JsRuntimeManager _manager;
        private readonly Engine _engine;

        public HarmonyJsHelper(HarmonyBridge harmonyBridge, ManualLogSource logger, JsRuntimeManager manager, Engine engine)
        {
            _harmonyBridge = harmonyBridge;
            _logger = logger;
            _manager = manager;
            _engine = engine;
        }

        public bool patch(string targetTypeName, string methodName, JsValue hooks)
        {
            var targetType = _manager.FindType(targetTypeName);
            if (targetType == null)
            {
                _logger.LogError($"[Harmony] Target type not found: {targetTypeName}");
                return false;
            }

            var method = targetType.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (method == null)
            {
                _logger.LogError($"[Harmony] Target method not found: {targetTypeName}.{methodName}");
                return false;
            }

            Func<object, object[], bool>? prefix = null;
            Action<object, object[], object>? postfix = null;

            if (hooks.IsObject())
            {
                var hooksObj = hooks.AsObject();
                var prefixVal = hooksObj.Get("prefix");
                if (prefixVal is Function prefixFn)
                {
                    prefix = (instance, args) =>
                    {
                        var jsArgs = new List<JsValue>
                        {
                            JsValue.FromObject(_engine, instance),
                            JsValue.FromObject(_engine, args)
                        };
                        var res = prefixFn.Call(JsValue.Undefined, jsArgs.ToArray());
                        return !res.IsBoolean() || res.AsBoolean();
                    };
                }

                var postfixVal = hooksObj.Get("postfix");
                if (postfixVal is Function postfixFn)
                {
                    postfix = (instance, args, result) =>
                    {
                        var jsArgs = new List<JsValue>
                        {
                            JsValue.FromObject(_engine, instance),
                            JsValue.FromObject(_engine, args),
                            JsValue.FromObject(_engine, result)
                        };
                        postfixFn.Call(JsValue.Undefined, jsArgs.ToArray());
                    };
                }
            }

            return _harmonyBridge.Patch(method, prefix, postfix);
        }
    }

    public class JsRuntimeManager : IDisposable
    {
        private readonly ManualLogSource _logger;
        private readonly Action<string, string> _onLogForward;
        private readonly Action<Action>? _runOnMainThread;
        private Engine? _engine;
        private readonly HarmonyBridge _harmonyBridge;
        private readonly MonoBehaviour? _coroutineHost;
        private CoroutineBridge? _coroutineBridge;
        private CrossGameBridge? _crossGameBridge;

        // Lifecycle callbacks registered from JS
        private readonly List<Action> _updateHooks = new List<Action>();
        private readonly List<Action> _fixedUpdateHooks = new List<Action>();
        private readonly List<Action> _guiHooks = new List<Action>();
        private readonly List<Action> _unloadHooks = new List<Action>();

        private readonly object _lock = new object();

        // Last-known-good state: restored when a new reload fails at runtime
        private string? _lastGoodCode;
        private string? _lastGoodSourceName;
        private string? _lastGoodModDir;

        // Per-startup-mod sandboxed engines (one Engine per autoloaded mod, never shared)
        private readonly List<Engine> _startupEngines = new List<Engine>();

        public CrossGameBridge? CrossGame => _crossGameBridge;

        public JsRuntimeManager(ManualLogSource logger, Action<string, string> onLogForward, MonoBehaviour? coroutineHost = null, Action<Action>? runOnMainThread = null)
        {
            _logger = logger;
            _onLogForward = onLogForward;
            _coroutineHost = coroutineHost;
            _runOnMainThread = runOnMainThread;
            _harmonyBridge = new HarmonyBridge("com.bepinexjs.dynamic");

            // CrossGameBridge is created once and lives for the plugin lifetime.
            // It is NOT torn down on hot reload so remote WS connections are preserved.
            _crossGameBridge = new CrossGameBridge(_logger, null!, _runOnMainThread ?? (act => act()));
        }

        public (bool Success, string? Error) Reload(string code, string sourceName = "mod.bundle.js", string? modDir = null)
        {
            lock (_lock)
            {
                // 1. Teardown previous mod state
                Teardown();

                // 2. Initialize fresh Engine
                _engine = new Engine(options =>
                {
                    options.AllowClr(AppDomain.CurrentDomain.GetAssemblies());
                    options.AllowOperatorOverloading();
                    options.CatchClrExceptions();
                });

                // Resolve directory
                string resolvedDir;
                if (!string.IsNullOrEmpty(modDir))
                {
                    resolvedDir = modDir!;
                }
                else
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(sourceName);
                        resolvedDir = string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : Path.GetFullPath(dir);
                    }
                    catch
                    {
                        resolvedDir = Directory.GetCurrentDirectory();
                    }
                }

                // 3. Register Globals and Bridges
                SetupGlobals(_engine, resolvedDir, sourceName);

                // 4. Evaluate code
                try
                {
                    _engine.Execute(code, sourceName);
                    _logger.LogInfo($"[BepinExJS] Successfully loaded/reloaded {sourceName} (dir: {resolvedDir})");
                    _onLogForward?.Invoke("info", $"Successfully loaded/reloaded {sourceName}");

                    // Persist last-known-good state for fallback
                    _lastGoodCode = code;
                    _lastGoodSourceName = sourceName;
                    _lastGoodModDir = resolvedDir;

                    return (true, null);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[BepinExJS] Error executing script: {ex}");
                    _onLogForward?.Invoke("error", $"Execution error: {ex.Message}\n{ex.StackTrace}");

                    // Attempt to restore last-known-good bundle so the game stays modded
                    if (_lastGoodCode != null)
                    {
                        _logger.LogWarning("[BepinExJS] Attempting to restore last known-good bundle...");
                        _onLogForward?.Invoke("warn", "Hot-reload failed — restoring last known-good bundle.");
                        TryRestoreLastGood();
                    }

                    return (false, ex.Message);
                }
            }
        }

        /// <summary>
        /// Re-executes the last successfully loaded bundle after a failed reload.
        /// Called from within the _lock in Reload(). Does NOT update _lastGood fields.
        /// </summary>
        private void TryRestoreLastGood()
        {
            if (_lastGoodCode == null) return;

            try
            {
                // Teardown the broken engine from the failed attempt
                _engine?.Dispose();
                _engine = null;

                _engine = new Engine(options =>
                {
                    options.AllowClr(AppDomain.CurrentDomain.GetAssemblies());
                    options.AllowOperatorOverloading();
                    options.CatchClrExceptions();
                });

                SetupGlobals(_engine, _lastGoodModDir ?? Directory.GetCurrentDirectory(), _lastGoodSourceName ?? "mod.bundle.js");
                _engine.Execute(_lastGoodCode, _lastGoodSourceName ?? "mod.bundle.js");
                _logger.LogInfo("[BepinExJS] Last known-good bundle restored successfully.");
                _onLogForward?.Invoke("info", "Last known-good bundle restored. Fix the error in your code and save again.");
            }
            catch (Exception restoreEx)
            {
                _logger.LogError($"[BepinExJS] Failed to restore last known-good bundle: {restoreEx.Message}");
                _onLogForward?.Invoke("error", $"Restore also failed: {restoreEx.Message}. Mod is unloaded.");
                _engine?.Dispose();
                _engine = null;
            }
        }

        /// <summary>
        /// Executes a startup (autoloaded) mod script in its own isolated Engine sandbox.
        /// Each call produces a new Engine so that mods cannot pollute each other's globals.
        /// </summary>
        public void ExecuteStartupScript(string code, string sourceName = "startup.js", string? modDir = null)
        {
            lock (_lock)
            {
                string resolvedDir;
                if (!string.IsNullOrEmpty(modDir))
                {
                    resolvedDir = modDir!;
                }
                else
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(sourceName);
                        resolvedDir = string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : Path.GetFullPath(dir);
                    }
                    catch
                    {
                        resolvedDir = Directory.GetCurrentDirectory();
                    }
                }

                // Each startup mod gets its own isolated Engine — no shared global state
                var sandboxEngine = new Engine(options =>
                {
                    options.AllowClr(AppDomain.CurrentDomain.GetAssemblies());
                    options.AllowOperatorOverloading();
                    options.CatchClrExceptions();
                });

                SetupGlobals(sandboxEngine, resolvedDir, sourceName);

                try
                {
                    sandboxEngine.Execute(code, sourceName);
                    _logger.LogInfo($"[BepinExJS] Successfully executed startup script: {sourceName}");
                    _startupEngines.Add(sandboxEngine);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[BepinExJS] Error executing startup script {sourceName}: {ex}");
                    // Dispose the failed engine immediately; don't track it
                    try { sandboxEngine.Dispose(); } catch { }
                }
            }
        }

        public string ExecuteRepl(string snippet)
        {
            lock (_lock)
            {
                if (_engine == null)
                {
                    _engine = new Engine(options =>
                    {
                        options.AllowClr(AppDomain.CurrentDomain.GetAssemblies());
                        options.AllowOperatorOverloading();
                        options.CatchClrExceptions();
                    });
                    SetupGlobals(_engine, Directory.GetCurrentDirectory(), "repl.js");
                }

                try
                {
                    var result = _engine.Evaluate(snippet);
                    return result.ToString();
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            }
        }

        private void SetupGlobals(Engine engine, string modDir, string sourcePath)
        {
            // Inject mod directory and filename globals
            engine.SetValue("__dirname", modDir);
            engine.SetValue("__filename", sourcePath);
            engine.SetValue("resolvePath", new Func<string, string>(rel => Path.GetFullPath(Path.Combine(modDir, rel))));

            // CS namespace root
            engine.SetValue("CS", new ClrNamespaceInstance(engine, this, ""));

            // importType helper
            engine.SetValue("importType", new Func<string, JsValue>(typeName =>
            {
                var type = FindType(typeName);
                if (type == null)
                    throw new InvalidOperationException($"Type not found: {typeName}");
                return TypeReference.CreateTypeReference(engine, type);
            }));

            // Console bridge
            engine.SetValue("console", new ConsoleBridge(LogBridge));

            // Lifecycle registration functions
            engine.SetValue("onUpdate", new Action<Action>(hook =>
            {
                if (hook != null) _updateHooks.Add(hook);
            }));

            engine.SetValue("onFixedUpdate", new Action<Action>(hook =>
            {
                if (hook != null) _fixedUpdateHooks.Add(hook);
            }));

            engine.SetValue("onGUI", new Action<Action>(hook =>
            {
                if (hook != null) _guiHooks.Add(hook);
            }));

            engine.SetValue("onUnload", new Action<Action>(hook =>
            {
                if (hook != null) _unloadHooks.Add(hook);
            }));

            // Dynamic Harmony helper
            engine.SetValue("Harmony", new HarmonyJsHelper(_harmonyBridge, _logger, this, engine));

            // Action & UnityAction delegate helpers
            var actionBridge = new ActionBridge(engine);
            engine.SetValue("toAction", new Func<JsValue, Action>(actionBridge.CreateAction));
            engine.SetValue("Action", new Func<JsValue, Action>(actionBridge.CreateAction));
            engine.SetValue("toAction1", new Func<JsValue, Action<object>>(actionBridge.CreateAction1));
            engine.SetValue("toUnityAction", new Func<JsValue, UnityAction>(actionBridge.CreateUnityAction));
            engine.SetValue("UnityAction", new Func<JsValue, UnityAction>(actionBridge.CreateUnityAction));
            engine.SetValue("toUnityActionBool", new Func<JsValue, UnityAction<bool>>(actionBridge.CreateUnityActionBool));
            engine.SetValue("toUnityActionFloat", new Func<JsValue, UnityAction<float>>(actionBridge.CreateUnityActionFloat));
            engine.SetValue("toUnityActionString", new Func<JsValue, UnityAction<string>>(actionBridge.CreateUnityActionString));
            engine.SetValue("toFunc", new Func<JsValue, Func<object?>>(actionBridge.CreateFunc));

            // CrossGame event communication — re-wrap the long-lived bridge in a fresh JS-side wrapper.
            // The bridge itself is created once in the constructor and survives hot reloads.
            if (_crossGameBridge != null)
            {
                _crossGameBridge.UpdateEngine(engine);
                engine.SetValue("CrossGame", new CrossGameJsWrapper(_crossGameBridge));
            }

            // getType helper to inspect any object or CLR instance's type name safely
            engine.SetValue("getType", new Func<JsValue, string>(val =>
            {
                if (val == null || val.IsNull() || val.IsUndefined()) return "null";
                var obj = val.ToObject();
                if (obj == null) return "null";
                if (obj is Type t) return t.FullName ?? t.Name;
                return obj.GetType().FullName ?? obj.GetType().Name;
            }));

            // Coroutine & Async delay helpers
            if (!object.ReferenceEquals(_coroutineHost, null))
            {
                _coroutineBridge = new CoroutineBridge(_coroutineHost);
                engine.SetValue("__coroutineWaitSeconds", new Action<float, Action>(_coroutineBridge.WaitSeconds));
                engine.SetValue("__coroutineWaitNextFrame", new Action<Action>(_coroutineBridge.WaitNextFrame));
                engine.SetValue("__coroutineWaitForFixedUpdate", new Action<Action>(_coroutineBridge.WaitForFixedUpdate));
            }
            else
            {
                // Fallback for test / headless environments
                engine.SetValue("__coroutineWaitSeconds", new Action<float, Action>((s, cb) => cb?.Invoke()));
                engine.SetValue("__coroutineWaitNextFrame", new Action<Action>(cb => cb?.Invoke()));
                engine.SetValue("__coroutineWaitForFixedUpdate", new Action<Action>(cb => cb?.Invoke()));
            }

            engine.Execute(@"
                    function waitSeconds(seconds) {
                        return new Promise(function(resolve) {
                            __coroutineWaitSeconds(seconds, resolve);
                        });
                    }
                    function waitNextFrame() {
                        return new Promise(function(resolve) {
                            __coroutineWaitNextFrame(resolve);
                        });
                    }
                    function waitForFixedUpdate() {
                        return new Promise(function(resolve) {
                            __coroutineWaitForFixedUpdate(resolve);
                        });
                    }
                    async function waitFor(predicate, intervalSeconds) {
                        intervalSeconds = intervalSeconds || 0.1;
                        while (!predicate()) {
                            await waitSeconds(intervalSeconds);
                        }
                    }
                    function startCoroutine(asyncFn) {
                        return asyncFn().catch(function(err) {
                            console.error('[Coroutine Error]', err);
                        });
                    }
                ");
        }

        private void LogBridge(string level, object[] args)
        {
            var msg = string.Join(" ", args.Select(a => a?.ToString() ?? "null"));
            switch (level)
            {
                case "error":
                    _logger.LogError(msg);
                    break;
                case "warn":
                    _logger.LogWarning(msg);
                    break;
                default:
                    _logger.LogInfo(msg);
                    break;
            }
            _onLogForward?.Invoke(level, msg);
        }

        public void Teardown()
        {
            // Run unload callbacks from the hot-reload engine
            foreach (var hook in _unloadHooks)
            {
                try { hook(); } catch (Exception ex) { _logger.LogError($"[Teardown] Error in onUnload: {ex}"); }
            }
            _unloadHooks.Clear();

            // Clear frame hooks
            _updateHooks.Clear();
            _fixedUpdateHooks.Clear();
            _guiHooks.Clear();

            // Stop coroutines
            _coroutineBridge?.StopAll();
            _coroutineBridge = null;

            // Unpatch Dynamic Harmony patches
            _harmonyBridge?.UnpatchAll();

            // Note: _crossGameBridge is NOT disposed here — it lives for the full plugin lifetime.
            // It is disposed only in Dispose() (full shutdown).

            _engine?.Dispose();
            _engine = null;
        }

        /// <summary>
        /// Disposes all sandboxed startup-mod engines. Called on full plugin shutdown.
        /// </summary>
        private void TeardownStartupEngines()
        {
            lock (_lock)
            {
                foreach (var eng in _startupEngines)
                {
                    try { eng.Dispose(); } catch (Exception ex) { _logger.LogWarning($"[Teardown] Error disposing startup engine: {ex.Message}"); }
                }
                _startupEngines.Clear();
            }
        }

        public void TickUpdate()
        {
            if (_updateHooks.Count == 0) return;
            for (int i = 0; i < _updateHooks.Count; i++)
            {
                try { _updateHooks[i](); } catch (Exception ex) { _logger.LogError($"[Update] Error: {ex}"); }
            }
        }

        public void TickFixedUpdate()
        {
            if (_fixedUpdateHooks.Count == 0) return;
            for (int i = 0; i < _fixedUpdateHooks.Count; i++)
            {
                try { _fixedUpdateHooks[i](); } catch (Exception ex) { _logger.LogError($"[FixedUpdate] Error: {ex}"); }
            }
        }

        public void TickGUI()
        {
            if (_guiHooks.Count == 0) return;
            for (int i = 0; i < _guiHooks.Count; i++)
            {
                try { _guiHooks[i](); } catch (Exception ex) { _logger.LogError($"[OnGUI] Error: {ex}"); }
            }
        }

        public Type? FindType(string fullTypeName)
        {
            // 1. Standard Type.GetType
            var type = Type.GetType(fullTypeName, false);
            if (type != null) return type;

            // 2. Search loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType(fullTypeName, false);
                if (type != null) return type;
            }

            return null;
        }

        public void Dispose()
        {
            Teardown();
            TeardownStartupEngines();
            _crossGameBridge?.Dispose();
        }
    }
}
