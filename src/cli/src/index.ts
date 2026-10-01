#!/usr/bin/env node

import { Command } from 'commander';
import { runDev } from './commands/dev';
import { runRepl } from './commands/repl';
import { runInit } from './commands/init';
import { runGenTypes } from './commands/genTypes';

import { runBuild } from './commands/build';
import { runGames } from './commands/games';
import { runAssemblies } from './commands/assemblies';

const program = new Command();

program
  .name('bepinexjs')
  .description('CLI tool and hot-reload dev server for BepInEx JavaScript Unity mods')
  .version('0.1.0');

program
  .command('games')
  .description('List all active Unity game instances running with BepinExJS')
  .action(() => {
    runGames();
  });

program
  .command('assemblies')
  .description('List all C# assembly DLLs loaded in the running Unity game')
  .option('-g, --game <nameOrPid>', 'Target running game name, executable, or PID')
  .option('-f, --filter <query>', 'Filter assemblies by name or path substring')
  .option('-p, --port <port>', 'WebSocket port of in-game plugin')
  .option('-h, --host <host>', 'WebSocket host of in-game plugin', '127.0.0.1')
  .action((opts) => {
    runAssemblies({
      game: opts.game,
      filter: opts.filter,
      port: opts.port ? parseInt(opts.port, 10) : undefined,
      host: opts.host
    });
  });

program
  .command('build [entry]')
  .description('Compile and package mod into a production bundle for game startup auto-load')
  .option('-o, --out <path>', 'Output file path (e.g. dist/my-mod.js or BepInEx/scripts/my-mod.js)')
  .option('-g, --game-dir <path>', 'Game root folder (installs directly into <game>/BepInEx/scripts/)')
  .option('-m, --minify', 'Minify output bundle', false)
  .action((entry = 'src/index.ts', opts) => {
    runBuild(entry, {
      out: opts.out,
      gameDir: opts.gameDir,
      minify: opts.minify
    });
  });

program
  .command('dev [entry]')
  .description('Bundle, watch, and hot-reload your JavaScript/TypeScript mod into Unity')
  .option('-g, --game <nameOrPid>', 'Target running game name, executable, or PID')
  .option('-p, --port <port>', 'WebSocket port of in-game plugin')
  .option('-h, --host <host>', 'WebSocket host of in-game plugin', '127.0.0.1')
  .action((entry = 'src/index.ts', opts) => {
    runDev(entry, {
      game: opts.game,
      port: opts.port ? parseInt(opts.port, 10) : undefined,
      host: opts.host,
      cwd: process.cwd()
    });
  });

program
  .command('repl')
  .description('Interactive terminal REPL connected to running Unity game instance')
  .option('-g, --game <nameOrPid>', 'Target running game name, executable, or PID')
  .option('-p, --port <port>', 'WebSocket port of in-game plugin')
  .option('-h, --host <host>', 'WebSocket host of in-game plugin', '127.0.0.1')
  .action((opts) => {
    runRepl({
      game: opts.game,
      port: opts.port ? parseInt(opts.port, 10) : undefined,
      host: opts.host
    });
  });

program
  .command('init [projectName]')
  .description('Scaffold a new TypeScript/JavaScript mod project')
  .action((projectName = 'MyBepinExJsMod') => {
    runInit(projectName);
  });

program
  .command('gen-types')
  .description('Generate TypeScript type definitions for BepinExJS')
  .option('-l, --live', 'Connect to running game to dump loaded C# types')
  .option('-s, --split', 'Generate separate modular .d.ts files per assembly in types/assemblies/', true)
  .option('--no-split', 'Bundle all assembly types into a single game.d.ts file')
  .option('-g, --game <nameOrPid>', 'Target running game name, executable, or PID')
  .option('-a, --assemblies <assemblies>', 'Comma-separated assemblies to extract (e.g. "Assembly-CSharp,mscorlib,System")')
  .option('-p, --port <port>', 'WebSocket port of in-game plugin')
  .option('-h, --host <host>', 'WebSocket host of in-game plugin', '127.0.0.1')
  .option('-o, --output <path>', 'Output path for bepinex.d.ts or root types folder')
  .option('-m, --managed-dir <path>', 'Path to game Managed/ directory')
  .action((opts) => {
    runGenTypes({
      output: opts.output,
      managedDir: opts.managedDir,
      assemblies: opts.assemblies,
      live: opts.live,
      split: opts.split,
      game: opts.game,
      port: opts.port ? parseInt(opts.port, 10) : undefined,
      host: opts.host
    });
  });

program.parse(process.argv);
