import readline from 'readline';
import WebSocket from 'ws';
import chalk from 'chalk';
import { resolveTargetSession } from '../sessionManager';

interface ReplOptions {
  port?: number;
  host?: string;
  game?: string;
}

export function runRepl(options: ReplOptions) {
  let targetPort = options.port;
  let targetHost = options.host || '127.0.0.1';

  const session = resolveTargetSession(options.game || targetPort);
  if (session) {
    targetPort = session.port;
    targetHost = session.host || targetHost;
    console.log(chalk.cyan(`[BepinExJS] Connecting REPL to ${chalk.bold.green(session.gameTitle)} (PID: ${session.pid})...`));
  } else {
    targetPort = targetPort || 9092;
  }

  const wsUrl = `ws://${targetHost}:${targetPort}`;
  console.log(chalk.cyan(`[BepinExJS] Connecting REPL to ${wsUrl}...`));

  const ws = new WebSocket(wsUrl);

  const rl = readline.createInterface({
    input: process.stdin,
    output: process.stdout,
    prompt: chalk.green('bepinex> ')
  });

  let pendingCallback: ((res: string) => void) | null = null;

  ws.on('open', () => {
    console.log(chalk.green(`[BepinExJS] Connected to in-game runtime! Type any JS expression or .exit to quit.\n`));
    rl.prompt();
  });

  ws.on('message', (data: WebSocket.Data) => {
    try {
      const payload = JSON.parse(data.toString());
      if (payload.type === 'eval_result') {
        if (pendingCallback) {
          pendingCallback(payload.result);
          pendingCallback = null;
        } else {
          console.log(chalk.cyan(`=> ${payload.result}`));
        }
      } else if (payload.type === 'log') {
        // Output in-game log without corrupting prompt
        process.stdout.write(`\r${chalk.gray(`[Game ${payload.level}]`)} ${payload.message}\n`);
        rl.prompt();
      }
    } catch {
      // Non-json
    }
  });

  ws.on('error', (err) => {
    console.error(chalk.red(`[Error] WebSocket connection failed: ${err.message}`));
    process.exit(1);
  });

  ws.on('close', () => {
    console.log(chalk.yellow(`\nConnection closed by game.`));
    process.exit(0);
  });

  rl.on('line', (line) => {
    const trimmed = line.trim();
    if (!trimmed) {
      rl.prompt();
      return;
    }

    if (trimmed === '.exit') {
      rl.close();
      ws.close();
      process.exit(0);
    }

    if (ws.readyState !== WebSocket.OPEN) {
      console.log(chalk.red(`Not connected to game.`));
      rl.prompt();
      return;
    }

    pendingCallback = (res) => {
      console.log(chalk.cyan(`=> ${res}`));
      rl.prompt();
    };

    ws.send(JSON.stringify({
      type: 'eval',
      code: trimmed,
      id: Math.random().toString(36).substring(2, 9)
    }));
  });

  rl.on('close', () => {
    console.log(chalk.gray(`Exiting REPL.`));
    ws.close();
    process.exit(0);
  });
}
