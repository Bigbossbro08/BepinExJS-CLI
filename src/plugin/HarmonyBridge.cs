using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BepinExJS.Plugin
{
    public class DynamicPatchCallbacks
    {
        public Func<object, object[], bool>? Prefix { get; set; }
        public Action<object, object[], object>? Postfix { get; set; }
    }

    public class HarmonyBridge
    {
        private readonly Harmony _harmony;
        private readonly string _harmonyId;

        // Per-instance patch table, stored in a static registry so the static
        // Harmony delegate stubs can reach the right instance's callbacks.
        private readonly Dictionary<MethodBase, DynamicPatchCallbacks> _patches
            = new Dictionary<MethodBase, DynamicPatchCallbacks>();

        private static readonly ConcurrentDictionary<string, HarmonyBridge> _registry
            = new ConcurrentDictionary<string, HarmonyBridge>();

        public HarmonyBridge(string harmonyId = "com.bepinexjs.dynamic")
        {
            _harmonyId = harmonyId;
            _harmony = new Harmony(harmonyId);
            _registry[harmonyId] = this;
        }

        public bool Patch(MethodBase targetMethod, Func<object, object[], bool>? prefix, Action<object, object[], object>? postfix)
        {
            if (targetMethod == null)
                throw new ArgumentNullException(nameof(targetMethod));

            _patches[targetMethod] = new DynamicPatchCallbacks { Prefix = prefix, Postfix = postfix };

            var prefixMethod = prefix != null
                ? new HarmonyMethod(typeof(HarmonyBridge).GetMethod(nameof(DynamicPrefix), BindingFlags.Static | BindingFlags.NonPublic))
                : null;

            var postfixMethod = postfix != null
                ? new HarmonyMethod(typeof(HarmonyBridge).GetMethod(nameof(DynamicPostfix), BindingFlags.Static | BindingFlags.NonPublic))
                : null;

            _harmony.Patch(targetMethod, prefixMethod, postfixMethod);
            return true;
        }

        public void UnpatchAll()
        {
            _patches.Clear();
            _harmony.UnpatchSelf();
            _registry.TryRemove(_harmonyId, out _);
        }

        private static bool DynamicPrefix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            // Find the bridge instance that owns the patch for this harmony ID
            foreach (var bridge in _registry.Values)
            {
                if (bridge._patches.TryGetValue(__originalMethod, out var callbacks) && callbacks.Prefix != null)
                {
                    try
                    {
                        return callbacks.Prefix(__instance, __args);
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError($"[HarmonyBridge] Error in JS prefix patch for {__originalMethod.Name}: {ex}");
                    }
                }
            }
            return true;
        }

        private static void DynamicPostfix(MethodBase __originalMethod, object __instance, object[] __args, object __result)
        {
            foreach (var bridge in _registry.Values)
            {
                if (bridge._patches.TryGetValue(__originalMethod, out var callbacks) && callbacks.Postfix != null)
                {
                    try
                    {
                        callbacks.Postfix(__instance, __args, __result);
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError($"[HarmonyBridge] Error in JS postfix patch for {__originalMethod.Name}: {ex}");
                    }
                }
            }
        }
    }
}
