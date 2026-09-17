# BepinExJS-CLI

> Hot-reloadable JavaScript & TypeScript modding framework for Unity (Mono / BepInEx 5) with full access to compiled game structures, dynamic Harmony hooking, and an interactive CLI REPL.

---

## Architecture Overview

```text
┌────────────────────────────────────────────────────────┐
│                   CLI / Dev Machine                    │
│   (Node.js / TypeScript / esbuild / WebSocket Client)  │
│                                                        │
│   • bepinexjs init      (Scaffold new TS/JS mod)       │
│   • bepinexjs dev       (Watch, bundle & hot-reload)   │
│   • bepinexjs repl      (Interactive in-game console)  │
│   • bepinexjs gen-types (Generate .d.ts bindings)      │
└───────────────────────────┬────────────────────────────┘
                            │ WebSocket (ws://127.0.0.1:9092)
                            │ JSON-RPC & Hot-Reload Payloads
┌───────────────────────────▼────────────────────────────┐
│              Unity Game (BepInEx 5 Plugin)             │
│  ┌──────────────────────────────────────────────────┐  │
│  │ BepinExJS Runtime (Embedded Jint 3.1.6 Engine)   │  │
│  │ ├─ CLR Bridge (Dynamic Type Resolver: CS.*)      │  │
│  │ ├─ Unity Lifecycle (onUpdate, onFixedUpdate, GUI)│  │
│  │ ├─ Hot-Reload Teardown Registry (onUnload)       │  │
│  │ ├─ Dynamic HarmonyX Patching Bridge              │  │
│  │ └─ WebSocket Server & Remote Console Logger      │  │
│  └──────────────────────────────────────────────────┘  │
│                 ▼ Full Mono/CLR Access                 │
│      UnityEngine.dll, Assembly-CSharp.dll, etc.         │
└────────────────────────────────────────────────────────┘
```

---

## 1. Installation

### In-Game Plugin (BepInEx 5)
1. Build the plugin binaries:
   ```bash
   dotnet publish src/plugin/BepinExJS.Plugin.csproj -c Release -f netstandard2.0 -o dist/plugin
   ```
2. In your game's directory, open `BepInEx/plugins/` and create a `BepinExJS` folder:
   ```text
   <YourGameFolder>/
   └── BepInEx/
       └── plugins/
           └── BepinExJS/
   ```
3. Copy all files from `dist/plugin/` into `BepInEx/plugins/BepinExJS/`.
   Key files:
   - `BepinExJS.Plugin.dll`
   - `Jint.dll` (v3.1.6 Mono-compatible)
   - `Esprima.dll`
   - `Fleck.dll`

### CLI Tool
Install dependencies, build, and link the CLI globally:
```bash
cd src/cli
npm install
npm run build
npm link
```
*(This makes the `bepinexjs` command available anywhere in your command prompt or PowerShell).*

---

## 2. Quick Start

### A. Scaffold a New Mod Project
```bash
bepinexjs init MyAwesomeMod
cd MyAwesomeMod
```
This scaffolds a project with:
- `src/index.ts`: Mod entry point with sample UI and hooks.
- `types/bepinex.d.ts`: TypeScript typings for runtime APIs.
- `.vscode/`: Pre-configured VS Code tasks and settings.

### B. Start Live Hot-Reloading
Launch your Unity game, then run:
```bash
bepinexjs dev src/index.ts
```
Whenever you edit and save any file in `src/`, `bepinexjs` bundles your TypeScript in **<40ms** and pushes the new code into the running game instantly without restarting!

### C. Open Interactive In-Game REPL
In another terminal window:
```bash
bepinexjs repl
```
Inspect game state and evaluate expressions live in the game:
```javascript
bepinex> CS.UnityEngine.Time.timeScale = 2.0
=> 2.0

### D. Deploy for Game Startup (Permanent Installation)
When your mod is ready and you want it to run automatically on game startup without opening the CLI:

```bash
# Option 1: Direct install into your game's BepInExJS/ folder
bepinexjs build src/index.ts --game-dir "C:/path/to/YourUnityGame"

# Option 2: Output to dist/
bepinexjs build src/index.ts
```

#### Startup Configuration (`<game_dir>/BepInExJS.json`)
The game automatically creates a `BepInExJS.json` in your game root directory to control startup loading:

```json
{
  "enabled": true,
  "port": 9092,
  "host": "127.0.0.1",
  "autoloadDirectories": [
    "BepInExJS",
    "MyMods"
  ],
  "startupMods": [
    "MyMods/custom-cheat.js",
    { "path": "MyMods/experimental.js", "enabled": false }
  ]
}
```
- **`autoloadDirectories`**: Any `.js` files located in these folders are automatically executed when the game starts.
- **`startupMods`**: Specify exact script files to load, or temporarily toggle them on/off with `"enabled": false`.

---

## 3. Generating C# Type Bindings (`.d.ts`)

To get full autocomplete and IntelliSense for all game classes, player scripts, and items in VS Code:

With your game running, run:
```bash
bepinexjs gen-types --live
```
- Connects to the game over WebSocket.
- Reflects across `Assembly-CSharp` (all game-specific code, classes, methods, fields, enums).
- Generates `types/game.d.ts` in under a second.

---

## 4. VS Code Development Setup

Open your mod folder in VS Code:
```bash
code .
```
- **Start Dev Mode**: Press **`Ctrl + Shift + B`** (or `Terminal` -> `Run Build Task...`). This runs `bepinexjs dev` in the background.
- **Extract Types**: Press `Ctrl + Shift + P` -> `Tasks: Run Task` -> select **`BepinExJS: Extract Live C# Types`**.
- **REPL**: Press `Ctrl + Shift + P` -> `Tasks: Run Task` -> select **`BepinExJS: Interactive REPL`**.

