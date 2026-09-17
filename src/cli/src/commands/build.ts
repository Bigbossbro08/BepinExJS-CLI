import * as fs from 'fs';
import * as path from 'path';
import * as esbuild from 'esbuild';
import chalk from 'chalk';

interface BuildOptions {
  out?: string;
  gameDir?: string;
  minify?: boolean;
}

export async function runBuild(entryFile: string, options: BuildOptions) {
  const cwd = process.cwd();
  const fullEntry = path.resolve(cwd, entryFile);

  if (!fs.existsSync(fullEntry)) {
    console.error(chalk.red(`[Error] Entry file not found: ${fullEntry}`));
    process.exit(1);
  }

  // Determine output path
  let targetPath: string;

  if (options.out) {
    targetPath = path.resolve(cwd, options.out);
  } else if (options.gameDir) {
    const scriptsDir = path.resolve(options.gameDir, 'BepInExJS');
    if (!fs.existsSync(scriptsDir)) {
      fs.mkdirSync(scriptsDir, { recursive: true });
    }
    const modName = path.basename(cwd) + '.js';
    targetPath = path.join(scriptsDir, modName);

    // Ensure BepInExJS.json exists in game root
    const configPath = path.resolve(options.gameDir, 'BepInExJS.json');
    if (!fs.existsSync(configPath)) {
      const defaultConfig = {
        enabled: true,
        port: 9092,
        host: "127.0.0.1",
        autoloadDirectories: [
          "BepInExJS"
        ],
        startupMods: []
      };
      fs.writeFileSync(configPath, JSON.stringify(defaultConfig, null, 2));
    }
  } else {
    // Default to dist/<mod-name>.js
    const distDir = path.join(cwd, 'dist');
    if (!fs.existsSync(distDir)) {
      fs.mkdirSync(distDir, { recursive: true });
    }
    const modName = path.basename(cwd) + '.js';
    targetPath = path.join(distDir, modName);
  }

  const outDir = path.dirname(targetPath);
  if (!fs.existsSync(outDir)) {
    fs.mkdirSync(outDir, { recursive: true });
  }

  console.log(chalk.cyan(`[BepinExJS] Building production bundle...`));
  console.log(chalk.gray(`  Entry:  ${fullEntry}`));
  console.log(chalk.gray(`  Output: ${targetPath}`));

  const startTime = Date.now();
  try {
    const result = await esbuild.build({
      entryPoints: [fullEntry],
      bundle: true,
      format: 'iife',
      outfile: targetPath,
      target: 'es2020',
      minify: options.minify ?? false,
      sourcemap: false,
    });

    const duration = Date.now() - startTime;
    const stats = fs.statSync(targetPath);
    console.log(chalk.green(`\n✓ Build successful in ${duration}ms!`));
    console.log(chalk.white(`  File: ${targetPath} (${(stats.size / 1024).toFixed(1)} KB)`));

    if (options.gameDir) {
      console.log(chalk.green(`\n★ Installed to <game>/BepInExJS/${path.basename(targetPath)}!`));
      console.log(chalk.cyan(`  Configured in <game>/BepInExJS.json to execute automatically on game startup.`));
    } else {
      console.log(chalk.yellow(`\nTip: To run this mod automatically on game startup, copy it to:`));
      console.log(chalk.white(`  <YourGameFolder>/BepInExJS/${path.basename(targetPath)}`));
      console.log(chalk.yellow(`Or build directly into the game folder:`));
      console.log(chalk.white(`  bepinexjs build ${entryFile} --game-dir "C:/path/to/game"`));
    }
  } catch (err: any) {
    console.error(chalk.red(`[Error] Build failed: ${err.message}`));
    process.exit(1);
  }
}
