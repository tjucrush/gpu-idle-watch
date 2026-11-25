import { spawn } from 'node:child_process';
import { closeSync, mkdirSync, openSync } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

const port = Number(process.env.GPU_DASHBOARD_PORT || 8787);
const url = `http://127.0.0.1:${port}/`;
const runtime = new URL('./.runtime/', import.meta.url);
const log = new URL('./dashboard.log', runtime);

async function isReady() {
  try {
    const result = await fetch(`${url}api/health`, { signal: AbortSignal.timeout(750) });
    return result.ok && (await result.json()).app === 'gpu-watch';
  } catch {
    return false;
  }
}

async function launch() {
  if (!Number.isInteger(port) || port < 1024 || port > 65_535) throw new Error('端口必须在 1024–65535 范围内');
  if (!await isReady()) {
    mkdirSync(runtime, { recursive: true });
    const logFile = openSync(log, 'a');
    const child = spawn(process.execPath, [fileURLToPath(new URL('./server.mjs', import.meta.url))], {
      detached: true, windowsHide: true, stdio: ['ignore', logFile, logFile],
    });
    closeSync(logFile);
    let failure;
    child.on('error', (error) => { failure = error.message; });
    child.on('exit', (code) => { failure = `退出状态 ${code}`; });
    child.unref();
    for (let attempt = 0; attempt < 24 && !failure; attempt++) {
      if (await isReady()) break;
      await delay(250);
    }
    if (!await isReady()) throw new Error(`看板启动失败（${failure || '等待超时'}），日志：${fileURLToPath(log)}`);
  }
  console.log(`GPU 实时看板 ${url}`);
  if (process.platform === 'win32' && !process.argv.includes('--no-open')) {
    const browser = spawn('explorer.exe', [url], { detached: true, windowsHide: true, stdio: 'ignore' });
    browser.on('error', () => console.log(`请在浏览器打开 ${url}`));
    browser.unref();
  }
}

launch().catch((error) => { console.error(error.message); process.exitCode = 1; });
