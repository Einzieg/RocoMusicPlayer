import { useState } from 'react';
import type { DdDiagnostics as Report } from '../types';
import { rpc } from '../lib/rpc';
import { friendlyError } from '../lib/model';

const labels: Record<string, string> = {
  verified: 'SHA256 匹配', mismatch: '校验不匹配', missing: '文件缺失',
  error: '读取失败', unverified: '可读取（没有内置哈希基准）',
};

export default function DdDiagnostics({ disabled }: { disabled: boolean }) {
  const [busy, setBusy] = useState(false);
  const [report, setReport] = useState<Report | null>(null);
  const [error, setError] = useState('');

  async function inspect() {
    if (disabled || busy) return;
    setBusy(true);
    setError('');
    setReport(null);
    try { setReport(await rpc('dd_diagnostics', {})); }
    catch (cause) { setError(friendlyError(cause)); }
    finally { setBusy(false); }
  }

  return <section className="panel settings-section dd-diagnostics">
    <div className="section-heading"><div><h2>DD 驱动诊断</h2>
      <p>只读检查文件完整性、服务与设备状态；不会初始化 DD、发送按键或重装驱动。</p>
    </div><button type="button" className="button secondary" disabled={disabled || busy} onClick={() => void inspect()}>
      {busy ? '正在检查…' : '运行只读诊断'}
    </button></div>
    {error && <p className="error-text" role="alert">{error}</p>}
    {report && <div aria-live="polite">
      <p>{report.issues.length ? '发现需要关注的项目：' : '未发现已检查项目的异常；这不等于 DD 输入测试通过。'}</p>
      <ul>{report.issues.map((issue, index) => <li key={index}>{issue}</li>)}</ul>
      <p>检查时间：{report.checked_at}</p>
      <p>服务：{report.device ? `ddhid63340 · ${report.device.registered ? '已注册' : '未注册'} · 状态 ${report.device.state ?? '无'}` : '查询失败'}</p>
      {report.device?.devices.map((device) => <p key={device.id}>{device.id} · {device.started ? '已启动' : '未启动'} · 问题码 {device.problem} · 查询结果 {device.query_result}</p>)}
      {report.device && report.device.devices.length === 0 && <p>未找到关联的当前 PnP 设备。</p>}
      <details><summary>文件校验详情</summary><ul>{report.files.map((file) => <li key={file.path}>
        <code>{file.path}</code> — {labels[file.status] || file.status}{file.error && `：${file.error}`}
      </li>)}</ul></details>
      <p>运行日志：<code>{report.runtime_log}</code></p>
      {!report.log_saved && <p className="error-text">日志写入失败，请保留此页面的诊断信息。</p>}
      <p>安装日志：<code>{report.install_logs}</code></p>
      <small>{report.note} 文件损坏时请正常退出播放器后重装完整包，不会自动覆盖正在使用的 DLL。</small>
    </div>}
  </section>;
}