---

## 5. JavaScript / TypeScript API Reference

### Accessing Unity & Game Structures (`CS.*`)
Access any class, struct, static or instance method across `UnityEngine`, `System`, or the game's `Assembly-CSharp`:

```typescript
// Find game objects
const player = CS.UnityEngine.GameObject.Find("Player");

// Access transform and properties
player.transform.position = new CS.UnityEngine.Vector3(0, 10, 0);

// Call static helpers
console.log("Current Scene:", CS.UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
```

### Frame & Lifecycle Hooks
```typescript
onUpdate(() => {
  // Check keypresses or tick logic
  if (CS.UnityEngine.Input.GetKeyDown(CS.UnityEngine.KeyCode.F1)) {
    console.log("F1 Pressed!");
  }
});

onFixedUpdate(() => {
  // Physics step
});
```

### Coroutines & Async / Await
Pause execution or create asynchronous routines running on Unity's frame/physics loop without freezing the game:

```typescript
// Start an async coroutine
startCoroutine(async () => {
  console.log("Waiting 3 seconds...");
  await waitSeconds(3.0);
  console.log("3 seconds passed!");

  // Wait for next frame (equivalent to yield return null)
  await waitNextFrame();

  // Wait until a custom condition is met
  await waitFor(() => CS.UnityEngine.GameObject.Find("Boss") !== null);
  console.log("Boss spawned!");
});
```
*(All running coroutines are automatically cancelled and cleaned up on hot-reload).*

### Action & UnityAction Delegates
Effortlessly pass JavaScript callbacks to C# methods, events, and Unity UI buttons:

```typescript
// Native Unity UI Button
const button = myButtonObj.GetComponent(CS.UnityEngine.UI.Button);
button.onClick.AddListener(UnityAction(() => {
  console.log("Button clicked from JS!");
}));

// C# Action with arguments
const myAction = toAction1((arg) => {
  console.log("C# event received:", arg);
});

// Unity UI Sliders / Toggles
slider.onValueChanged.AddListener(toUnityActionFloat((val) => {
  console.log("Slider moved to:", val);
}));
```

### In-Game GUI (IMGUI)
Render hot-reloadable IMGUI windows:
```typescript
let showMenu = true;

onGUI(() => {
  if (!showMenu) return;
  const GUI = CS.UnityEngine.GUI;
  const Rect = CS.UnityEngine.Rect;

  GUI.Box(new Rect(20, 20, 260, 150), "★ BepinExJS Mod Menu ★");
  if (GUI.Button(new Rect(35, 60, 230, 30), "Spawn Test Cube")) {
    const cube = CS.UnityEngine.GameObject.CreatePrimitive(CS.UnityEngine.PrimitiveType.Cube);
    cube.name = "BepinExJS_Cube";
  }
});
```

### Dynamic Harmony Patching
Intercept, alter, or cancel compiled game methods dynamically from JavaScript without compiling C# patch methods:

```typescript
Harmony.patch("Assembly-CSharp.PlayerController", "TakeDamage", {
  prefix: (instance, args) => {
    const amount = args[0];
    console.log("[Patch] Player taking damage:", amount);
    
    // Return false to block original method execution (e.g. Godmode)
    return false;
  },
  postfix: (instance, args, result) => {
    console.log("[Patch] TakeDamage completed");
  }
});
```
*(All Harmony patches are automatically unregistered and cleaned up on hot-reload).*

### Teardown Hook (`onUnload`)
Use `onUnload` to destroy spawned GameObjects, reset states, or detach listeners before the new script version is evaluated:

```typescript
const spawnedObjects = [];

onUnload(() => {
  console.log("Cleaning up before hot-reload...");
  spawnedObjects.forEach(obj => CS.UnityEngine.Object.Destroy(obj));
});
```

### Accessing Other C# Plugin DLLs
Any existing C# plugin installed in `BepInEx/plugins/` shares the same AppDomain:
```typescript
// Access other plugin singletons or classes
const otherPlugin = CS.OtherPluginNamespace.MainPlugin.Instance;
otherPlugin.DoSomething();
```

---

## 6. Common Recipes

### Get All GameObjects in the Scene (Including Disabled)
```typescript
function getAllSceneObjects() {
  const SceneManager = CS.UnityEngine.SceneManagement.SceneManager;
  const rootObjects = SceneManager.GetActiveScene().GetRootGameObjects();
  const all = [];

  function collect(obj) {
    all.push(obj);
    for (let i = 0; i < obj.transform.childCount; i++) {
      collect(obj.transform.GetChild(i).gameObject);
    }
  }

  for (let i = 0; i < rootObjects.length; i++) {
    collect(rootObjects[i]);
  }
  return all;
}
```

### Get All Components on a GameObject
```typescript
function getComponents(obj) {
  return Array.from(obj.GetComponents(CS.UnityEngine.Component)).map(c => ({
    name: c.GetType().Name,
    fullName: c.GetType().FullName,
    instance: c
  }));
}
```

---

## 7. Running Tests

Run the full automated test suite (CLR bridging, type resolution, hot-reload lifecycle, WebSocket server):

```bash
dotnet test
```
