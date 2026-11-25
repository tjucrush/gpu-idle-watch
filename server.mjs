import { spawn } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import http from 'node:http';
import { fileURLToPath } from 'node:url';

const port = Number(process.env.GPU_DASHBOARD_PORT || 8787);
const address = '127.0.0.1';
const interval = 2000;
const historyWindow = 300_000;
const askPass = fileURLToPath(new URL('./GpuWatch.AskPass.exe', import.meta.url));

function readHosts() {
  let config;
  try {
    const json = readFileSync(new URL('./servers.local.json', import.meta.url), 'utf8');
    config = JSON.parse(json.replace(/^\uFEFF/, ''));
  } catch (error) {
    throw new Error(error.code === 'ENOENT'
      ? '请先将 servers.example.json 复制为 servers.local.json，填写自己的服务器配置。'
      : '无法读取 servers.local.json，请检查文件编码和 JSON 格式。');
  }
  if (!Array.isArray(config?.servers) || !config.servers.length) throw new Error('请在 servers.local.json 中至少配置一台服务器。');
  const aliases = new Set();
  const names = new Set();
  return config.servers.map((entry, index) => {
    const label = `servers.local.json 第 ${index + 1} 台服务器`;
    const { name, ssh, mount, auth = 'key' } = entry || {};
    if (typeof name !== 'string' || !name.trim() || names.has(name.trim().toLowerCase())) throw new Error(`${label} 需要唯一的显示名称 name。`);
    if (typeof ssh !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/.test(ssh) || aliases.has(ssh.toLowerCase())) throw new Error(`${label} 需要唯一有效的 OpenSSH 别名 ssh。`);
    if (typeof mount !== 'string' || !mount.startsWith('/') || /[\u0000-\u001f\u007f-\u009f]/u.test(mount)) throw new Error(`${label} 的 mount 应为 Linux 绝对路径。`);
    if (auth !== 'key' && auth !== 'password') throw new Error(`${label} 的 auth 仅支持 key 或 password。`);
    names.add(name.trim().toLowerCase());
    aliases.add(ssh.toLowerCase());
    return { id: String(index + 1), name: name.trim(), ssh, auth };
  });
}
const hosts = readHosts();

const collector = readFileSync(new URL('./collect.py', import.meta.url), 'utf8').replaceAll('\r\n', '\n');
const quotedCollector = "'" + collector.replaceAll("'", "'\"'\"'") + "'";
const remoteCommand = `env LC_ALL=C python3 -B -u -c ${quotedCollector}`;

function connectionIssue(stderr) {
  if (/host key|identification has changed/i.test(stderr)) return 'SSH 主机密钥校验失败';
  if (/permission denied|authentication failed/i.test(stderr)) return 'SSH 认证失败，请检查本机 SSH 配置和凭据';
  if (/python3.*not found/i.test(stderr)) return '服务器上找不到 Python 3';
  if (/timed out|no route|unreachable|refused|resolve hostname/i.test(stderr)) return '无法访问服务器的 SSH 端口';
  return 'SSH 连接已断开，正在重试';
}

class HostMonitor {
  constructor(host, publish) {
    this.host = host;
    this.publish = publish;
    this.history = [];
    this.state = 'waiting';
    this.retryDelay = 4000;
  }

  start() {
    if (this.enabled) return;
    this.enabled = true;
    this.connect();
  }

  stop() {
    this.enabled = false;
    this.disconnect();
    this.state = 'waiting';
    this.issue = null;
    this.retryDelay = 4000;
  }

  disconnect() {
    clearTimeout(this.deadline);
    clearTimeout(this.nextRequest);
    this.pending = false;
    const child = this.child;
    this.child = null;
    if (child) {
      child.stdin.destroy();
      child.kill();
    }
  }

  fail(issue) {
    this.disconnect();
    this.state = 'offline';
    this.issue = issue;
    this.publish();
    if (!this.enabled) return;
    this.nextRequest = setTimeout(() => this.connect(), this.retryDelay);
    this.retryDelay = Math.min(this.retryDelay * 2, 30_000);
  }

