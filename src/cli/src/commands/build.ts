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

  // Resolve entry file, respecting mod.json if entryFile is the default "src/index.ts"
  let resolvedEntry = entryFile;
  const modJsonPath = path.join(cwd, 'mod.json');
  if (fs.existsSync(modJsonPath)) {
    try {
      const parsed = JSON.parse(fs.readFileSync(modJsonPath, 'utf8'));
      if (entryFile === 'src/index.ts' && parsed.entry && typeof parsed.entry === 'string') {
        resolvedEntry = parsed.entry;
      }
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

  // Determine output path
  let targetPath: string;
  let targetModDir: string | null = null;

  if (options.out) {
    targetPath = path.resolve(cwd, options.out);
  } else if (options.gameDir) {
    const scriptsDir = path.resolve(options.gameDir, 'BepInExJS');
    if (!fs.existsSync(scriptsDir)) {
      fs.mkdirSync(scriptsDir, { recursive: true });
    }
    const modFolderName = path.basename(cwd);
    targetModDir = path.join(scriptsDir, modFolderName);
    if (!fs.existsSync(targetModDir)) {
      fs.mkdirSync(targetModDir, { recursive: true });
    }
    targetPath = path.join(targetModDir, 'index.js');

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

    if (targetModDir) {
      // Copy assets folder if present
      const srcAssets = path.join(cwd, 'assets');
      const dstAssets = path.join(targetModDir, 'assets');
      if (fs.existsSync(srcAssets)) {
        fs.cpSync(srcAssets, dstAssets, { recursive: true });
        console.log(chalk.gray(`  Copied assets/ to mod folder.`));
      }

      // Copy mod.json if present
      const srcModJson = path.join(cwd, 'mod.json');
      if (fs.existsSync(srcModJson)) {
        fs.copyFileSync(srcModJson, path.join(targetModDir, 'mod.json'));
      }
    }

    if (options.gameDir) {
      const folderName = targetModDir ? path.basename(targetModDir) : path.basename(targetPath);
      console.log(chalk.green(`\n★ Installed to <game>/BepInExJS/${folderName}/!`));
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
