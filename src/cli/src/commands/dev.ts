import * as fs from 'fs';
import * as path from 'path';
import * as esbuild from 'esbuild';
import chokidar from 'chokidar';
import WebSocket from 'ws';
import chalk from 'chalk';
import { resolveTargetSession, getActiveGameSessions } from '../sessionManager';

interface DevOptions {
  port?: number;
  host?: string;
  game?: string;
  cwd: string;
}

export async function runDev(entryFile: string, options: DevOptions) {
  const cwd = options.cwd || process.cwd();

  // 1. Resolve entry file, respecting mod.json if entryFile is the default "src/index.ts"
  let resolvedEntry = entryFile;
  const modJsonPath = path.join(cwd, 'mod.json');
  let targetGame = options.game;
  let targetPort = options.port;
  let targetHost = options.host || '127.0.0.1';

  if (fs.existsSync(modJsonPath)) {
    try {
      const parsed = JSON.parse(fs.readFileSync(modJsonPath, 'utf8'));
      if (entryFile === 'src/index.ts' && parsed.entry && typeof parsed.entry === 'string') {
        resolvedEntry = parsed.entry;
      }
      if (!targetGame && parsed.game) targetGame = parsed.game;
      if (!targetPort && parsed.port) targetPort = Number(parsed.port);
    } catch { }
  }

  const fullEntry = path.resolve(cwd, resolvedEntry);

  if (!fs.existsSync(fullEntry)) {
    console.error(chalk.red(`[Error] Entry file not found: ${fullEntry}`));
    if (resolvedEntry !== entryFile) {
      console.error(chalk.yellow(`  (Specified in mod.json entry: "${resolvedEntry}")`));
    }
    process.exit(1);
  }

  // Attempt session discovery
  const session = resolveTargetSession(targetGame || targetPort);
  if (session) {
    targetPort = session.port;
    targetHost = session.host || targetHost;
    console.log(chalk.cyan(`[BepinExJS] Auto-detected running game: ${chalk.bold.green(session.gameTitle)} (PID: ${session.pid}, Port: ${session.port})`));
  } else {
    const all = getActiveGameSessions();
    if (all.length > 1 && !targetGame && !options.port) {
      console.log(chalk.yellow(`[Notice] Multiple Unity games running (${all.length}).`));
      console.log(chalk.yellow(`  Connecting to default port ${targetPort || 9092}. Use --game <name> to pick a specific game.`));
    }
    targetPort = targetPort || 9092;
  }

  const wsUrl = `ws://${targetHost}:${targetPort}`;
  console.log(chalk.cyan(`[BepinExJS] Watching mod entry: ${chalk.bold(path.relative(cwd, fullEntry))}`));
  console.log(chalk.cyan(`[BepinExJS] Target Unity WebSocket: ${chalk.bold(wsUrl)}`));

  let ws: WebSocket | null = null;
  let isConnected = false;
  let isHandshakeComplete = false;
  let reconnectTimer: NodeJS.Timeout | null = null;
  let lastBundle: string | null = null;
  let currentGeneration = 0;
  let heartbeatTimer: NodeJS.Timeout | null = null;
  let lastPongTime = 0;
  const pendingAckTimeouts = new Map<number, NodeJS.Timeout>();

  // Build + reload telemetry counters
  const stats = { built: 0, buildFailed: 0, reloadOk: 0, reloadError: 0, reloadIgnored: 0 };

  function startHeartbeat() {
    stopHeartbeat();
    lastPongTime = Date.now();
    heartbeatTimer = setInterval(() => {
      if (ws && isConnected && ws.readyState === WebSocket.OPEN) {
        // If no pong received within 10 seconds, consider connection dead
        if (Date.now() - lastPongTime > 10000) {
          console.log(chalk.yellow(`[BepinExJS] Heartbeat lost (no pong in 10s). Reconnecting to game...`));
          try { ws.terminate(); } catch {}
          return;
        }
        try {
          ws.send(JSON.stringify({ type: 'ping' }));
        } catch {}
      }
    }, 3000);
  }

  function stopHeartbeat() {
    if (heartbeatTimer) {
      clearInterval(heartbeatTimer);
      heartbeatTimer = null;
    }
  }

  function connect() {
    if (ws) {
      try {
        ws.terminate();
      } catch {}
    }

    stopHeartbeat();
    isHandshakeComplete = false;
    ws = new WebSocket(wsUrl);

    ws.on('open', () => {
      // Send handshake to verify target is genuine BepinExJS plugin
      ws?.send(JSON.stringify({ type: 'handshake', version: '1.0.0' }));
    });

    ws.on('message', (data: WebSocket.Data) => {
      try {
        const payload = JSON.parse(data.toString());

        if (payload.type === 'pong') {
          lastPongTime = Date.now();
          return;
        }

        if (payload.type === 'handshake_ack') {
          isConnected = true;
          isHandshakeComplete = true;
          startHeartbeat();
          console.log(chalk.green(`[BepinExJS] Verified handshake with ${chalk.bold(payload.gameTitle || 'Unity Game')} (Unity ${payload.unityVersion || ''})!`));

          if (lastBundle) {
            pushReload(lastBundle, resolvedEntry);
          }
          return;
        }

        if (!isHandshakeComplete) return;

        if (payload.type === 'log') {
          const prefix = `[Game ${payload.level.toUpperCase()}]`;
          if (payload.level === 'error') {
            console.log(chalk.red(prefix), payload.message);
          } else if (payload.level === 'warn') {
            console.log(chalk.yellow(prefix), payload.message);
          } else {
            console.log(chalk.magenta(prefix), payload.message);
          }
        } else if (payload.type === 'reload_ack') {
          const gen = Number(payload.generation);
          if (pendingAckTimeouts.has(gen)) {
            clearTimeout(pendingAckTimeouts.get(gen)!);
            pendingAckTimeouts.delete(gen);
          }

          if (payload.status === 'ok') {
            stats.reloadOk++;
            console.log(chalk.green(`[BepinExJS] Hot-reload applied successfully in game! (gen: ${payload.generation || currentGeneration})`));
          } else if (payload.status === 'error') {
            stats.reloadError++;
            console.error(chalk.red(`[BepinExJS] Hot-reload error in game:`), payload.message || 'Execution failed');
          } else if (payload.status === 'ignored') {
            stats.reloadIgnored++;
            console.log(chalk.gray(`[BepinExJS] Stale reload generation ${payload.generation} ignored by game.`));
          }
        }
      } catch {
        // Non-json response
      }
    });

    ws.on('close', () => {
      stopHeartbeat();
      pendingAckTimeouts.forEach(t => clearTimeout(t));
      pendingAckTimeouts.clear();

      if (isConnected) {
        console.log(chalk.yellow(`[BepinExJS] Disconnected from game. Reconnecting...`));
      }
      isConnected = false;
      isHandshakeComplete = false;
      scheduleReconnect();
    });

    ws.on('error', () => {
      // Handled in close
    });
  }

  function scheduleReconnect() {
    if (reconnectTimer) return;
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null;
      connect();
    }, 2000);
  }

  function pushReload(code: string, filePath: string) {
    currentGeneration++;
    const gen = currentGeneration;

    if (ws && isConnected && isHandshakeComplete && ws.readyState === WebSocket.OPEN) {
      const msg = JSON.stringify({
        type: 'reload',
        generation: gen,
        code: code,
        path: filePath,
        modDir: cwd,
        timestamp: Date.now()
      });
      ws.send(msg);
      console.log(chalk.cyan(`[BepinExJS] Pushed updated bundle (gen ${gen}, ${(code.length / 1024).toFixed(1)} KB)`));

      // 5s ACK timeout to detect if Unity is frozen or paused
      const ackTimeout = setTimeout(() => {
        pendingAckTimeouts.delete(gen);
        if (isConnected) {
          console.log(chalk.yellow(`[Warning] No reload confirmation from game within 5s for gen ${gen}. The game might be paused, in a loading screen, or blocked.`));
        }
      }, 5000);
      pendingAckTimeouts.set(gen, ackTimeout);
    } else {
      console.log(chalk.gray(`[BepinExJS] Built bundle (gen ${gen}). Waiting for game connection...`));
    }
  }

  // Serialized build scheduler to prevent overlapping/out-of-order esbuild bundles
  let isBuilding = false;
  let isPendingBuild = false;

  async function scheduleBuild() {
    if (isBuilding) {
      isPendingBuild = true;
      return;
    }

    isBuilding = true;
    const startTime = Date.now();
    try {
      const result = await esbuild.build({
        entryPoints: [fullEntry],
        bundle: true,
        format: 'iife',
        write: false,
        sourcemap: 'inline',
        target: 'es2020',
        logLevel: 'silent',
      });

      if (result.outputFiles && result.outputFiles.length > 0) {
        lastBundle = result.outputFiles[0].text;
        const duration = Date.now() - startTime;
        stats.built++;
        console.log(chalk.green(`[BepinExJS] Bundled successfully in ${duration}ms`));
        pushReload(lastBundle, resolvedEntry);
      }
    } catch (err: any) {
      stats.buildFailed++;
      console.error(chalk.red(`[BepinExJS] Bundling error:`), err.message);
    } finally {
      isBuilding = false;
      if (isPendingBuild) {
        isPendingBuild = false;
        scheduleBuild();
      }
    }
  }

  // Connect to WebSocket server
  connect();

  // Initial build
  await scheduleBuild();

  // Watch for changes across the mod project directory (src, assets, mod.json)
  const watchTarget = path.resolve(cwd).replace(/[/\\]+$/, '');
  const watcher = chokidar.watch(watchTarget, {
    disableGlobbing: true,
    ignored: (filePath: string) => {
      const norm = filePath.replace(/\\/g, '/');
      return (
        /(^|\/)\.[^/.]/.test(norm) || // dotfiles/hidden dirs (.git, .vscode, etc.)
        norm.includes('/node_modules/') ||
        norm.includes('/dist/') ||
        norm.includes('/types/')
      );
    },
    persistent: true,
    ignoreInitial: true,
    awaitWriteFinish: {
      stabilityThreshold: 100,
      pollInterval: 50
    }
  });

  watcher.on('error', (err) => {
    console.warn(chalk.yellow(`[BepinExJS] File watcher warning: ${err.message}`));
  });

  let debounceTimer: NodeJS.Timeout | null = null;
  watcher.on('all', (event, changedPath) => {
    const ext = path.extname(changedPath).toLowerCase();
    const basename = path.basename(changedPath);
    if (ext === '.ts' || ext === '.js' || ext === '.json' || basename === 'mod.json') {
      if (debounceTimer) clearTimeout(debounceTimer);
      debounceTimer = setTimeout(() => {
        console.log(chalk.gray(`[Watch] Change detected in ${path.relative(cwd, changedPath)}`));
        scheduleBuild();
      }, 100);
    }
  });

  // Graceful shutdown on Ctrl+C
  let isShuttingDown = false;
  const cleanupAndExit = () => {
    if (isShuttingDown) return;
    isShuttingDown = true;

    // Clear all pending timers before any async operations to prevent post-exit callbacks
    if (debounceTimer) { clearTimeout(debounceTimer); debounceTimer = null; }
    if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
    stopHeartbeat();
    pendingAckTimeouts.forEach(t => clearTimeout(t));
    pendingAckTimeouts.clear();

    console.log(chalk.yellow('\n[BepinExJS] Shutting down dev server. Unloading mod in game...'));
    console.log(chalk.gray(`  Session stats — builds: ${chalk.green(stats.built + ' ok')} / ${chalk.red(stats.buildFailed + ' failed')} | reloads: ${chalk.green(stats.reloadOk + ' ok')} / ${chalk.red(stats.reloadError + ' error')} / ${chalk.gray(stats.reloadIgnored + ' ignored')}`));
    try { watcher.close(); } catch {}

    if (ws && isConnected && ws.readyState === WebSocket.OPEN) {
      try {
        ws.send(JSON.stringify({ type: 'unload' }), () => {
          try { ws?.close(); } catch {}
          console.log(chalk.green('[BepinExJS] Mod unloaded cleanly. Goodbye!'));
          process.exit(0);
        });
        setTimeout(() => {
          process.exit(0);
        }, 400);
        return;
      } catch {}
    }

    process.exit(0);
  };

  process.on('SIGINT', cleanupAndExit);
  process.on('SIGTERM', cleanupAndExit);
}
