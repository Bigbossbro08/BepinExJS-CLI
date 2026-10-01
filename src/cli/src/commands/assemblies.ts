import WebSocket from 'ws';
import chalk from 'chalk';
import { resolveTargetSession, getActiveGameSessions } from '../sessionManager';

interface AssembliesOptions {
  port?: number;
  host?: string;
  game?: string;
  filter?: string;
}

export function runAssemblies(options: AssembliesOptions) {
  let targetPort = options.port;
  let targetHost = options.host || '127.0.0.1';
  let targetTitle = "Unity Game";

  const session = resolveTargetSession(options.game || targetPort);
  if (session) {
    targetPort = session.port;
    targetHost = session.host || targetHost;
    targetTitle = session.gameTitle;
  } else {
    targetPort = targetPort || 9092;
  }

  const wsUrl = `ws://${targetHost}:${targetPort}`;
  console.log(chalk.cyan(`[BepinExJS] Querying loaded assemblies from ${chalk.bold(targetTitle)} at ${wsUrl}...`));

  const ws = new WebSocket(wsUrl);

  const timeout = setTimeout(() => {
    console.error(chalk.red(`\n[Timeout] No response received from game within 5 seconds.`));
    console.log(chalk.yellow(`Make sure the game is running with BepinExJS plugin loaded.`));
    try { ws.close(); } catch { }
    process.exit(1);
  }, 5000);

  ws.on('open', () => {
    ws.send(JSON.stringify({ type: 'list_assemblies' }));
  });

  ws.on('message', (data: WebSocket.Data) => {
    clearTimeout(timeout);
    try {
      const payload = JSON.parse(data.toString());
      if (payload.type === 'list_assemblies_result') {
        if (payload.error) {
          console.error(chalk.red(`[Error] Failed to list assemblies: ${payload.error}`));
          process.exit(1);
        }

        let assemblies: { name: string; location: string }[] = payload.assemblies || [];

        // Apply filter if specified
        if (options.filter) {
          const q = options.filter.toLowerCase();
          assemblies = assemblies.filter(
            a => a.name.toLowerCase().includes(q) || a.location.toLowerCase().includes(q)
          );
        }

        assemblies.sort((a, b) => a.name.localeCompare(b.name));

        console.log(chalk.green(`\n★ Loaded Assemblies in ${targetTitle} [${assemblies.length} found] ★\n`));

        assemblies.forEach((a, idx) => {
          const num = chalk.gray(`[${String(idx + 1).padStart(3, ' ')}]`);
          const name = chalk.bold.white(a.name);
          const loc = a.location ? chalk.gray(`(${a.location})`) : chalk.gray(`(in-memory)`);
          console.log(`  ${num} ${name} ${loc}`);
        });

        console.log();
        ws.close();
        process.exit(0);
      }
    } catch (err: any) {
      console.error(chalk.red(`[Error] Failed parsing response: ${err.message}`));
      ws.close();
      process.exit(1);
    }
  });

  ws.on('error', (err) => {
    clearTimeout(timeout);
    console.error(chalk.red(`[Error] Connection failed: ${err.message}`));
    console.log(chalk.yellow(`Make sure the Unity game is running and BepinExJS is loaded.`));
    process.exit(1);
  });
}
