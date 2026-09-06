# AGENTS.md

家庭网络监控（C#/.NET 10，Windows 服务）。探测 网关/光猫/百度/B站/阿里DNS 的 ICMP、TCP connect(443)、HTTPS TTFB，提供实时看板（localhost:8000）与静态报表。指标口径参考 `D:\projects\Archive\NetworkMonitoring`（TS 旧版，保留只读对照）。

## 构建与运行

- **dotnet 不在 PATH**，必须用完整路径：`& "C:\Program Files\dotnet\dotnet.exe"`。
- 多项目结构（`NetworkMonitoring.slnx`）：
  - `src\NetworkMonitoring.Core\` 类库：Models/Config/Util/Log/GatewayDetector/Probes/Collector（唯一无 ASP.NET 依赖，引 Hosting.Abstractions 包）
  - `src\NetworkMonitoring.Dashboard\` 类库：DashboardServer（引 Core + AspNetCore.App）
  - `src\NetworkMonitoring.Reports\` 类库：ReportGenerator（引 Core）
  - `src\NetworkMonitoring.App\` exe 入口：Program/ServiceInstaller（引三者 + WindowsServices 包，`AssemblyName` 保持 `NetworkMonitoring`，assets 拷贝在此）
- 命令（`dotnet run --project src\NetworkMonitoring.App -- <cmd>`）：
  - `collect` 前台采集+看板（Ctrl+C 停止）
  - `report [YYYY-MM-DD|--all]` 生成报表
  - `install` / `uninstall` 注册/卸载服务（**install 必须管理员**，非管理员用 `Start-Process -Verb RunAs` 提升）

## 关键架构约束

- **目录基于 `AppContext.BaseDirectory`**：`data\`、`reports\`、`assets\`、`logs\` 都相对 exe 位置。`dotnet run` 时在 `bin\Debug\net10.0\`，publish 后在 `dist\`。验证产出物时先确认看的是哪个目录。
- **CSV header 即 schema**：`Collector.BuildHeader()` 写表头，`ReportGenerator.ParseCsv` 按 header 动态解析（兼容 archive 旧数据含 google 列）。改目标/列会破坏旧数据兼容性。
- **`Config.Targets` 顺序 = CSV 列顺序 = `/api/latest` 点数组列序**（`[ts, ICMP×N, TCP×M, TTFB×K]`）。前端 `assets\dashboard.html` 的 `renderCharts` 硬编码了按顺序取列的逻辑，增删/重排目标必须同步检查前端和 `/api/meta`（meta 驱动目标列表，但点数组按 TARGETS 顺序）。
- **TTFB 丢包率按探测轮统计**（`TtfbIntervalRounds=5`，每 5s 一个探测轮）：`Collector` 写失败标记 `key_ttfb`，`DashboardServer.TtfbStat` 和 `ReportGenerator.IsProbeRow` 都按"该秒是否探测轮"计分母，非探测秒不计入。三处逻辑必须保持一致。
- 网关 IP 动态探测（`GatewayDetector.GetBestInterface`），60s 缓存、30s 失败静默；`PingProbe` 用原生 `System.Net.NetworkInformation.Ping`（无 ping.exe 编码问题）。

## 陷阱与验证

- **ReportGenerator 的 raw string 陷阱**：JS 代码块（`JsOpts`/`JsOverviewCharts`/`ShellTemplate`）含大量花括号，必须用**非插值** raw string（`"""`）+ 字符串拼接；用 `$"""` 会触发 CS9006。
- 报表数值验证：`node src/report.ts 2026-09-03`（archive 项目）与 `report 2026-09-03` 输出应逐项一致。
- 无单元测试；手动验证流程：`collect` 跑 10s → `Invoke-RestMethod http://127.0.0.1:8000/api/summary` → 检查当日 CSV 行格式与 TTFB 5s 节奏。
- 采集 CSV 用 StreamWriter `AutoFlush=true`（改回 false 会在强杀时丢数据）。

## 服务状态

`NetworkMonitor` 服务（Automatic/LocalSystem）当前运行的是 `dist\NetworkMonitoring.exe`（Release publish）。改代码后需重新 `publish` 并 `install`（先 uninstall）才会生效；开发期改代码只影响 `collect` 前台模式，服务不受影响。