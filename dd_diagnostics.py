"""Read-only DD checks and bounded runtime logs; never load DD or install devices."""

from datetime import datetime, timezone
import hashlib
import json
import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import sys
import threading


# Pinned to the bundled 63340 package, not a manifest beside the checked files.
PACKAGE_HASHES = {
    'ddhid.63340.dll': 'ae2b10387dbfc60ad8c59c3d579cf20e99bdfcc52cf7d4d8736f1948d0972eca',
    'drv/ddc.exe': '3c535b334f0897b8a0870bcb476c30ea79afd09cfc18f8e00190bdc7c6c46785',
    'drv/ddhid63340.sys': 'fbe510402b3822c63e94752051b7d5895b67875f22ec48593de19764a649f8b1',
    'drv/ddhid63340.inf': '17fe3814f57e98dd2af97f56b63502e474ea5e41cda1a510ffe435ee6ad7a104',
    'drv/ddhid63340.cat': '6135c664711127a62e0988f6844521e345d78ace9d3747a392400ce99be96983',
}
_logger = None
_log_lock = threading.Lock()


class _RuntimeLogHandler(RotatingFileHandler):
    def handleError(self, record):
        # logging normally swallows emit errors; the UI needs an honest saved flag.
        raise OSError('Unable to append DD runtime log') from sys.exc_info()[1]


def runtime_log_path():
    base = Path(os.environ.get('LOCALAPPDATA', Path.home() / 'AppData' / 'Local'))
    return base / 'RocoPlayer' / 'logs' / 'dd-runtime.jsonl'


def record(event, **fields):
    global _logger
    try:
        with _log_lock:
            if _logger is None:
                path = runtime_log_path()
                path.parent.mkdir(parents=True, exist_ok=True)
                handler = _RuntimeLogHandler(path, maxBytes=1024 * 1024, backupCount=2, encoding='utf-8')
                _logger = logging.Logger('roco-dd', level=logging.INFO)
                _logger.addHandler(handler)
            _logger.info(json.dumps({'time': datetime.now(timezone.utc).isoformat(),
                                    'pid': os.getpid(), 'thread': threading.get_ident(),
                                    'event': event, **fields}, ensure_ascii=False))
        return True
    except Exception as exc:
        # A full disk must not strand keys or prevent hook cleanup.
        try:
            print(f'DD runtime log unavailable: {exc}', file=sys.stderr, flush=True)
        except OSError:
            pass
        return False


def check_file(path, expected=None):
    path = Path(path)
    item = {'path': str(path), 'expected_sha256': expected}
    try:
        with path.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        item.update(sha256=digest, status=('verified' if digest == expected else 'mismatch')
                    if expected else 'unverified')
    except FileNotFoundError:
        item['status'] = 'missing'
    except OSError as exc:
        item.update(status='error', error=str(exc))
    return item


def verify_library(path):
    path = Path(path)
    expected = PACKAGE_HASHES.get(path.name.lower())
    item = check_file(path, expected)
    record('library_check', **item)
    if item['status'] in ('missing', 'mismatch', 'error'):
        raise RuntimeError(f"DD DLL 文件校验失败（{item['status']}）：{path}；请在设置中运行 DD 只读诊断")
    # Explicit custom DD versions retain compatibility, but are never called verified.
    return item


def diagnose(path):
    from dd_setup import RUNTIMES, healthy, inspect_driver

    files = []
    issues = []
    if path is None:
        issues.append('未找到 DD DLL，请重新安装包含 DD 的播放器安装包。')
    else:
        path = Path(path).resolve()
        known = path.name.lower() in PACKAGE_HASHES
        files = [check_file(path, PACKAGE_HASHES.get(path.name.lower()))]
        if known:
            files.extend(check_file(path.parent / name, digest)
                         for name, digest in PACKAGE_HASHES.items() if name.startswith('drv/'))
            # Runtime hashes vary with the Python bundle; existence is not verification.
            files.extend(check_file(path.parent / 'drv' / name) for name in RUNTIMES)
        else:
            issues.append('自定义 DD 版本没有内置校验基准，不能确认其完整性或对应的设备状态。')
        for item in files:
            if item['status'] in ('missing', 'mismatch', 'error'):
                issues.append(f"{Path(item['path']).name}: {item['status']}；建议重新安装官方完整包，不要单独替换 DLL。")
    device = None
    try:
        device = inspect_driver()
        if not healthy(device):
            issues.append('ddhid63340 服务或 PnP 设备未就绪；请查看设备问题码，不要反复重装驱动。')
    except Exception as exc:
        issues.append(f'设备状态查询失败：{exc}')
    install_logs = Path(os.environ.get('PROGRAMDATA', 'C:/ProgramData')) / 'RocoPlayer' / 'logs'
    pending = (install_logs / 'dd-install-pending.json').exists()
    if pending:
        issues.append('存在安装超时标记；请先核查安装日志及进程状态，不自动清除或重复安装。')
    report = {'checked_at': datetime.now(timezone.utc).isoformat(), 'files': files,
              'device': device, 'issues': issues, 'pending_install': pending,
              'runtime_log': str(runtime_log_path()), 'install_logs': str(install_logs),
              'note': '只读检查：未加载 DD、未发送按键、未安装驱动。设备就绪不代表 DD 初始化或游戏输入成功。'}
    report['log_saved'] = record('diagnostics', report=report.copy())
    return report
