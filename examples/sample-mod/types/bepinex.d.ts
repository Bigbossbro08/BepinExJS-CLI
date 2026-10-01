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

/**
 * Coroutine & Async Delay Helpers
 */
declare function waitSeconds(seconds: number): Promise<void>;
declare function waitNextFrame(): Promise<void>;
declare function waitForFixedUpdate(): Promise<void>;
declare function waitFor(predicate: () => boolean, intervalSeconds?: number): Promise<void>;
declare function startCoroutine(asyncFn: () => Promise<any>): Promise<any>;

/**
 * Action & UnityAction Delegate Converters
 */
declare function Action(fn: () => void): any;
declare function toAction(fn: () => void): any;
declare function toAction1(fn: (arg: any) => void): any;
declare function UnityAction(fn: () => void): any;
declare function toUnityAction(fn: () => void): any;
declare function toUnityActionBool(fn: (val: boolean) => void): any;
declare function toUnityActionFloat(fn: (val: number) => void): any;
declare function toUnityActionString(fn: (val: string) => void): any;
declare function toFunc(fn: () => any): any;

/**
 * Returns the CLR full type name or short name of any object safely.
 */
declare function getType(obj: any): string;

/**
 * Absolute directory path of the current self-contained mod project folder.
 */
declare const __dirname: string;

/**
 * Absolute file path of the executing script file.
 */
declare const __filename: string;

/**
 * Resolves a relative path against this mod project's directory.
 */
declare function resolvePath(relativePath: string): string;

/**
 * Cross-game peer-to-peer communication bridge.
 */
declare const CrossGame: {
  /**
   * Listen for an event emitted by another running Unity game.
   */
  on(eventName: string, callback: (data: any, sourcePid: number) => void): void;

  /**
   * Broadcast an event to all other running Unity games.
   */
  emit(eventName: string, data: any): void;

  /**
   * Discover other running Unity games with BepinExJS.
   */
  getGames(): { pid: number; gameTitle: string; processName: string; port: number }[];
};
