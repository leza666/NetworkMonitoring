# 家庭网络监控（NetworkMonitoring）· C# 版

用 C#（.NET 10）监测家庭网络质量：**网关（路由器）与光猫（默认网关）延迟 + 国内出口目标延迟 + 网页/视频体验探测**（TCP 握手、HTTPS 首字节 TTFB），提供实时看板与静态数据报表。以 **Windows 服务**方式运行（开机即采集，无需登录）。

- 语言：C# / .NET 10（ASP.NET Core Minimal API 内嵌看板，零外部依赖）
- 数据：CSV 按天分文件，1Hz 采样（TTFB 每 5 秒），与 TS 旧版格式完全兼容
- 无国外目标（无需 Google 8.8.8.8 等国际探针）

## 被测目标与探测矩阵

| key | 目标 | ICMP | TCP connect 443 | TTFB(HTTPS) |
|---|---|---|---|---|
| gateway | 默认网关（路由器，动态探测） | 1Hz | — | — |
| modem | 光猫 192.168.1.1 | 1Hz | — | — |
| baidu | www.baidu.com | 1Hz | 1Hz | 每 5s |
| bilibili | www.bilibili.com | 1Hz | 1Hz | 每 5s |
| alibaba | 223.5.5.5（阿里 DNS） | 1Hz | — | — |

- **ICMP**：网络可达性/RTT；**TCP connect**：网页可达性握手延迟（浏览器真实连接通道 443）；
  **TTFB**（HTTPS GET 首字节耗时）= 最接近"打开网站/视频加载慢"的量化指标。
- 在 `src/NetworkMonitoring/Config.cs` 修改 `Targets` 可增删目标；目标加 `Tcp = true` / `Ttfb = true` 即启用对应探测。

## 快速开始

```powershell
# 前台采集器（Ctrl+C 停止；自带实时看板）—— 调试用
& "C:\Program Files\dotnet\dotnet.exe" run --project src\NetworkMonitoring -- collect

# 打开看板
http://localhost:8000

# 静态报表（当天 / 指定日期 / 多日对比）
& "C:\Program Files\dotnet\dotnet.exe" run --project src\NetworkMonitoring -- report
& "C:\Program Files\dotnet\dotnet.exe" run --project src\NetworkMonitoring -- report 2026-09-01
& "C:\Program Files\dotnet\dotnet.exe" run --project src\NetworkMonitoring -- report --all
```

也可用仓库内 `.\build.ps1`（封装 dotnet 完整路径）。

## 安装为 Windows 服务（推荐，开机即运行）

```powershell
# 管理员 PowerShell
& "C:\Program Files\dotnet\dotnet.exe" run --project src\NetworkMonitoring -- install

# 卸载
& "C:\Program Files\dotnet\dotnet.exe" run --project src\NetworkMonitoring -- uninstall

# 查看状态
Get-Service NetworkMonitor
```

- 服务名 `NetworkMonitor`，默认 **LocalSystem** 账号，开机自动启动，登录与否均采集
- 服务模式无控制台，日志写入 exe 目录 `logs\app-YYYY-MM-DD.log`
- 发布成独立 exe 后注册更稳（见 `.\build.ps1 publish`）

## 采集行为

- 每秒一轮，5 目标并发 ICMP + 百度/B站 TCP 握手（1Hz），TTFB 每 5 秒一轮
- ICMP 用 `System.Net.NetworkInformation.Ping`（原生 ICMP，无 ping.exe 编码问题）；域名走系统 DNS 解析
- 目标丢包/超时**不重试**，该秒记空值；整轮超 1 秒时自动跳过错过的秒，网络恢复后回到 1Hz
- 跨天自动切换数据文件；自动清理 90 天前的 data/reports
- 网关 IP 用 `GetBestInterface` 探测并缓存 60s 重查，失败静默抑制日志 30s

### CSV 格式（`data/YYYY-MM-DD.csv`，每秒一行）

```
timestamp,gateway_ms,modem_ms,baidu_ms,bilibili_ms,alibaba_ms,
baidu_tcp_ms,bilibili_tcp_ms,baidu_ttfb_ms,bilibili_ttfb_ms,failures
2026-09-06T16:14:38.306+08:00,1,3,10,10,7,20.34,11.81,155.94,260.72,
```

空值 = 丢包/失败（TTFB 列在非探测秒为空属正常），`failures` 列记录失败指标 key。
**TTFB/TCP 丢包率按"探测轮"统计**（TTFB 每 5 秒一个探测轮，非探测秒不计入分母）。

## 看板（实时）

`http://localhost:8000`，三个 Chart.js 面板，每 5 秒自动刷新，支持
5 分钟 / 30 分钟 / 1 小时 / 6 小时窗口切换：
1. **ICMP 延迟曲线**（5 条）
2. **TCP 连接握手延迟**（百度/B站 2 条，1Hz）
3. **HTTPS 首字节 TTFB**（百度/B站 2 条，每 5 秒）

顶部卡片（每目标一张）统计**近 30 分钟**滑动窗口：均值/最大/真实丢包率；
严重丢包（丢包率 ≥ 20%）或全丢 → 红框 + "丢包"提示。

内置 API：`/api/meta`（目标与配色）、`/api/latest?from=<ms>`（窗口内秒级数据）、
`/api/summary`（近 30 分钟 + 当日统计与告警目标）。

## 报表（静态 HTML，输出到 `reports/`）

- 按 1 分钟聚合：ICMP 平均/丢包率曲线 + TCP 平均 + TTFB 平均曲线（Chart.js，本地离线）
- 统计表格（按探测类型分组）：样本数、探测次数、有效、平均/最小/最大、抖动、丢包率
- `--all` 生成 `reports/overview.html` 多日对比（ICMP 维度）

## 目录结构

```
NetworkMonitoring/
├── NetworkMonitoring.sln / build.ps1
├── src/NetworkMonitoring/
│   ├── Config.cs          # 目标、端口(8000)、留存天数(90) 等
│   ├── Models.cs          # 类型定义
│   ├── Util.cs / Log.cs   # 时间格式化、清理、文件日志
│   ├── GatewayDetector.cs # 默认网关（GetBestInterface + 遍历降级）
│   ├── Probes.cs          # Ping / TCP connect / HTTPS TTFB 探针
│   ├── Collector.cs       # 1Hz 并发采集核心 + 内存环形缓冲 + CSV
│   ├── DashboardServer.cs # 看板 API（Minimal API）
│   ├── ReportGenerator.cs # 静态报表（当日 + 多日对比）
│   ├── ServiceInstaller.cs# sc.exe 注册/卸载服务
│   └── Program.cs         # 入口（服务/collect/report/install/uninstall）
├── assets/                # dashboard.html + chart.min.js
├── data/                  # 按天 CSV（运行时生成）
└── reports/               # 静态 HTML 报表（运行时生成）
```

## 实现要点（备忘）

- 所有目录基于 exe 所在位置（`AppContext.BaseDirectory`），Windows 服务运行不受工作目录影响
- TCP/TTFB 探测走 .NET 系统 DNS 通道；频率 ≤1Hz / 每 5s，远低于限流阈值，可放心常开
- 大陆网络 ICMP 现实：公共 DNS（223.5.5.5 等）可达；百度/B站响应 ICMP（10ms 级），
  个别 CDN 节点可能不响应——对百度/B站另做 TCP/TTFB 探测是为了量化"打开加载慢"
- CSV 写入 AutoFlush，进程被强杀也不丢数据；报表解析按 CSV header 驱动列名，
  兼容旧版含 google 列的归档数据