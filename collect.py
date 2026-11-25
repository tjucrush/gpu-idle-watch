"""Respond to read-only GPU, process and local disk sampling requests over SSH."""

import csv
import json
import math
import os
import posixpath
import re
import subprocess
import sys
import threading
import time


GPU_FIELDS = ('index', 'uuid', 'name', 'memory.used', 'memory.total', 'utilization.gpu',
              'utilization.memory', 'temperature.gpu', 'power.draw', 'power.limit', 'fan.speed', 'pstate')
PROCESS_FIELDS = ('gpu_uuid', 'pid', 'process_name', 'used_memory')
GENERIC_DIRS = {'anaconda3', 'miniconda3', 'miniforge3', 'mambaforge', 'envs', 'venv', '.venv',
                'data', 'datasets', 'models', 'weights', 'checkpoints', 'cache', 'logs', 'tmp', 'runs',
                'bin', 'lib', 'opt', 'src', 'scripts', 'tools', 'config', 'configs'}
WORKSPACE_DIRS = {'workspace', 'workspaces', 'projects', 'repos', 'code', 'work'}
PATH_OPTIONS = {'--project', '--config', '--model', '--model-path', '--model_path', '--weights',
                '--checkpoint', '--output-dir', '--output_dir', '--run-dir', '--run_dir', '--root'}


def local_disk_space(mount):
    # Pin the target before checking its mount ID, so an unmount cannot substitute the root disk.
    fd = os.open(mount, os.O_PATH | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        with open('/proc/self/fdinfo/{}'.format(fd)) as info:
            mount_id = next((line.split()[1] for line in info if line.startswith('mnt_id:')), None)
        # Match the kernel's escaping without interpreting literal backslashes twice.
        mount_field = mount.translate(str.maketrans({' ': r'\040', '\t': r'\011', '\n': r'\012', '\\': r'\134'}))
        with open('/proc/self/mountinfo', encoding='utf-8', errors='surrogateescape') as mounts:
            mounted = any(fields[0] == mount_id and fields[4] == mount_field
                          for fields in (line.split(' ', 5) for line in mounts) if len(fields) > 4)
        if not mounted:
            return [], 'unavailable'
        stats = os.fstatvfs(fd)
        if stats.f_blocks <= 0 or stats.f_frsize <= 0:
            return [], 'unavailable'
        return [{'mount': mount, 'totalBytes': stats.f_blocks * stats.f_frsize,
                 'availableBytes': max(0, stats.f_bavail) * stats.f_frsize}], 'ok'
    finally:
        os.close(fd)


class DiskSampler(threading.Thread):
    interval = 30

    def __init__(self, mount):
        super().__init__(daemon=True)
        self.mount = mount
        self.lock = threading.Lock()
        self.latest = ([], 'pending', None, None)

    def run(self):
        while True:
            try:
                volumes, status = local_disk_space(self.mount)
            except OSError:
                volumes, status = [], 'unavailable'
            with self.lock:
                self.latest = (volumes, status, int(time.time()), time.monotonic())
            time.sleep(self.interval)

    def snapshot(self):
        with self.lock:
            volumes, status, sampled_at, completed = self.latest
        age = None if completed is None else int(time.monotonic() - completed)
        if age is not None and age >= 3 * self.interval:
            status = 'stale'
        return {'disks': volumes, 'diskStatus': status, 'diskSampledAt': sampled_at, 'diskAgeSeconds': age}


def run_command(args, timeout):
    return subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                          timeout=timeout, check=False)


def smi_query(kind, fields):
    result = run_command(['nvidia-smi', '--query-' + kind + '=' + ','.join(fields),
                          '--format=csv,noheader,nounits'], 4)
    if result.returncode:
        return None
    return [[value.strip() for value in row]
            for row in csv.reader(result.stdout.decode('utf-8', 'replace').splitlines(), skipinitialspace=True)
            if len(row) == len(fields)]


def number(value):
    try:
        parsed = float(value)
        return parsed if math.isfinite(parsed) and parsed >= 0 else None
    except ValueError:
        return None


def label(value, limit=100):
    return ''.join(char for char in value if char.isprintable()).strip()[:limit]


def project_from_path(candidate, cwd=None):
    if not candidate or (not candidate.startswith('/') and not cwd):
        return None
    path = posixpath.normpath(posixpath.join(cwd or '/', candidate))
    parts = path.strip('/').split('/')
    if len(parts) < 2:
        return None
    if parts[0] == 'home':
        parts = parts[2:]
        if parts and parts[0].lower() in WORKSPACE_DIRS:
            parts = parts[1:]
    elif re.fullmatch(r'mnt\d*|data\d*', parts[0]):
        parts = parts[1:]
        if parts and re.fullmatch(r'(?:sd[a-z]\d*|nvme\d+(?:n\d+p?\d*)?|disk\d+)', parts[0]):
            parts = parts[1:]
        if parts and (parts[0].startswith('.') or parts[0].lower() in GENERIC_DIRS):
            return None
        if len(parts) >= 2 and parts[1].lower() not in GENERIC_DIRS and '.' not in parts[1]:
            parts = parts[1:]
    else:
        return None
    if not parts or not parts[0] or parts[0].startswith('.') or parts[0].lower() in GENERIC_DIRS:
        return None
    if len(parts) == 1 and re.search(r'\.(py|sh|ya?ml|json|pt|pth|ckpt|safetensors|onnx)$', parts[0], re.I):
        return None
    return label(parts[0]) or None


