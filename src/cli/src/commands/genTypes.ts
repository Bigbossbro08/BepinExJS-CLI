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
`;
}

import { resolveTargetSession } from '../sessionManager';

export interface GenTypesOptions {
  output?: string;
  managedDir?: string;
  assemblies?: string;
  live?: boolean;
  port?: number;
  host?: string;
  game?: string;
  split?: boolean;
}

export function runGenTypes(options: GenTypesOptions) {
  const outputPath = options.output || path.join(process.cwd(), 'types', 'bepinex.d.ts');
  const baseDir = path.dirname(outputPath);

  if (!fs.existsSync(baseDir)) {
    fs.mkdirSync(baseDir, { recursive: true });
  }

  if (options.live) {
    let host = options.host || '127.0.0.1';
    let port = options.port;
    let targetTitle = 'Unity Game';

    const session = resolveTargetSession(options.game || port);
    if (session) {
      port = session.port;
      host = session.host || host;
      targetTitle = session.gameTitle;
    } else {
      port = port || 9092;
    }

    const wsUrl = `ws://${host}:${port}`;
    console.log(chalk.cyan(`[BepinExJS] Connecting to ${chalk.bold(targetTitle)} at ${wsUrl} to extract live C# types...`));

    const WebSocket = require('ws');
    const ws = new WebSocket(wsUrl);

    ws.on('open', () => {
      console.log(chalk.green(`[BepinExJS] Connected to game! Requesting type catalog...`));
      const payload: any = {
        type: 'dump_types',
        split: options.split !== false // default to true if requested or true
      };
      if (options.assemblies) {
        payload.assemblies = options.assemblies;
      }
      ws.send(JSON.stringify(payload));
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
          } else if (options.split && payload.files) {
            // Modular generation: write separate file for each assembly
            const assembliesDir = path.join(baseDir, 'assemblies');
            if (!fs.existsSync(assembliesDir)) {
              fs.mkdirSync(assembliesDir, { recursive: true });
            }

            const fileNames = Object.keys(payload.files);
            console.log(chalk.cyan(`[BepinExJS] Writing modular type definitions for ${fileNames.length} assembly files...`));

            for (const asmName of fileNames) {
              const safeName = asmName.replace(/[^a-zA-Z0-9_\-\.]/g, '_');
              const asmFilePath = path.join(assembliesDir, `${safeName}.d.ts`);
              fs.writeFileSync(asmFilePath, payload.files[asmName]);
              console.log(chalk.green(`  ✔ ${path.relative(process.cwd(), asmFilePath)}`));
            }

            // Write index / reference file in types/game.d.ts
            const gameDtsPath = path.join(baseDir, 'game.d.ts');
            let indexContent = `// Auto-generated Modular C# Assembly Type Index\n// Generated by BepinExJS TypeGen\n\n`;
            for (const asmName of fileNames) {
              const safeName = asmName.replace(/[^a-zA-Z0-9_\-\.]/g, '_');
              indexContent += `/// <reference path="./assemblies/${safeName}.d.ts" />\n`;
            }
            fs.writeFileSync(gameDtsPath, indexContent);
            console.log(chalk.green(`[BepinExJS] Master typings index created at: ${gameDtsPath}`));

            // Also ensure bepinex.d.ts exists
            if (!fs.existsSync(outputPath)) {
              fs.writeFileSync(outputPath, generateTypesContent());
              console.log(chalk.green(`[BepinExJS] Generated runtime globals at: ${outputPath}`));
            }
          } else {
            // Monolithic fallback
            const gameDtsPath = path.join(baseDir, 'game.d.ts');
            fs.writeFileSync(gameDtsPath, payload.dts || '');
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
