import * as fs from 'fs';
import * as path from 'path';
import * as esbuild from 'esbuild';
import chokidar from 'chokidar';
import WebSocket from 'ws';
import chalk from 'chalk';

interface DevOptions {
  port: number;
  host: string;
  cwd: string;
}

export async function runDev(entryFile: string, options: DevOptions) {
  const cwd = options.cwd || process.cwd();
  const fullEntry = path.resolve(cwd, entryFile);

  if (!fs.existsSync(fullEntry)) {
    console.error(chalk.red(`[Error] Entry file not found: ${fullEntry}`));
    process.exit(1);
  }

  const wsUrl = `ws://${options.host}:${options.port}`;
  console.log(chalk.cyan(`[BepinExJS] Watching mod entry: ${chalk.bold(entryFile)}`));
  console.log(chalk.cyan(`[BepinExJS] Target Unity WebSocket: ${chalk.bold(wsUrl)}`));

  let ws: WebSocket | null = null;
  let isConnected = false;
  let reconnectTimer: NodeJS.Timeout | null = null;
  let lastBundle: string | null = null;

  function connect() {
    if (ws) {
      try {
        ws.terminate();
      } catch {}
    }

    ws = new WebSocket(wsUrl);

    ws.on('open', () => {
      isConnected = true;
      console.log(chalk.green(`[BepinExJS] Connected to Unity game!`));

      // Push latest bundle immediately upon connection if we have one
      if (lastBundle) {
        pushReload(lastBundle, entryFile);
      }
    });

    ws.on('message', (data: WebSocket.Data) => {
      try {
        const payload = JSON.parse(data.toString());
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
          console.log(chalk.green(`[BepinExJS] Hot-reload acknowledged by game!`));
        }
      } catch {
        // Non-json response
      }
    });

    ws.on('close', () => {
      if (isConnected) {
        console.log(chalk.yellow(`[BepinExJS] Disconnected from game. Reconnecting...`));
      }
      isConnected = false;
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
    if (ws && isConnected && ws.readyState === WebSocket.OPEN) {
      const msg = JSON.stringify({
        type: 'reload',
        code: code,
        path: filePath,
        timestamp: Date.now()
      });
      ws.send(msg);
      console.log(chalk.cyan(`[BepinExJS] Pushed updated bundle (${(code.length / 1024).toFixed(1)} KB)`));
    } else {
      console.log(chalk.gray(`[BepinExJS] Built bundle. Waiting for game connection...`));
    }
  }

  async function build() {
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
        console.log(chalk.green(`[BepinExJS] Bundled successfully in ${duration}ms`));
        pushReload(lastBundle, entryFile);
      }
    } catch (err: any) {
      console.error(chalk.red(`[BepinExJS] Bundling error:`), err.message);
    }
  }

  // Initial build
  await build();

  // Watch for changes
  const watchDir = path.dirname(fullEntry);
  const watcher = chokidar.watch(watchDir, {
    ignored: /(^|[\/\\])\..|node_modules|dist/,
    persistent: true,
    ignoreInitial: true,
  });

  let debounceTimer: NodeJS.Timeout | null = null;
  watcher.on('all', (event, changedPath) => {
    if (changedPath.endsWith('.ts') || changedPath.endsWith('.js') || changedPath.endsWith('.json')) {
      if (debounceTimer) clearTimeout(debounceTimer);
      debounceTimer = setTimeout(() => {
        console.log(chalk.gray(`[Watch] Change detected in ${path.relative(cwd, changedPath)}`));
        build();
      }, 100);
    }
  });

  // Start connecting to WebSocket
  connect();
}
