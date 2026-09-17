import * as fs from 'fs';
import * as path from 'path';
import chalk from 'chalk';
import { generateTypesContent } from './genTypes';

export function runInit(projectName: string) {
  const targetDir = path.resolve(process.cwd(), projectName);

  if (fs.existsSync(targetDir)) {
    console.error(chalk.red(`[Error] Directory '${projectName}' already exists.`));
    process.exit(1);
  }

  console.log(chalk.cyan(`[BepinExJS] Scaffolding new mod in ${chalk.bold(targetDir)}...`));

  fs.mkdirSync(path.join(targetDir, 'src'), { recursive: true });
  fs.mkdirSync(path.join(targetDir, 'types'), { recursive: true });

  // package.json
  const pkgJson = {
    name: projectName.toLowerCase().replace(/\s+/g, '-'),
    version: '1.0.0',
    description: 'Unity game mod built with BepinExJS',
    scripts: {
      dev: 'bepinexjs dev src/index.ts',
      repl: 'bepinexjs repl'
    },
    devDependencies: {
      typescript: '^5.5.2'
    }
  };
  fs.writeFileSync(path.join(targetDir, 'package.json'), JSON.stringify(pkgJson, null, 2));

  // tsconfig.json
  const tsConfig = {
    compilerOptions: {
      target: 'ES2020',
      module: 'ESNext',
      moduleResolution: 'Node',
      strict: false,
      noImplicitAny: false,
      skipLibCheck: true
    },
    include: ['src/**/*', 'types/**/*']
  };
  fs.writeFileSync(path.join(targetDir, 'tsconfig.json'), JSON.stringify(tsConfig, null, 2));

  // types/bepinex.d.ts
  fs.writeFileSync(path.join(targetDir, 'types', 'bepinex.d.ts'), generateTypesContent());

  // src/index.ts
  const sampleModCode = `// BepinExJS Mod Entrypoint
console.log("[Mod] MyMod loaded successfully!");

let showWindow = true;
let windowRect = { x: 50, y: 50, width: 260, height: 160 };

// Draw Unity IMGUI GUI
onGUI(() => {
  if (!showWindow) return;

  const GUI = CS.UnityEngine.GUI;
  const Rect = CS.UnityEngine.Rect;

  // Render a simple window
  GUI.Box(new Rect(20, 20, 260, 150), "★ BepinExJS Mod Menu ★");

  if (GUI.Button(new Rect(35, 60, 230, 30), "Spawn Test Cube")) {
    console.log("[Mod] Spawning a test cube...");
    const cube = CS.UnityEngine.GameObject.CreatePrimitive(CS.UnityEngine.PrimitiveType.Cube);
    cube.name = "BepinExJS_Cube";
    cube.transform.position = new CS.UnityEngine.Vector3(0, 2, 5);
  }

  if (GUI.Button(new Rect(35, 100, 230, 30), "Log Game Time")) {
    console.log("[Mod] Unity Time: " + CS.UnityEngine.Time.time);
  }
});

// Unity Update loop hook
let timer = 0;
onUpdate(() => {
  // Check keypresses or tick logic
  // e.g. CS.UnityEngine.Input.GetKeyDown(CS.UnityEngine.KeyCode.F5)
});

// Teardown / cleanup hook executed prior to hot reload
onUnload(() => {
  console.log("[Mod] Unloading previous mod version...");
  const existingCube = CS.UnityEngine.GameObject.Find("BepinExJS_Cube");
  if (existingCube) {
    CS.UnityEngine.Object.Destroy(existingCube);
  }
});
`;
  fs.writeFileSync(path.join(targetDir, 'src', 'index.ts'), sampleModCode);

  // .vscode folder
  const vscodeDir = path.join(targetDir, '.vscode');
  fs.mkdirSync(vscodeDir, { recursive: true });

  const tasksJson = {
    version: "2.0.0",
    tasks: [
      {
        label: "BepinExJS: Start Dev & Hot-Reload",
        type: "shell",
        command: "bepinexjs dev src/index.ts",
        isBackground: true,
        problemMatcher: [],
        group: {
          kind: "build",
          isDefault: true
        }
      },
      {
        label: "BepinExJS: Interactive REPL",
        type: "shell",
        command: "bepinexjs repl",
        problemMatcher: []
      },
      {
        label: "BepinExJS: Extract Live C# Types",
        type: "shell",
        command: "bepinexjs gen-types --live",
        problemMatcher: []
      }
    ]
  };
  fs.writeFileSync(path.join(vscodeDir, 'tasks.json'), JSON.stringify(tasksJson, null, 2));

  const settingsJson = {
    "typescript.suggest.completeFunctionCalls": true,
    "editor.formatOnSave": true,
    "editor.defaultFormatter": "vscode.typescript-language-features"
  };
  fs.writeFileSync(path.join(vscodeDir, 'settings.json'), JSON.stringify(settingsJson, null, 2));

  // .gitignore
  fs.writeFileSync(path.join(targetDir, '.gitignore'), `node_modules/\ndist/\n`);

  console.log(chalk.green(`\nMod project initialized successfully with VS Code setup!`));
  console.log(`\nNext steps:`);
  console.log(chalk.white(`  1. cd ${projectName}`));
  console.log(chalk.white(`  2. Open in VS Code (code .)`));
  console.log(chalk.white(`  3. Press Ctrl+Shift+B to start Hot-Reload dev mode!`));
}
