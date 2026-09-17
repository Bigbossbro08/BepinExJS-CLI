// BepinExJS Global TypeScript Definitions

/**
 * Access any compiled CLR / Mono class or namespace dynamically.
 * Examples:
 *   CS.UnityEngine.GameObject
 *   CS.UnityEngine.Time.deltaTime
 *   CS.AssemblyCSharp.PlayerController
 */
declare const CS: {
  [namespaceOrClass: string]: any;
};

/**
 * Import a specific CLR Type explicitly by its full qualified name.
 */
declare function importType(fullTypeName: string): any;

/**
 * Registers a callback invoked every Unity frame in Update().
 */
declare function onUpdate(callback: () => void): void;

/**
 * Registers a callback invoked every physics step in FixedUpdate().
 */
declare function onFixedUpdate(callback: () => void): void;

/**
 * Registers a callback invoked during the Unity IMGUI OnGUI() pass.
 */
declare function onGUI(callback: () => void): void;

/**
 * Registers a cleanup hook called right before the script is hot-reloaded or destroyed.
 * Use this to clean up spawned GameObjects, reset states, or detach custom listeners.
 */
declare function onUnload(callback: () => void): void;

/**
 * Dynamic Harmony method patching bridge.
 */
declare const Harmony: {
  /**
   * Patch any C# method dynamically.
   * @param targetType Full type name (e.g. "Assembly-CSharp.PlayerController")
   * @param methodName Method name (e.g. "TakeDamage")
   * @param hooks Prefix and/or Postfix callbacks
   */
  patch(
    targetType: string,
    methodName: string,
    hooks: {
      prefix?: (instance: any, args: any[]) => boolean | void;
      postfix?: (instance: any, args: any[], result?: any) => void;
    }
  ): boolean;
};
