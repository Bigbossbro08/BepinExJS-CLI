# BepinExJS-CLI

> Hot-reloadable JavaScript & TypeScript modding framework for Unity (Mono / BepInEx 5) with full access to compiled game structures, self-contained project folders, multi-game support, cross-game event bus, dynamic Harmony hooking, and an interactive CLI REPL.

---

## Architecture Overview

```text
┌────────────────────────────────────────────────────────┐
│                   CLI / Dev Machine                    │
│   (Node.js / TypeScript / esbuild / WebSocket Client)  │
│                                                        │
│   • bepinexjs init       (Scaffold self-contained mod) │
│   • bepinexjs dev        (Watch, bundle & hot-reload)  │
│   • bepinexjs repl       (Interactive in-game console) │
│   • bepinexjs games      (List all active Unity games) │
│   • bepinexjs assemblies (List loaded C# assembly DLLs)│
│   • bepinexjs gen-types  (Extract live C# typings)     │
│   • bepinexjs build      (Production mod packaging)    │
└───────────────────────────┬────────────────────────────┘
                            │ WebSocket (Dynamic Port / Handshake)
                            │ JSON-RPC & Hot-Reload Payloads
┌───────────────────────────▼────────────────────────────┐
│              Unity Game (BepInEx 5 Plugin)             │
│  ┌──────────────────────────────────────────────────┐  │
│  │ BepinExJS Runtime (Embedded Jint 3.1.6 Engine)   │  │
│  │ ├─ CLR Bridge (Dynamic Type Resolver: CS.*)      │  │
│  │ ├─ Self-Contained Projects (__dirname, assets)   │  │
│  │ ├─ CrossGame Event Bus (P2P Inter-game IPC)      │  │
│  │ ├─ Unity Coroutines (async/await, waitSeconds)   │  │
│  │ ├─ C# Delegate Bridges (Action, UnityAction)     │  │
│  │ ├─ Dynamic HarmonyX Patching Bridge              │  │
│  │ └─ Session Registry (~/.bepinexjs/sessions/<PID>)│  │
│  └──────────────────────────────────────────────────┘  │
│                 ▼ Full Mono/CLR Access                 │
│      UnityEngine.dll, Assembly-CSharp.dll, etc.         │
└────────────────────────────────────────────────────────┘
```

---

## 1. Installation

### In-Game Plugin (BepInEx 5)
1. Build and publish the plugin binaries:
   ```bash
   dotnet publish src/plugin/BepinExJS.Plugin.csproj -c Release -o dist/plugin
   ```
2. In your game's directory, open `BepInEx/plugins/` and create a `BepinExJS` folder:
   ```text
   <YourGameFolder>/
   └── BepInEx/
       └── plugins/
           └── BepinExJS/
   ```
3. Copy all files from `dist/plugin/` into `BepInEx/plugins/BepinExJS/`:
   - `BepinExJS.Plugin.dll`
   - `Jint.dll` (v3.1.6 Mono-compatible)
   - `Esprima.dll`
   - `Fleck.dll`

### CLI Tool
Install dependencies, compile, and link the CLI globally:
```bash
cd src/cli
npm install
npm run build
npm link
```
*(This makes the `bepinexjs` command available anywhere in your command prompt or PowerShell).*

---

## 2. Quick Start

### A. Scaffold a New Self-Contained Mod Project
```bash
bepinexjs init MyAwesomeMod
cd MyAwesomeMod
```
*(Or run `bepinexjs init .` inside an existing empty directory).*

This scaffolds:
- `mod.json`: Self-contained mod manifest (name, version, entry).
- `src/index.ts`: Mod entry point with UI, coroutines, CrossGame events, and cleanup hooks.
- `assets/config.json`: Sample project asset file accessible via `resolvePath("assets/config.json")`.
- `types/bepinex.d.ts`: TypeScript typings for all runtime APIs (`CS.*`, `CrossGame`, `__dirname`, etc.).
- `.vscode/`: Pre-wired VS Code build tasks and formatting settings.

### B. Start Live Hot-Reloading
Launch your Unity game, then run:
```bash
bepinexjs dev src/index.ts
```
Whenever you edit and save any file in `src/`, `bepinexjs` bundles your TypeScript in **<40ms** and pushes the new code into the running game instantly!

### C. Open Interactive In-Game REPL
```bash
bepinexjs repl
```
Inspect game state and evaluate expressions live in the game:
```javascript
bepinex> CS.UnityEngine.Time.timeScale = 2.0
=> 2.0
bepinex> CS.UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
=> "MainScene"
```