def process_project(pid, executable):
    try:
        cwd = os.readlink('/proc/{}/cwd'.format(pid))
    except OSError:
        cwd = None
    project = project_from_path(cwd)
    if project:
        return project, 'cwd'
    try:
        with open('/proc/{}/cmdline'.format(pid), 'rb') as file:
            argv = file.read(16384).decode('utf-8', 'replace').strip('\0').split('\0')
    except OSError:
        argv = []
    for arg in argv[1:]:
        if re.search(r'\.(py|sh)$', arg, re.I) and not arg.startswith('-') and '://' not in arg:
            project = project_from_path(arg, cwd)
            if project:
                return project, 'script'
    for index, arg in enumerate(argv[1:], start=1):
        option, separator, value = arg.partition('=')
        if option in PATH_OPTIONS:
            candidate = value if separator else (argv[index + 1] if index + 1 < len(argv) else '')
            project = project_from_path(candidate) if candidate.startswith('/') else None
            if project:
                return project, 'config'
    project = project_from_path(executable) if executable.startswith('/') else None
    return (project, 'executable') if project else (None, None)


def process_info(pids):
    rows = {}
    if pids:
        result = run_command(['ps', '-p', ','.join(str(pid) for pid in sorted(pids)),
                              '-o', 'pid=', '-o', 'user:32=', '-o', 'etimes=', '-o', 'comm='], 2)
        for line in result.stdout.decode('utf-8', 'replace').splitlines():
            parts = line.split(None, 3)
            if len(parts) == 4 and parts[0].isdigit():
                rows[int(parts[0])] = (label(parts[1]), number(parts[2]), label(parts[3]))
    return rows


def sample():
    try:
        rows = smi_query('gpu', GPU_FIELDS)
    except (OSError, subprocess.TimeoutExpired):
        rows = None
    if rows is None:
        return {'error': 'gpu_query'}
    gpus = []
    for index, uuid, name, used, total, util, memory_util, temp, watts, limit, fan, pstate in rows:
        if index.isdigit() and uuid:
            gpus.append({'index': int(index), 'uuid': label(uuid), 'name': label(name),
                         'memoryUsed': number(used), 'memoryTotal': number(total), 'utilization': number(util),
                         'memoryUtilization': number(memory_util), 'temperature': number(temp),
                         'power': number(watts), 'powerLimit': number(limit), 'fan': number(fan),
                         'pstate': label(pstate) if pstate.startswith('P') else None})
    gpus.sort(key=lambda gpu: gpu['index'])
    if not gpus:
        return {'error': 'no_gpus'}
    try:
        rows = smi_query('compute-apps', PROCESS_FIELDS)
    except (OSError, subprocess.TimeoutExpired):
        rows = None
    if rows is None:
        return {'gpus': gpus, 'processes': [], 'processStatus': 'unavailable'}
    by_uuid = {gpu['uuid']: gpu['index'] for gpu in gpus}
    apps = [(by_uuid[uuid], int(pid), executable, number(used)) for uuid, pid, executable, used in rows
            if uuid in by_uuid and pid.isdigit() and int(pid) > 0]
    try:
        info = process_info({app[1] for app in apps})
    except (OSError, subprocess.TimeoutExpired):
        info = {}
    projects = {}
    processes = []
    seen = set()
    for gpu, pid, executable, used in apps:
        if (gpu, pid) in seen:
            continue
        seen.add((gpu, pid))
        user, elapsed, command = info.get(pid, (None, None, None))
        if pid not in projects:
            projects[pid] = process_project(pid, executable)
        name, source = projects[pid]
        processes.append({'gpu': gpu, 'pid': pid, 'user': user, 'runningSeconds': elapsed,
                          'program': label(posixpath.basename(executable)) or command,
                          'memoryUsed': used, 'project': name, 'projectSource': source})
    return {'gpus': gpus, 'processes': processes, 'processStatus': 'ok'}


if __name__ == '__main__':
    disks = DiskSampler(sys.argv[1]) if len(sys.argv) > 1 else None
    if disks is not None:
        disks.start()
    for request in sys.stdin:
        if request.strip() == 'sample':
            result = sample()
            if disks is not None:
                result.update(disks.snapshot())
            print('GPU_SAMPLE ' + json.dumps(result, ensure_ascii=True, allow_nan=False), flush=True)
