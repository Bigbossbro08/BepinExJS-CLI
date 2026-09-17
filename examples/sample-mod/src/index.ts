// BepinExJS Mod Entrypoint
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

  if (GUI.Button(new Rect(35, 140, 230, 30), "Get all GameObjects")) {
    // ----------------------------------------------------
    // Example: Inspect all objects in console
    // ----------------------------------------------------
    const objects = getAllSceneObjects();
    console.log(`[Scene Inspector] Total GameObjects found: ${objects.length}`);

    // Print the first 15 objects with their positions and active status
    objects.forEach((obj, idx) => {
      const pos = obj.transform.position;
      console.log(
        `#${idx + 1}: "${obj.name}" | Active: ${obj.activeSelf} | Pos: (${pos.x.toFixed(1)}, ${pos.y.toFixed(1)}, ${pos.z.toFixed(1)})`
      );
    });
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

// Function to get all GameObjects in the current active scene
function getAllSceneObjects() {
  const SceneManager = CS.UnityEngine.SceneManagement.SceneManager;
  const activeScene = SceneManager.GetActiveScene();
  const rootObjects = activeScene.GetRootGameObjects();
  
  const allObjects = [];

  // Recursive function to collect an object and all its children
  function collectHierarchy(obj) {
    allObjects.push(obj);
    
    const transform = obj.transform;
    const childCount = transform.childCount;
    
    for (let i = 0; i < childCount; i++) {
      const childObj = transform.GetChild(i).gameObject;
      collectHierarchy(childObj);
    }
  }

  // Traverse all root objects in the scene
  for (let i = 0; i < rootObjects.length; i++) {
    collectHierarchy(rootObjects[i]);
  }

  return allObjects;
}