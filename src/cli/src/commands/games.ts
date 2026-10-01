import chalk from 'chalk';
import { getActiveGameSessions } from '../sessionManager';

export function runGames() {
  const sessions = getActiveGameSessions();

  if (sessions.length === 0) {
    console.log(chalk.yellow(`[BepinExJS] No active Unity games with BepinExJS detected.`));
    console.log(chalk.gray(`Make sure your Unity game is launched and BepinExJS plugin is installed.`));
    return;
  }

  console.log(chalk.cyan(`\n★ Active Unity Games with BepinExJS (${sessions.length}) ★\n`));

  sessions.forEach((s, idx) => {
    console.log(
      chalk.green(`  [${idx + 1}] `) +
      chalk.bold.white(`${s.gameTitle}`) +
      chalk.gray(` (PID: ${s.pid})`)
    );
    console.log(chalk.gray(`      Process: ${s.processName}.exe`));
    console.log(chalk.gray(`      Target:  ws://${s.host}:${s.port}`));
    if (s.unityVersion) {
      console.log(chalk.gray(`      Unity:   ${s.unityVersion}`));
    }
    console.log();
  });

  console.log(chalk.yellow(`To connect to a specific game:`));
  console.log(chalk.white(`  bepinexjs dev --game "${sessions[0].gameTitle}"`));
  console.log(chalk.white(`  bepinexjs repl --game "${sessions[0].gameTitle}"`));
}
