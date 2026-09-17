using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Native.Function;
using Jint.Runtime.Interop;
using UnityEngine;

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
        private Engine? _engine;
        private readonly HarmonyBridge _harmonyBridge;

        // Lifecycle callbacks registered from JS
        private readonly List<Action> _updateHooks = new List<Action>();
        private readonly List<Action> _fixedUpdateHooks = new List<Action>();
        private readonly List<Action> _guiHooks = new List<Action>();
        private readonly List<Action> _unloadHooks = new List<Action>();

        private readonly object _lock = new object();

        public JsRuntimeManager(ManualLogSource logger, Action<string, string> onLogForward)
        {
            _logger = logger;
            _onLogForward = onLogForward;
            _harmonyBridge = new HarmonyBridge("com.bepinexjs.dynamic");
        }

        public void Reload(string code, string sourceName = "mod.bundle.js")
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

                // 3. Register Globals and Bridges
                SetupGlobals(_engine);

                // 4. Evaluate code
                try
                {
                    _engine.Execute(code, sourceName);
                    _logger.LogInfo($"[BepinExJS] Successfully loaded/reloaded {sourceName}");
                    _onLogForward?.Invoke("info", $"Successfully loaded/reloaded {sourceName}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[BepinExJS] Error executing script: {ex}");
                    _onLogForward?.Invoke("error", $"Execution error: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        public void ExecuteStartupScript(string code, string sourceName = "startup.js")
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
                    SetupGlobals(_engine);
                }

                try
                {
                    _engine.Execute(code, sourceName);
                    _logger.LogInfo($"[BepinExJS] Successfully executed startup script: {sourceName}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[BepinExJS] Error executing startup script {sourceName}: {ex}");
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
                    SetupGlobals(_engine);
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

        private void SetupGlobals(Engine engine)
        {
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
            // Run unload callbacks
            foreach (var hook in _unloadHooks)
            {
                try { hook(); } catch (Exception ex) { _logger.LogError($"[Teardown] Error in onUnload: {ex}"); }
            }
            _unloadHooks.Clear();

            // Clear frame hooks
            _updateHooks.Clear();
            _fixedUpdateHooks.Clear();
            _guiHooks.Clear();

            // Unpatch Harmony
            _harmonyBridge.UnpatchAll();

            _engine?.Dispose();
            _engine = null;
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
        }
    }
}