---

## 3. Multiple Games & Dynamic Port Discovery

BepinExJS supports running **multiple Unity games simultaneously** with zero port collisions:

- If default port `9092` is busy, the plugin automatically probes the next available port (`9093`, `9094`, ...).
- Each running game registers itself in `~/.bepinexjs/sessions/<PID>.json` and performs handshake verification with the CLI.

### List All Running Unity Games
```bash
bepinexjs games
```
Output:
```text
★ Active Unity Games with BepinExJS (2) ★

  [1] Valheim (PID: 14208)
      Process: Valheim.exe
      Target:  ws://127.0.0.1:9092

  [2] Risk of Rain 2 (PID: 28410)
      Process: Risk of Rain 2.exe
      Target:  ws://127.0.0.1:9093
```

### Connect to a Specific Game
```bash
# Connect by title or PID:
bepinexjs dev src/index.ts --game "Valheim"
bepinexjs repl --game "Risk of Rain"
```
Or define `"game": "Valheim"` directly in your mod's `mod.json`!

---

## 4. Querying Loaded C# Assemblies

To see what DLLs and modules are currently loaded in the game's CLR runtime:
```bash
bepinexjs assemblies
```
Filter by name:
```bash
bepinexjs assemblies -f "UnityEngine"
bepinexjs assemblies -f "Assembly-CSharp"
```

---

## 5. Generating C# Type Bindings (`.d.ts`)

BepinExJS supports **modular per-assembly** TypeScript generation to keep your type definitions organized, fast for IDE IntelliSense, and clean in source control:

```bash
# Dump default game assembly into types/assemblies/Assembly-CSharp.d.ts
bepinexjs gen-types --live

# Extract ALL loaded assemblies in the game (* or all)
bepinexjs gen-types --live -a "*"

# Extract multiple specific assemblies into separate modular files
bepinexjs gen-types --live -a "Assembly-CSharp,UnityEngine.CoreModule,mscorlib"

# Target a specific game instance when multiple are running
bepinexjs gen-types --live -g "Valheim" -a "*"

# Optional: Generate a single monolithic file instead of modular files
bepinexjs gen-types --live --no-split
```

By default with `--split` (enabled by default), `gen-types`:
1. Creates `types/assemblies/<AssemblyName>.d.ts` for each loaded C# assembly.
2. Generates an index `types/game.d.ts` with `/// <reference path="..." />` pointers to all assembly definitions.
3. Automatically leverages TypeScript declaration merging under `declare namespace CS`.

---

## 6. Self-Contained Project Packaging & Deployment

When your mod is ready for release or startup autoloading:

```bash
# Build directly into the game's BepInExJS/ directory
bepinexjs build src/index.ts --game-dir "C:/path/to/YourUnityGame"
```

The game installs the mod as a self-contained folder:
```text
<GameDir>/
  ├── BepInExJS.json              <-- Master config
  └── BepInExJS/
      └── MyAwesomeMod/           <-- Self-contained mod folder
          ├── mod.json
          ├── index.js            <-- Bundled script
          └── assets/             <-- Copied assets
              └── config.json
```

#### Startup Configuration (`<game_dir>/BepInExJS.json`)
```json
{
  "enabled": true,
  "port": 9092,
  "host": "127.0.0.1",
  "autoloadDirectories": [
    "BepInExJS"
  ],
  "startupMods": [
    "MyMods/custom-mod.js",
    { "path": "BepInExJS/ExperimentalMod", "enabled": false }
  ]
}
```

---

## 7. CLI Commands & Options Reference

Below is a complete reference of all `bepinexjs` CLI commands, default arguments, and available options:

