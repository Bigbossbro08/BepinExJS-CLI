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
    const scriptsDir = path.resolve(options.gameDir, 'BepInEx', 'scripts');
    if (!fs.existsSync(scriptsDir)) {
      fs.mkdirSync(scriptsDir, { recursive: true });
    }
    const modName = path.basename(cwd) + '.js';
    targetPath = path.join(scriptsDir, modName);
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

    if (targetPath.includes('BepInEx') && targetPath.includes('scripts')) {
      console.log(chalk.green(`\n★ Installed to BepInEx/scripts! This mod will now execute automatically whenever the game starts!`));
    } else {
      console.log(chalk.yellow(`\nTip: To have this mod run automatically on game startup, copy it to:`));
      console.log(chalk.white(`  <YourGameFolder>/BepInEx/scripts/${path.basename(targetPath)}`));
      console.log(chalk.yellow(`Or run: bepinexjs build ${entryFile} --game-dir "C:/path/to/game"`));
    }
  } catch (err: any) {
    console.error(chalk.red(`[Error] Build failed: ${err.message}`));
    process.exit(1);
  }
}
