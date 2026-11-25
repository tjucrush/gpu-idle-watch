const $ = (id) => document.getElementById(id);
const series = ['accent', 'mint', 'blue', 'amber', 'red'];
const sourceLabels = { cwd: '按工作目录推断', script: '按启动脚本推断', config: '按启动配置路径推断', executable: '按程序路径推断' };
const clock = new Intl.DateTimeFormat('zh-CN', { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
const shortClock = new Intl.DateTimeFormat('zh-CN', { hour: '2-digit', minute: '2-digit', hour12: false });

let snapshot = { hosts: [], time: Date.now(), interval: 2000, historyWindow: 300_000 };
let filterSignature;
let stream;
let streamState = 'connecting';
let paused = false;
let view = 'overview';
let gpuHost = 'all';
let processHost = 'all';
let processGpu = null;
let cursorX = null;

function escape(value) {
  return String(value ?? '').replace(/[&<>"']/g, (char) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]);
}

function seriesName(host) {
  return series[(Number(host.id) - 1) % series.length] || series[0];
}

function updateFilters() {
  const options = snapshot.hosts.map(({ id, name }) => [id, name]);
  const signature = JSON.stringify(options);
  if (signature === filterSignature) return;
  filterSignature = signature;
  if (!options.some(([id]) => id === gpuHost)) gpuHost = 'all';
  if (!options.some(([id]) => id === processHost)) {
    processHost = 'all';
    processGpu = null;
  }
  const buttons = [['all', '全部'], ...options].map(([id, name]) =>
    `<button type="button" data-host="${escape(id)}" data-focus-key="filter-${escape(id)}" aria-pressed="false">${escape(name)}</button>`
  ).join('');
  for (const group of ['gpu-filter', 'process-filter']) replaceContent($(group), buttons);
  setPressed('gpu-filter', gpuHost);
  setPressed('process-filter', processHost);
}

function percent(value) {
  return Number.isFinite(value) ? `${Math.round(value)}%` : '—';
}

function gib(value, decimals = 1) {
  return Number.isFinite(value) ? (value / 1024).toFixed(decimals) : '—';
}

function ratio(gpu) {
  return Number.isFinite(gpu.memoryUsed) && gpu.memoryTotal > 0 ? 100 * gpu.memoryUsed / gpu.memoryTotal : null;
}

function modelName(name) {
  return String(name ?? '').replace(/^NVIDIA\s+(GeForce\s+)?/, '') || '未知型号';
}

function meanUtilization(gpus) {
  const available = gpus.map((gpu) => gpu.utilization).filter(Number.isFinite);
  return available.length ? available.reduce((total, value) => total + value, 0) / available.length : null;
}

function runtime(seconds) {
  if (!Number.isFinite(seconds)) return '—';
  if (seconds < 60) return `${Math.floor(seconds)} 秒`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)} 分钟`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} 小时 ${Math.floor(seconds % 3600 / 60)} 分钟`;
  return `${Math.floor(seconds / 86400)} 天 ${Math.floor(seconds % 86400 / 3600)} 小时`;
}

function replaceContent(node, content) {
  const key = node.contains(document.activeElement) && document.activeElement.dataset.focusKey;
  node.innerHTML = content;
  if (key) [...node.querySelectorAll('[data-focus-key]')].find((element) => element.dataset.focusKey === key)?.focus({ preventScroll: true });
}

function currentHosts() {
  return snapshot.hosts.filter((host) => host.state === 'online');
}

function hostStatus(host) {
  if (host.state === 'online') return ['在线', 'online'];
  if (host.state === 'offline') return ['离线', 'offline'];
  return [host.state === 'waiting' ? '等待采样' : '连接中', 'waiting'];
}

function renderStatus() {
  const someOnline = currentHosts().length > 0;
  const someOffline = snapshot.hosts.some((host) => host.state === 'offline');
  const allOffline = snapshot.hosts.length > 0 && snapshot.hosts.every((host) => host.state === 'offline');
  const [state, description] = paused ? ['paused', '已暂停']
    : streamState !== 'open' ? [streamState, streamState === 'offline' ? '连接中断' : '连接中']
    : allOffline ? ['offline', '服务器离线']
    : someOffline ? ['connecting', '部分离线']
    : someOnline ? ['live', `实时 · ${snapshot.interval / 1000} 秒`] : ['connecting', '连接中'];
  const indicator = $('connection');
  if (indicator.dataset.current !== description) {
    indicator.className = `connection ${state}`;
    indicator.dataset.current = description;
    indicator.replaceChildren(document.createElement('span'), document.createTextNode(description));
  }
  $('pause').setAttribute('aria-pressed', String(paused));
  $('pause').lastElementChild.textContent = paused ? '继续' : '暂停';
}

function metric(glyph, title, value, foot) {
  return `<article class="metric"><div class="metric-title"><span class="metric-glyph" aria-hidden="true">${glyph}</span><span class="metric-label">${title}</span></div><strong>${value}</strong><div class="metric-foot">${foot}</div></article>`;
}

function renderSummary() {
  const hosts = currentHosts();
  const gpus = hosts.flatMap((host) => host.gpus);
  const processes = hosts.flatMap((host) => host.processes.map((item) => ({ ...item, hostId: host.id })));
  const uniqueProcesses = new Set(processes.map((item) => `${item.hostId}:${item.pid}`)).size;
  const projects = new Set(processes.map((item) => item.project).filter(Boolean)).size;
  const used = gpus.filter((gpu) => Number.isFinite(ratio(gpu)));
  const memoryUsed = used.reduce((sum, gpu) => sum + gpu.memoryUsed, 0);
  const memoryTotal = used.reduce((sum, gpu) => sum + gpu.memoryTotal, 0);
  const sampled = hosts.length > 0 || snapshot.hosts.some((host) => host.state === 'offline');
  const processSample = hosts.some((host) => host.processStatus === 'ok');
  const recent = Math.max(0, ...snapshot.hosts.map((host) => host.sampledAt || 0));
  $('update-time').innerHTML = recent ? `最近更新 <span class="mono">${clock.format(recent)}</span>` : '等待服务器数据';
  $('summary').innerHTML = [
    metric('▤', '在线服务器', `${sampled ? hosts.length : '—'} <small>/ ${snapshot.hosts.length}</small>`, gpus.length ? `<b>${gpus.length}</b> 张 GPU 在线` : '等待显卡数据'),
    metric('〽', '平均 GPU 利用率', percent(meanUtilization(gpus)), gpus.length ? `${gpus.filter((gpu) => gpu.utilization >= 5).length} 张卡利用率 ≥ 5%` : '等待利用率数据'),
    metric('▰', '显存占用', used.length ? `${gib(memoryUsed, 0)} <small>/ ${gib(memoryTotal, 0)} GiB</small>` : '—', used.length ? `合计占用 ${percent(100 * memoryUsed / memoryTotal)}` : '等待显存数据'),
    metric('≡', '计算进程', processSample ? uniqueProcesses : '—', processSample ? `识别到 <b>${projects}</b> 个项目` : '等待进程数据'),
  ].join('');
}

function renderServers() {
  const hosts = snapshot.hosts;
  $('online-count').textContent = `${currentHosts().length} / ${hosts.length} 在线`;
  replaceContent($('servers'), hosts.map((host) => {
    const [text, state] = hostStatus(host);
    const models = [...new Set(host.gpus.map((gpu) => modelName(gpu.name)))];
    const gpuType = host.gpus.length ? `${models.join(' / ')} · ${host.gpus.length} 张` : (host.issue || '等待显卡数据');
    const projects = new Set(host.processes.map((item) => item.project).filter(Boolean)).size;
    const stats = host.state === 'online' ? (host.processStatus === 'ok' ? `${projects} 个已识别项目` : '进程信息不可用') : '';
    return `<button type="button" class="server-row" data-select-host="${escape(host.id)}" data-focus-key="server-${escape(host.id)}" aria-label="查看 ${escape(host.name)} 的显卡，${text}">
      <span class="server-id series-${seriesName(host)}" aria-hidden="true">GPU</span>
      <span class="server-main"><strong>${escape(host.name)}</strong><small title="${escape(gpuType)}">${escape(gpuType)}</small></span>
      <span class="server-meta"><span class="status-label ${state}"><i class="status-dot" aria-hidden="true"></i>${text}</span>${stats ? `<small>${stats}</small>` : ''}</span>
    </button>`;
  }).join(''));
}

function meter(title, value, amount, type = '') {
  const fill = Number.isFinite(amount) ? Math.max(0, Math.min(100, amount)) : 0;
  return `<div class="meter-row"><div class="meter-text"><span>${title}</span><b>${value}</b></div>
    <progress class="meter-bar ${type} ${type === 'memory' && fill >= 90 ? 'high' : ''}" value="${fill}" max="100" aria-hidden="true"></progress></div>`;
}

function gpuCard(host, gpu, apps) {
  const active = host.state === 'online';
  const memory = ratio(gpu);
  const [state, stateText] = !active ? ['waiting', '旧读数']
    : gpu.utilization >= 5 ? ['busy', '计算活跃']
    : apps.length || gpu.memoryUsed >= 256 ? ['occupied', '显存已占用']
    : Number.isFinite(gpu.utilization) && memory !== null ? ['idle', '低占用'] : ['waiting', '指标不全'];
  const ordered = [...apps].sort((a, b) => (b.memoryUsed ?? -1) - (a.memoryUsed ?? -1));
  const names = [...new Set(ordered.map((item) => item.project || '未知项目'))];
  const projectText = host.processStatus === 'unavailable' ? '进程信息不可用' : '暂无计算进程';
  return `<article class="gpu-card${active ? '' : ' is-stale'}">
    <div class="gpu-card-top"><span class="gpu-id">GPU ${escape(gpu.index)}</span><span class="gpu-state ${state}">${stateText}</span></div>
    <div class="model" title="${escape(gpu.name)}">${escape(modelName(gpu.name))}</div>
    ${meter('利用率', percent(gpu.utilization), gpu.utilization)}
    ${meter('显存', memory === null ? '—' : `${gib(gpu.memoryUsed)} / ${gib(gpu.memoryTotal, 0)} GiB`, memory, 'memory')}
    <div class="hardware">
      <span class="${gpu.temperature >= 80 ? 'hot' : ''}">温度 <b>${Number.isFinite(gpu.temperature) ? `${Math.round(gpu.temperature)}°C` : '—'}</b></span>
      <span>功耗 <b>${Number.isFinite(gpu.power) ? `${Math.round(gpu.power)} W` : '—'}</b></span>
      ${Number.isFinite(gpu.fan) ? `<span>风扇 <b>${percent(gpu.fan)}</b></span>` : ''}
    </div>
    <div class="projects">
      ${names.length ? `<span class="project-names" title="${escape(names.join('、'))}"><span class="project-badge">${escape(names[0])}</span>${names.length > 1 ? `<span class="project-more">+${names.length - 1}</span>` : ''}</span>` : `<span class="quiet-label">${projectText}</span>`}
      ${apps.length ? `<button type="button" class="process-link" data-open-gpu="${escape(gpu.index)}" data-process-host="${escape(host.id)}" data-focus-key="process-${escape(host.id)}-${escape(gpu.index)}" aria-label="查看 ${escape(host.name)} 的 GPU ${escape(gpu.index)} 上的 ${apps.length} 个进程">${apps.length} 个进程</button>` : ''}
    </div>
  </article>`;
}

function renderGpuGroups() {
  const hosts = snapshot.hosts.filter((host) => gpuHost === 'all' || host.id === gpuHost);
  replaceContent($('gpu-groups'), hosts.map((host) => {
    const stale = host.state !== 'online' && host.sampledAt;
    const status = host.state === 'online' ? `${host.gpus.length} 张显卡`
      : stale ? `上次读数 ${clock.format(host.sampledAt)}` : host.issue || hostStatus(host)[0];
    return `<section class="server-group" aria-label="${escape(host.name)}">
      <div class="group-heading"><h3 class="series-${seriesName(host)}">${escape(host.name)}</h3><span class="${stale ? 'stale' : ''}">${escape(status)}</span></div>
      ${host.gpus.length ? `<div class="gpu-grid">${host.gpus.map((gpu) => gpuCard(host, gpu, host.processes.filter((app) => app.gpu === gpu.index))).join('')}</div>` : `<div class="group-empty">${escape(host.issue || '正在获取显卡指标')}</div>`}
    </section>`;
  }).join(''));
  setPressed('gpu-filter', gpuHost);
}

function setPressed(group, value) {
  for (const button of $(group).querySelectorAll('button[data-host]')) button.setAttribute('aria-pressed', String(button.dataset.host === value));
}

function renderProcesses() {
  const hosts = snapshot.hosts.filter((host) => processHost === 'all' || host.id === processHost);
  const available = hosts.filter((host) => host.state === 'online' && host.processStatus === 'ok');
  const query = $('process-query').value.trim().toLocaleLowerCase();
  const rows = available.flatMap((host) => host.processes.map((item) => ({ ...item, hostName: host.name })))
    .filter((item) => processGpu === null || item.gpu === processGpu)
    .filter((item) => [item.project || '未知项目', item.pid, item.user, item.program, item.hostName].some((field) => String(field ?? '').toLocaleLowerCase().includes(query)))
    .sort((a, b) => (b.memoryUsed ?? -1) - (a.memoryUsed ?? -1) || a.pid - b.pid);
  setPressed('process-filter', processHost);
  const chip = $('gpu-chip');
  chip.hidden = processGpu === null;
  if (processGpu !== null) {
    chip.textContent = `GPU ${processGpu} ×`;
    chip.setAttribute('aria-label', `清除 GPU ${processGpu} 筛选`);
  }
  $('process-count').textContent = available.length ? `${rows.length} 条进程记录` : '等待进程数据';
  const issues = hosts.filter((host) => host.state === 'offline' || host.processStatus === 'unavailable');
  $('process-issues').hidden = issues.length === 0;
  $('process-issues').textContent = issues.map((host) => `${host.name} ${host.issue || '进程信息不可用'}`).join(' · ');
  $('process-rows').innerHTML = rows.length ? rows.map((item) => `<tr>
      <td class="process-project ${item.project ? 'guessed' : 'unknown'}" title="${escape(item.project ? `${item.project} · ${sourceLabels[item.projectSource] || '按路径推断'}` : '当前进程没有可用的项目线索')}">${escape(item.project || '未知项目')}</td>
      <td class="mono">${escape(item.hostName)} <span class="dim">/</span> GPU ${escape(item.gpu)}</td>
      <td class="mono">${escape(item.pid)}</td><td>${escape(item.user || '—')}</td>
      <td class="mono" title="${escape(item.program || '')}">${escape(item.program || '—')}</td>
      <td class="mono memory-number">${gib(item.memoryUsed)}${Number.isFinite(item.memoryUsed) ? ' GiB' : ''}</td>
      <td>${runtime(item.runningSeconds)}</td>
    </tr>`).join('') : `<tr><td colspan="7" class="empty">${available.length ? (query ? '没有匹配的进程' : '暂无计算进程') : '当前筛选范围暂无可用读数'}</td></tr>`;
}

function drawTrend() {
  if (view !== 'overview') return;
  const canvas = $('trend');
  const { width, height } = canvas.getBoundingClientRect();
  if (width < 80 || height < 50) return;
  const scale = window.devicePixelRatio || 1;
  const pixels = [Math.round(width * scale), Math.round(height * scale)];
  if (canvas.width !== pixels[0]) canvas.width = pixels[0];
  if (canvas.height !== pixels[1]) canvas.height = pixels[1];
  const ctx = canvas.getContext('2d');
  ctx.setTransform(scale, 0, 0, scale, 0, 0);
  ctx.clearRect(0, 0, width, height);
  const left = 38;
  const right = width - 11;
  const top = 8;
  const bottom = height - 23;
  const allPoints = snapshot.hosts.flatMap((host) => host.history);
  const end = snapshot.time;
  const earliest = allPoints.length ? Math.min(...allPoints.map(([time]) => time)) : end;
  const duration = Math.min(snapshot.historyWindow, Math.max(20_000, end - earliest + 1500));
  const start = end - duration;
  const x = (time) => left + (time - start) / duration * (right - left);
  const y = (value) => bottom - Math.min(100, Math.max(0, value)) / 100 * (bottom - top);

  ctx.font = '12px "Cascadia Mono", Consolas, monospace';
  ctx.textBaseline = 'middle';
  ctx.strokeStyle = '#2b3245';
  ctx.fillStyle = '#98a6c1';
  ctx.lineWidth = 1;
  ctx.textAlign = 'right';
  for (const value of [0, 25, 50, 75, 100]) {
    ctx.beginPath();
    ctx.moveTo(left, y(value));
    ctx.lineTo(right, y(value));
    ctx.stroke();
    ctx.fillText(`${value}%`, left - 4, y(value));
  }
  const divisions = width > 450 ? 4 : 2;
  for (let i = 0; i <= divisions; i++) {
    const time = start + duration * i / divisions;
    ctx.textAlign = i === 0 ? 'left' : i === divisions ? 'right' : 'center';
    ctx.fillText((duration < 120_000 && width > 370 ? clock : shortClock).format(time), x(time), height - 9);
  }
  $('plot-placeholder').hidden = allPoints.length > 0;
  for (const host of snapshot.hosts) {
    const points = host.history.filter(([time]) => time >= start);
    if (!points.length) continue;
    ctx.strokeStyle = getComputedStyle(document.documentElement).getPropertyValue(`--${seriesName(host)}`).trim();
    ctx.fillStyle = ctx.strokeStyle;
    ctx.lineWidth = 2.15;
    ctx.lineJoin = 'round';
    ctx.beginPath();
    points.forEach(([time, value], index) => {
      if (index && time - points[index - 1][0] < 9500) ctx.lineTo(x(time), y(value));
      else ctx.moveTo(x(time), y(value));
    });
    ctx.stroke();
    const [time, value] = points.at(-1);
    ctx.beginPath();
    ctx.arc(x(time), y(value), 3, 0, Math.PI * 2);
    ctx.fill();
  }
  const tooltip = $('plot-tooltip');
  if (cursorX === null || !allPoints.length || cursorX < left || cursorX > right) {
    tooltip.hidden = true;
    return;
  }
  const hoverTime = start + (cursorX - left) / (right - left) * duration;
  const closest = snapshot.hosts.map((host) => {
    const near = host.history.reduce((best, point) => !best || Math.abs(point[0] - hoverTime) < Math.abs(best[0] - hoverTime) ? point : best, null);
    return [host.name, near && Math.abs(near[0] - hoverTime) <= 4500 ? near[1] : null];
  });
  tooltip.hidden = closest.every(([, value]) => value === null);
  if (tooltip.hidden) return;
  ctx.strokeStyle = '#7984a2';
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(cursorX, top);
  ctx.lineTo(cursorX, bottom);
  ctx.stroke();
  tooltip.textContent = `${clock.format(hoverTime)} · ${closest.map(([id, value]) => `${id} ${percent(value)}`).join(' · ')}`;
  tooltip.style.left = `${Math.max(5, Math.min(cursorX + 8, width - tooltip.offsetWidth - 4))}px`;
}

function render() {
  updateFilters();
  renderStatus();
  if (view === 'processes') {
    renderProcesses();
    return;
  }
  renderSummary();
  renderServers();
  renderGpuGroups();
  $('legend').innerHTML = snapshot.hosts.map((host) => `<span class="legend-item"><i class="legend-dot series-${seriesName(host)}" aria-hidden="true"></i>${escape(host.name)} <strong class="mono series-${seriesName(host)}">${host.state === 'online' ? percent(meanUtilization(host.gpus)) : '—'}</strong></span>`).join('');
  drawTrend();
}

function connect() {
  if (stream) stream.close();
  const eventStream = new EventSource('/api/live');
  stream = eventStream;
  streamState = 'connecting';
  eventStream.onopen = () => {
    if (stream !== eventStream) return;
    streamState = 'open';
    renderStatus();
  };
  eventStream.onmessage = (event) => {
    if (stream !== eventStream) return;
    snapshot = JSON.parse(event.data);
    render();
  };
  eventStream.onerror = () => {
    if (stream !== eventStream) return;
    streamState = 'offline';
    renderStatus();
  };
  renderStatus();
}

function updateView() {
  view = location.hash === '#processes' ? 'processes' : 'overview';
  $('overview').hidden = view !== 'overview';
  $('processes').hidden = view !== 'processes';
  for (const link of document.querySelectorAll('a[data-view]')) {
    if (link.dataset.view === view) link.setAttribute('aria-current', 'page');
    else link.removeAttribute('aria-current');
  }
  render();
}

$('pause').addEventListener('click', () => {
  paused = !paused;
  if (paused) {
    stream?.close();
    stream = null;
  } else connect();
  renderStatus();
});
$('gpu-filter').addEventListener('click', (event) => {
  const button = event.target.closest('button[data-host]');
  if (button) { gpuHost = button.dataset.host; renderGpuGroups(); }
});
$('servers').addEventListener('click', (event) => {
  const button = event.target.closest('button[data-select-host]');
  if (!button) return;
  gpuHost = button.dataset.selectHost;
  renderGpuGroups();
  $('gpu-section').scrollIntoView({ block: 'start' });
});
$('gpu-groups').addEventListener('click', (event) => {
  const button = event.target.closest('button[data-open-gpu]');
  if (!button) return;
  processHost = button.dataset.processHost;
  processGpu = Number(button.dataset.openGpu);
  $('process-query').value = '';
  location.hash = '#processes';
});
$('process-filter').addEventListener('click', (event) => {
  const button = event.target.closest('button[data-host]');
  if (!button) return;
  processHost = button.dataset.host;
  processGpu = null;
  renderProcesses();
});
$('gpu-chip').addEventListener('click', () => { processGpu = null; renderProcesses(); });
$('process-query').addEventListener('input', renderProcesses);
$('trend').addEventListener('pointermove', (event) => {
  cursorX = event.clientX - $('trend').getBoundingClientRect().left;
  drawTrend();
});
$('trend').addEventListener('pointerleave', () => { cursorX = null; drawTrend(); });
new ResizeObserver(drawTrend).observe($('trend'));
window.addEventListener('hashchange', () => { updateView(); window.scrollTo(0, 0); });
window.addEventListener('pagehide', () => { stream?.close(); stream = null; });
window.addEventListener('pageshow', () => { if (!paused && !stream) connect(); });
updateView();
connect();