| Command | Arguments | Options / Flags | Description |
| :--- | :--- | :--- | :--- |
| `bepinexjs init` | `[projectName]` *(default: `MyBepinExJsMod`, or `.`)* | — | Scaffolds a new self-contained mod project with `mod.json`, assets, VS Code tasks, and typings. |
| `bepinexjs dev` | `[entry]` *(default: `src/index.ts`)* | `-g, --game <nameOrPid>`<br>`-p, --port <port>`<br>`-h, --host <host>` *(default: `127.0.0.1`)* | Bundles, watches, and hot-reloads mod code into the running Unity game in <40ms. Auto-detects game or targets via `-g`. |
| `bepinexjs repl` | — | `-g, --game <nameOrPid>`<br>`-p, --port <port>`<br>`-h, --host <host>` *(default: `127.0.0.1`)* | Opens an interactive in-game REPL console to evaluate JavaScript and inspect Unity engine state live. |
| `bepinexjs games` | — | — | Lists all currently active Unity games with BepinExJS, showing process name, PID, and WebSocket port. |
| `bepinexjs assemblies` | — | `-g, --game <nameOrPid>`<br>`-f, --filter <query>`<br>`-p, --port <port>`<br>`-h, --host <host>` *(default: `127.0.0.1`)* | Lists all C# assembly DLLs loaded in the running game, with optional substring filtering. |
| `bepinexjs gen-types` | — | `-l, --live`<br>`-s, --split` *(default: on)*<br>`--no-split`<br>`-g, --game <nameOrPid>`<br>`-a, --assemblies <list>`<br>`-o, --output <path>`<br>`-m, --managed-dir <path>`<br>`-p, --port <port>`<br>`-h, --host <host>` *(default: `127.0.0.1`)* | Generates TypeScript definitions (`.d.ts`). Modular per-assembly generation into `types/assemblies/` by default, or monolithic with `--no-split`. |
| `bepinexjs build` | `[entry]` *(default: `src/index.ts`)* | `-o, --out <path>`<br>`-g, --game-dir <path>`<br>`-m, --minify` | Compiles mod into a production bundle. If `--game-dir` is provided, packages mod into `<game>/BepInExJS/<ModName>/`. |

---

## 8. JavaScript / TypeScript API Reference

### Project Directory & Asset Helpers (`__dirname`, `resolvePath`)
Each mod runs in its own directory context:
```typescript
console.log("Mod folder:", __dirname);

// Resolve path relative to mod project folder
const configPath = resolvePath("assets/config.json");
if (CS.System.IO.File.Exists(configPath)) {
  const content = CS.System.IO.File.ReadAllText(configPath);
  console.log("Config:", JSON.parse(content));
}
```

### Cross-Game Communication (`CrossGame`)
Peer-to-peer event bus between all running Unity games:
```typescript
// Listen for events from another game
CrossGame.on("boss_slain", (data, sourcePid) => {
  console.log(`[CrossGame] Boss ${data.name} was defeated in PID ${sourcePid}!`);
});

// Broadcast an event to all other games
CrossGame.emit("boss_slain", {
  name: "Mithrix",
  time: CS.UnityEngine.Time.time
});

// Discover other running games
const games = CrossGame.getGames();
```

### Accessing Unity & Game Structures (`CS.*`)
```typescript
// Find game objects
const player = CS.UnityEngine.GameObject.Find("Player");
player.transform.position = new CS.UnityEngine.Vector3(0, 10, 0);

// Safe CLR Type inspection (never crashes on wrapped objects)
const typeName = getType(player); // "UnityEngine.GameObject"
```

### Coroutines & Async / Await
```typescript
startCoroutine(async () => {
  console.log("Waiting 3 seconds...");
  await waitSeconds(3.0);

  // Wait for next frame (yield return null)
  await waitNextFrame();

  // Wait until predicate condition is true
  await waitFor(() => CS.UnityEngine.GameObject.Find("Boss") !== null);
  console.log("Boss spawned!");
});
```

### Action & UnityAction Delegates
```typescript
// Unity UI Button
const button = myButtonObj.GetComponent(CS.UnityEngine.UI.Button);
button.onClick.AddListener(UnityAction(() => {
  console.log("Button clicked!");
}));

// C# Action with 1 argument
const onDamage = toAction1((amount) => {
  console.log("Damage received:", amount);
});

// Sliders / Toggles
slider.onValueChanged.AddListener(toUnityActionFloat((val) => {
  console.log("Slider moved to:", val);
}));
```

### Dynamic Harmony Hooking
```typescript
Harmony.patch("Assembly-CSharp.PlayerController", "TakeDamage", {
  prefix: (instance, args) => {
    const amount = args[0];
    console.log("[Patch] Taking damage:", amount);
    return false; // Return false to block damage (Godmode)
  }
});
```

### Lifecycle & Cleanup (`onUnload`)
```typescript
onUnload(() => {
  console.log("Cleaning up mod state before hot-reload...");
  const cube = CS.UnityEngine.GameObject.Find("sample-mod_Cube");
  if (cube) CS.UnityEngine.Object.Destroy(cube);
});
```

---

## 9. Running Automated Tests

Run the comprehensive xUnit test suite (11/11 tests covering CLR bridging, self-contained mod loading, port probing, session registry, CrossGame IPC, actions, and coroutines):

```bash
dotnet test tests/BepinExJS.Tests/BepinExJS.Tests.csproj
```