  connect() {
    if (!this.enabled) return;
    this.state = 'connecting';
    this.issue = null;
    this.stderr = '';
    this.publish();
    const passwordAuth = this.host.auth === 'password';
    if (passwordAuth && !existsSync(askPass)) {
      this.fail('找不到 GpuWatch.AskPass.exe，请运行项目的构建脚本或使用发行包');
      return;
    }
    const authOptions = passwordAuth
      ? ['-o', 'BatchMode=no', '-o', 'PreferredAuthentications=password', '-o', 'PasswordAuthentication=yes',
        '-o', 'PubkeyAuthentication=no', '-o', 'KbdInteractiveAuthentication=no', '-o', 'NumberOfPasswordPrompts=1']
      : ['-o', 'BatchMode=yes', '-o', 'PreferredAuthentications=publickey'];
    const env = passwordAuth ? { ...process.env, SSH_ASKPASS: askPass, SSH_ASKPASS_REQUIRE: 'force',
      GPUWATCH_SSH_ALIAS: this.host.ssh, DISPLAY: process.env.DISPLAY || 'local' } : process.env;
    const child = spawn('ssh', [
      '-T', ...authOptions, '-o', 'StrictHostKeyChecking=yes',
      '-o', 'ConnectTimeout=6', '-o', 'ConnectionAttempts=1', '-o', 'ClearAllForwardings=yes',
      '-o', 'ServerAliveInterval=10', '-o', 'ServerAliveCountMax=2', '-o', 'LogLevel=ERROR',
      this.host.ssh, remoteCommand,
    ], { env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    this.child = child;
    let buffer = '';
    child.stderr.setEncoding('utf8').on('data', (data) => {
      if (this.child === child) this.stderr = (this.stderr + data).slice(-4000);
    });
    child.stdout.setEncoding('utf8').on('data', (data) => {
      if (this.child !== child) return;
      buffer += data;
      let end;
      while (this.child === child && (end = buffer.indexOf('\n')) !== -1) {
        const line = buffer.slice(0, end);
        buffer = buffer.slice(end + 1);
        if (line.startsWith('GPU_SAMPLE ') && this.pending) this.receive(line.slice('GPU_SAMPLE '.length));
      }
      if (this.child === child && buffer.length > 1_000_000) this.fail('采样响应过长');
    });
    child.stdin.on('error', () => {
      if (this.child === child) this.fail(connectionIssue(this.stderr));
    });
    child.on('error', (error) => {
      if (this.child === child) this.fail(error.code === 'ENOENT' ? '本机找不到 OpenSSH 客户端' : '无法启动 SSH 客户端');
    });
    child.on('close', () => {
      if (this.child === child) this.fail(connectionIssue(this.stderr));
    });
    this.request(16_000);
  }

  request(timeout = 12_000) {
    if (!this.child || this.pending) return;
    this.pending = true;
    this.requestedAt = Date.now();
    this.deadline = setTimeout(() => this.fail(this.stderr ? connectionIssue(this.stderr) : '采样超时，正在重试'), timeout);
    this.child.stdin.write('sample\n');
  }

  receive(line) {
    clearTimeout(this.deadline);
    this.pending = false;
    let sample;
    try {
      sample = JSON.parse(line);
    } catch {
      this.fail('无法解析服务器返回的 GPU 指标');
      return;
    }
    if (!Array.isArray(sample.gpus) || !Array.isArray(sample.processes)) {
      this.fail(sample.error === 'no_gpus' ? '未识别到 NVIDIA 显卡' : '服务器的 nvidia-smi 查询失败');
      return;
    }
    this.sample = sample;
    this.sampledAt = Date.now();
    this.state = 'online';
    this.issue = null;
    this.retryDelay = 4000;
    const values = sample.gpus.map((gpu) => gpu.utilization).filter(Number.isFinite);
    if (values.length) this.history.push([this.sampledAt, Math.round(values.reduce((sum, n) => sum + n, 0) / values.length)]);
    this.history = this.history.filter(([timestamp]) => timestamp >= this.sampledAt - historyWindow).slice(-180);
    this.publish();
    this.nextRequest = setTimeout(() => this.request(), Math.max(100, interval - (Date.now() - this.requestedAt)));
  }

  snapshot(now) {
    return {
      id: this.host.id, name: this.host.name, state: this.state, issue: this.issue || null,
      sampledAt: this.sampledAt || null, gpus: this.sample?.gpus || [],
      processes: this.sample?.processes || [], processStatus: this.sample?.processStatus || 'waiting',
      history: this.history.filter(([timestamp]) => timestamp >= now - historyWindow),
    };
  }
}

const viewers = new Set();
let publishTimer;
let stopTimer;
const monitors = hosts.map((host) => new HostMonitor(host, scheduleBroadcast));
const assets = {
  '/': ['index.html', 'text/html; charset=utf-8'],
  '/style.css': ['style.css', 'text/css; charset=utf-8'],
  '/app.js': ['app.js', 'text/javascript; charset=utf-8'],
};

function snapshot() {
  const now = Date.now();
  return { time: now, interval, historyWindow, hosts: monitors.map((monitor) => monitor.snapshot(now)) };
}

function scheduleBroadcast() {
  if (publishTimer || !viewers.size) return;
  publishTimer = setTimeout(() => {
    publishTimer = null;
    const packet = `data: ${JSON.stringify(snapshot())}\n\n`;
    for (const viewer of viewers) {
      if (viewer.writableLength > 262_144) viewer.end();
      else viewer.write(packet);
    }
  }, 60);
}

function subscribe(response) {
  response.writeHead(200, { 'Content-Type': 'text/event-stream; charset=utf-8', 'Cache-Control': 'no-cache, no-transform' });
  response.write(`retry: 2000\ndata: ${JSON.stringify(snapshot())}\n\n`);
  viewers.add(response);
  clearTimeout(stopTimer);
  monitors.forEach((monitor) => monitor.start());
  const ping = setInterval(() => response.write(': 心跳\n\n'), 15_000);
  response.on('close', () => {
    clearInterval(ping);
    viewers.delete(response);
    if (!viewers.size) stopTimer = setTimeout(() => monitors.forEach((monitor) => monitor.stop()), 2500);
  });
}

const server = http.createServer((request, response) => {
  response.setHeader('Cache-Control', 'no-store');
  response.setHeader('X-Content-Type-Options', 'nosniff');
  response.setHeader('Referrer-Policy', 'no-referrer');
  response.setHeader('Content-Security-Policy', "default-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'");
  const host = request.headers.host;
  if (host !== `${address}:${port}` && host !== `localhost:${port}`) {
    response.writeHead(403).end();
    return;
  }
  const route = request.url.split('?')[0];
  if (route.startsWith('/api/') && (request.headers['sec-fetch-site'] === 'cross-site' ||
      request.headers.origin && request.headers.origin !== `http://${host}`)) {
    response.writeHead(403).end();
    return;
  }
  if (request.method !== 'GET') {
    response.writeHead(405, { Allow: 'GET' }).end();
    return;
  }
  if (route === '/api/live') {
    subscribe(response);
  } else if (route === '/api/health') {
    response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify({ app: 'gpu-watch', pid: process.pid }));
  } else if (Object.hasOwn(assets, route)) {
    const [filename, type] = assets[route];
    response.writeHead(200, { 'Content-Type': type }).end(readFileSync(new URL(`./public/${filename}`, import.meta.url)));
  } else {
    response.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' }).end('未找到此页面');
  }
});

function shutdown() {
  clearTimeout(stopTimer);
  clearTimeout(publishTimer);
  monitors.forEach((monitor) => monitor.stop());
  viewers.forEach((viewer) => viewer.end());
  server.close(() => process.exit(0));
}

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
if (!Number.isInteger(port) || port < 1024 || port > 65_535) throw new Error('端口必须在 1024–65535 范围内');
server.listen(port, address, () => console.log(`GPU 实时看板 http://${address}:${port}/`));
