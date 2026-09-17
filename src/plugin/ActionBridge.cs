using System;
using System.Collections.Generic;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using UnityEngine;
using UnityEngine.Events;

namespace BepinExJS.Plugin
{
    public class ActionBridge
    {
        private readonly Engine _engine;

        public ActionBridge(Engine engine)
        {
            _engine = engine;
        }

        public Action CreateAction(JsValue fnVal)
        {
            var fn = fnVal as Function;
            if (fn == null) return () => { };
            return () =>
            {
                try
                {
                    fn.Call(JsValue.Undefined);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ActionBridge] Error executing Action: {ex}");
                }
            };
        }

        public Action<object> CreateAction1(JsValue fnVal)
        {
            var fn = fnVal as Function;
            if (fn == null) return _ => { };
            return arg =>
            {
                try
                {
                    var jsArg = JsValue.FromObject(_engine, arg);
                    fn.Call(JsValue.Undefined, new[] { jsArg });
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ActionBridge] Error executing Action<T>: {ex}");
                }
            };
        }

        public UnityAction CreateUnityAction(JsValue fnVal)
        {
            var fn = fnVal as Function;
            if (fn == null) return () => { };
            return () =>
            {
                try
                {
                    fn.Call(JsValue.Undefined);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ActionBridge] Error executing UnityAction: {ex}");
                }
            };
        }

        public UnityAction<bool> CreateUnityActionBool(JsValue fnVal)
        {
            var fn = fnVal as Function;
            if (fn == null) return _ => { };
            return val =>
            {
                try
                {
                    fn.Call(JsValue.Undefined, new[] { JsValue.FromObject(_engine, val) });
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ActionBridge] Error executing UnityAction<bool>: {ex}");
                }
            };
        }

        public UnityAction<float> CreateUnityActionFloat(JsValue fnVal)
        {
            var fn = fnVal as Function;
            if (fn == null) return _ => { };
            return val =>
            {
                try
                {
                    fn.Call(JsValue.Undefined, new[] { JsValue.FromObject(_engine, val) });
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ActionBridge] Error executing UnityAction<float>: {ex}");
                }
            };
        }

        public UnityAction<string> CreateUnityActionString(JsValue fnVal)
        {
            var fn = fnVal as Function;
            if (fn == null) return _ => { };
            return str =>
            {
                try
                {
                    fn.Call(JsValue.Undefined, new[] { JsValue.FromObject(_engine, str) });
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ActionBridge] Error executing UnityAction<string>: {ex}");
                }
            };
        }

        public Func<object?> CreateFunc(JsValue fnVal)
        {
            var fn = fnVal as Function;
            if (fn == null) return () => null;
            return () =>
            {
                try
                {
                    var res = fn.Call(JsValue.Undefined);
                    return res.ToObject();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ActionBridge] Error executing Func: {ex}");
                    return null;
                }
            };
        }
    }
}
