import * as fs from 'fs';
import * as path from 'path';
import chalk from 'chalk';

export function generateTypesContent(): string {
  return `// BepinExJS Global TypeScript Definitions

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
`;
}

export function runGenTypes(options: { output?: string; managedDir?: string; live?: boolean; port?: number; host?: string }) {
  const outputPath = options.output || path.join(process.cwd(), 'types', 'bepinex.d.ts');
  const dir = path.dirname(outputPath);

  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true });
  }

  if (options.live) {
    const host = options.host || '127.0.0.1';
    const port = options.port || 9092;
    const wsUrl = `ws://${host}:${port}`;
    console.log(chalk.cyan(`[BepinExJS] Connecting to game at ${wsUrl} to extract live C# types...`));

    const WebSocket = require('ws');
    const ws = new WebSocket(wsUrl);

    ws.on('open', () => {
      console.log(chalk.green(`[BepinExJS] Connected to game! Requesting type catalog...`));
      ws.send(JSON.stringify({ type: 'dump_types' }));
    });

    const timeout = setTimeout(() => {
      console.error(chalk.red(`\n[Timeout] No response received from game within 10 seconds.`));
      console.log(chalk.yellow(`Possible causes:`));
      console.log(chalk.yellow(`  1. The game needs to be restarted so the updated BepinExJS.Plugin.dll is loaded.`));
      console.log(chalk.yellow(`  2. Check the game's BepInEx console window for any errors.`));
      try { ws.close(); } catch {}
      process.exit(1);
    }, 10000);

    ws.on('message', (data: any) => {
      clearTimeout(timeout);
      try {
        const payload = JSON.parse(data.toString());
        if (payload.type === 'dump_types_result') {
          if (payload.error) {
            console.error(chalk.red(`[Error] Failed to dump types: ${payload.error}`));
          } else {
            const gameDtsPath = path.join(dir, 'game.d.ts');
            fs.writeFileSync(gameDtsPath, payload.dts);
            console.log(chalk.green(`[BepinExJS] Generated game C# bindings at: ${gameDtsPath}`));

            // Also ensure bepinex.d.ts exists
            if (!fs.existsSync(outputPath)) {
              fs.writeFileSync(outputPath, generateTypesContent());
              console.log(chalk.green(`[BepinExJS] Generated runtime globals at: ${outputPath}`));
            }
          }
          ws.close();
          process.exit(0);
        }
      } catch (err: any) {
        console.error(chalk.red(`[Error] Failed parsing response: ${err.message}`));
        ws.close();
        process.exit(1);
      }
    });

    ws.on('error', (err: any) => {
      console.error(chalk.red(`[Error] Could not connect to game at ${wsUrl}: ${err.message}`));
      console.log(chalk.yellow(`Make sure the Unity game is running with BepinExJS plugin active.`));
      process.exit(1);
    });

    return;
  }

  let content = generateTypesContent();

  if (options.managedDir && fs.existsSync(options.managedDir)) {
    console.log(chalk.cyan(`[BepinExJS] Inspecting game assemblies in ${options.managedDir}...`));
    const files = fs.readdirSync(options.managedDir).filter(f => f.endsWith('.dll'));
    content += `\n// Discovered Assemblies in Managed folder:\n`;
    files.forEach(f => {
      content += `// - ${f}\n`;
    });
  }

  fs.writeFileSync(outputPath, content);
  console.log(chalk.green(`[BepinExJS] Type definitions generated at: ${outputPath}`));
}
