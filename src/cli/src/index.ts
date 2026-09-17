#!/usr/bin/env node

import { Command } from 'commander';
import { runDev } from './commands/dev';
import { runRepl } from './commands/repl';
import { runInit } from './commands/init';
import { runGenTypes } from './commands/genTypes';

import { runBuild } from './commands/build';

const program = new Command();

program
  .name('bepinexjs')
  .description('CLI tool and hot-reload dev server for BepInEx JavaScript Unity mods')
  .version('0.1.0');

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
  .option('-p, --port <port>', 'WebSocket port of in-game plugin', '9092')
  .option('-h, --host <host>', 'WebSocket host of in-game plugin', '127.0.0.1')
  .action((entry = 'src/index.ts', opts) => {
    runDev(entry, {
      port: parseInt(opts.port, 10),
      host: opts.host,
      cwd: process.cwd()
    });
  });

program
  .command('repl')
  .description('Interactive terminal REPL connected to running Unity game instance')
  .option('-p, --port <port>', 'WebSocket port of in-game plugin', '9092')
  .option('-h, --host <host>', 'WebSocket host of in-game plugin', '127.0.0.1')
  .action((opts) => {
    runRepl({
      port: parseInt(opts.port, 10),
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
  .option('-l, --live', 'Connect to running game to dump all loaded C# types into game.d.ts')
  .option('-p, --port <port>', 'WebSocket port of in-game plugin', '9092')
  .option('-h, --host <host>', 'WebSocket host of in-game plugin', '127.0.0.1')
  .option('-o, --output <path>', 'Output path for .d.ts file')
  .option('-m, --managed-dir <path>', 'Path to game Managed/ directory')
  .action((opts) => {
    runGenTypes({
      output: opts.output,
      managedDir: opts.managedDir,
      live: opts.live,
      port: parseInt(opts.port, 10),
      host: opts.host
    });
  });

program.parse(process.argv);
