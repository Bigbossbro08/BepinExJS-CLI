import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';

export interface GameSession {
  pid: number;
  processName: string;
  gameTitle: string;
  unityVersion?: string;
  gamePath?: string;
  host: string;
  port: number;
  startedAt?: number;
}

const SESSIONS_DIR = path.join(os.homedir(), '.bepinexjs', 'sessions');

function isPidAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (err: any) {
    return err.code === 'EPERM'; // Process exists but lack permission to signal
  }
}

/**
 * Discovers and returns all actively running Unity games with BepinExJS.
 * Automatically prunes sessions whose processes have terminated.
 */
export function getActiveGameSessions(): GameSession[] {
  if (!fs.existsSync(SESSIONS_DIR)) {
    return [];
  }

  const files = fs.readdirSync(SESSIONS_DIR);
  const active: GameSession[] = [];

  for (const file of files) {
    if (!file.endsWith('.json')) continue;
    const fullPath = path.join(SESSIONS_DIR, file);

    try {
      const content = fs.readFileSync(fullPath, 'utf8');
      const session = JSON.parse(content) as GameSession;

      if (session.pid && isPidAlive(session.pid)) {
        active.push(session);
      } else {
        // Prune stale session file
        try {
          fs.unlinkSync(fullPath);
        } catch { }
      }
    } catch {
      // Invalid JSON, remove
      try {
        fs.unlinkSync(fullPath);
      } catch { }
    }
  }

  return active;
}

/**
 * Resolves the best target game session based on user filter or active count.
 */
export function resolveTargetSession(filter?: string | number): GameSession | null {
  const sessions = getActiveGameSessions();
  if (sessions.length === 0) return null;

  if (filter !== undefined && filter !== null && filter !== '') {
    // If filter is a number or numeric string (like port or PID)
    const num = Number(filter);
    if (!isNaN(num)) {
      const byPortOrPid = sessions.find(s => s.port === num || s.pid === num);
      if (byPortOrPid) return byPortOrPid;
    }

    // Match by gameTitle or processName (case-insensitive substring)
    const strFilter = String(filter).toLowerCase();
    const matched = sessions.find(
      s => s.gameTitle.toLowerCase().includes(strFilter) || s.processName.toLowerCase().includes(strFilter)
    );
    if (matched) return matched;
  }

  // Default: if only 1 game is running, return it
  if (sessions.length === 1) {
    return sessions[0];
  }

  // Multiple running without explicit match
  return null;
}
