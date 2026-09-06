# C# 家庭网络监控软件实施计划

> 状态：**已实施完成**（2026-09-06）
> 参考实现：`D:\projects\Archive\NetworkMonitoring`（TypeScript 版，指标口径、CSV 格式、看板 API 结构以此为准）
> 用户决策：5 目标全保留（无国外目标）、复用参考前端、Windows 服务部署、报表全要、服务账号默认 LocalSystem

## 1. 项目目标

用 C#（.NET 10）重写家庭网络监控：
- 每秒监测 网关(路由器)、光猫、国内出口 的 ICMP 延迟
- 百度/B站 额外做 TCP connect(443) 1Hz + HTTPS TTFB 每 5 秒 体验探测
- 实时看板（本地 HTTP，复用参考项目 Chart.js 前端，API 结构 1:1）
- 按天 CSV 数据存储（与参考格式完全一致，兼容既有数据）
- 静态报表（当日报表 + 多日对比 overview）
- **Windows 服务**方式运行（开机即运行，不需登录），默认 LocalSystem 账号
- 不需要国外网络监控（无 Google 8.8.8.8）

## 2. 探测矩阵

| key | 目标 | kind | ICMP 1Hz | TCP 443 1Hz | TTFB 5s |
|---|---|---|---|---|---|
| gateway | 默认网关(路由器) | gateway(动态IP) | ✓ | — | — |
| modem | 光猫 192.168.1.1 | ip | ✓ | — | — |
| baidu | www.baidu.com | dns | ✓ | ✓ | ✓ |
| bilibili | www.bilibili.com | dns | ✓ | ✓ | ✓ |
| alibaba | 阿里DNS 223.5.5.5 | ip | ✓ | — | — |

- ICMP：可达性/RTT；TCP connect：浏览器真实连接通道握手延迟；
  TTFB（HTTPS GET 首字节）= 打开网站/视频加载慢的最直接量化指标
- 探测频率 ≤1Hz / 5s 一次，远低于限流阈值
- 颜色：gateway `#4facfe`、modem `#ffd43b`、baidu `#ff6b6b`、bilibili `#ffa94d`、alibaba `#2bd9af`

## 3. 项目结构

```
D:\projects\NetworkMonitoring\
├── NetworkMonitoring.sln
├── src\NetworkMonitoring\
│   ├── NetworkMonitoring.csproj      # net10.0 + AspNetCore.App 框架引用 + WindowsServices 包
│   ├── Program.cs                    # 入口：服务模式自检 / collect / report / install / uninstall
│   ├── Config.cs                     # 目标定义、端口8000、留存90天、TTFB间隔5轮、超时
│   ├── Models.cs                     # TargetSpec / Sample / ProbeResult / 统计类型
│   ├── GatewayDetector.cs            # 默认网关检测（GetBestInterface + 遍历降级）
│   ├── Probes.cs                     # PingProbe / TcpProbe / TtfbProbe
│   ├── Collector.cs                  # IHostedService：1Hz 并发采集 + 24h环形缓冲 + CSV + 清理
│   ├── DashboardServer.cs            # Minimal API：/api/meta /api/latest /api/summary + 静态
│   ├── ReportGenerator.cs            # 当日报表 + 多日对比（HTML + Chart.js 离线）
│   ├── ServiceInstaller.cs           # sc.exe 注册/卸载 Windows 服务
│   ├── Util.cs                       # 时间格式化 / CSV 解析 / 日志 / 旧文件清理
│   └── Log.cs                        # 文件日志（服务模式无控制台）
├── assets\
│   ├── dashboard.html                # 复用参考项目（删掉 google 颜色项）
│   └── chart.min.js                  # 复制自参考项目
├── data\                             # 按天 CSV（运行时生成，随 exe 所在目录）
├── reports\                          # 静态 HTML 报表（运行时生成）
├── docs\PLAN.md                      # 本文档
└── README.md                         # 使用说明（参考 README 风格）
```

## 4. 技术映射（参考 TS 实现 → C#）

### 4.1 Ping 探测（原 ping.ts）
- 参考：`ping.exe -n 1 -w 4000` + GBK buffer 解码 + 正则解析
- C#：`System.Net.NetworkInformation.Ping`，`SendPingAsync(host, 4000)`
  - 原生 ICMP（IcmpSendEcho），无编码问题、免管理员
  - 域名直接交给 Ping API 系统解析（与 ping.exe 同通道）
  - 超时/失败 → 空值（丢包），不重试

### 4.2 默认网关（原 gateway.ts）
- 参考：`Get-NetRoute 0.0.0.0/0 | Sort RouteMetric,InterfaceMetric | First` NextHop
- C#：P/Invoke `iphlpapi.dll GetBestInterface(0, out idx)` → 由接口索引取 IPv4 网关；
  失败降级：遍历 `NetworkInterface.GetAllNetworkInterfaces()`（Up 且含 IPv4 网关）
- 行为同参考：60s 缓存重查；解析失败静默抑制日志 30s；网关变化打日志

### 4.3 TCP connect（原 tcp.ts）
- `TcpClient.ConnectAsync(host, 443, cts)`，`CancellationTokenSource(3000)`，Stopwatch 计时
- 失败（连接错误/超时）→ 空值，记为 `key_tcp` 失败

### 4.4 HTTPS TTFB（原 tcp.ts ttfbMs）
- 共享 `HttpClient` + `SocketsHttpHandler`（AllowAutoRedirect=false、禁用自动解压、ConnectTimeout）
- `SendAsync(ResponseHeadersRead)`：从请求开始到收到首个响应头停表（含 DNS+TLS+首包）
- 总超时 6s（CancellationTokenSource），失败 → 空值记为 `key_ttfb`
- User-Agent 与参考一致

### 4.5 采集循环（原 collector.ts）
- `IHostedService`，StartAsync 启动循环，StopAsync 优雅停止（CancellationToken）
- 每秒一轮（Stopwatch 校准，整轮 >1s 自动跳秒）
- 每轮并发（Task.WhenAll）执行全部 ICMP + TCP +（每 5 轮）TTFB
- 内存环形缓冲保留 24h（供看板 API），超出修剪
- 每轮控制台/日志输出：`HH:MM:SS #轮次 gateway=… modem=… tcp:baidu=… [失败: …]`

### 4.6 CSV 存储（格式与参考完全一致）
```
timestamp,gateway_ms,modem_ms,baidu_ms,bilibili_ms,alibaba_ms,
baidu_tcp_ms,bilibili_tcp_ms,baidu_ttfb_ms,bilibili_ttfb_ms,failures
2026-09-03T21:31:09.538+08:00,3,4,10,10,8,54,26,239,163,
```
- 空值 = 丢包/失败；TTFB 列非探测秒为空属正常；failures 用 `|` 分隔
- StreamWriter 追加写，UTF8 无 BOM，跨天自动切换文件，新文件写 header
- 启动和跨天时清理 90 天前 data/ 与 reports/ 按日期命名文件
- 数据目录基于 `AppContext.BaseDirectory`（服务运行与工作目录无关）

### 4.7 看板 API（与参考 server.ts 字段 1:1，前端零改动复用）
- `GET /api/meta` — `{ targets, tcpTargets, ttfbTargets, colors, tcpColors, ttfbColors, now }`
- `GET /api/latest?from=<ms>` — `{ now, points: [[ts, ICMP×5, TCP×2, TTFB×2], …] }`
- `GET /api/summary` — 每目标：近30分钟 `avg/max/lossPct` + 当日 `dayAvg/dayLossPct`，
  百度/B站 另含 `tcpAvg/tcpLossPct/ttfbAvg/ttfbLossPct`；
  TTFB 丢包率**按探测轮统计**（非探测秒不计入分母）；`currentLossKeys` = 近30分钟 ICMP 丢包率 ≥ 20% 告警
- 静态：`/` → dashboard.html，`/chart.min.js`；favicon 返回 204
- 绑定 `127.0.0.1:8000`（端口可配置）

### 4.8 报表（原 report.ts）
- **当日报表** `reports/YYYY-MM-DD.html`：
  - 解析 CSV（header 驱动列名，兼容不同版本列组合）
  - 按 1 分钟聚合：ICMP 平均/丢包率曲线 + TCP 平均 + TTFB 平均（Chart.js 离线）
  - 统计表（ICMP/TCP/TTFB 分组）：样本数、探测次数、有效、平均、最小、最大、抖动、丢包率
- **多日对比** `reports/overview.html`：每日 ICMP 平均/最大/丢包率 表格 + 曲线
- 报表 HTML 引用 `../assets/chart.min.js`

### 4.9 Windows 服务
- `builder.Host.UseWindowsService(o => o.ServiceName = "NetworkMonitor")`
- `Program.cs` 分流：`Environment.UserInteractive == false`（SCM 启动）→ 服务模式；否则按命令行参数执行
- `install`：`sc.exe create NetworkMonitor binPath= "<exe>" start= auto`（需管理员，LocalSystem 默认）→ `sc start`
- `uninstall`：`sc stop`（忽略失败）+ `sc delete NetworkMonitor`
- 服务模式无控制台 → 日志写入 `logs\app-YYYY-MM-DD.log`（追加，按天轮转）

### 4.10 日志（Log.cs）
- 简化实现：Console（前台模式）+ 文件（服务模式，简单追加写，线程安全用 lock）
- 级别：Info / Warn / Error

## 5. 命令接口

```
NetworkMonitoring.exe                    # 服务模式（SCM 启动，自动分流）
NetworkMonitoring.exe collect            # 前台采集 + 看板（调试用，Ctrl+C 停止）
NetworkMonitoring.exe report [YYYY-MM-DD]|--all
NetworkMonitoring.exe install            # 注册并启动 Windows 服务（管理员）
NetworkMonitoring.exe uninstall          # 停止并删除服务
```

## 6. 构建与验证（dotnet 不在 PATH，需用完整路径）

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"

# 1. 创建解决方案与项目
& $dotnet new sln -n NetworkMonitoring
& $dotnet new console -n NetworkMonitoring -o src\NetworkMonitoring -f net10.0
& $dotnet sln add src\NetworkMonitoring
# csproj：加 <FrameworkReference Include="Microsoft.AspNetCore.App"/>，
#         NuGet 包 Microsoft.Extensions.Hosting.WindowsServices，
#         assets 拷贝（Link 到输出 assets\，CopyToOutputDirectory=PreserveNewest）

# 2. 前台验证（数据会写入 data\，看板 http://localhost:8000）
& $dotnet run --project src\NetworkMonitoring -- collect

# 3. 报表验证
& $dotnet run --project src\NetworkMonitoring -- report
& $dotnet run --project src\NetworkMonitoring -- report --all

# 4. 服务验证（管理员 PowerShell）
& $dotnet run --project src\NetworkMonitoring -- install
Get-Service NetworkMonitor
sc.exe query NetworkMonitor

# 5. 构建 Release
& $dotnet publish src\NetworkMonitoring -c Release -o dist
```

## 7. 实施顺序

1. `dotnet new sln` + 创建 console 项目（net10.0），csproj 配置框架引用、包、assets 拷贝
2. 复制 `chart.min.js`、`dashboard.html`（删 google 颜色）到 assets\
3. Config / Models / Util / Log（含 CSV 解析，兼容参考列结构）
4. GatewayDetector + Probes（Ping/Tcp/Ttfb）
5. Collector（1Hz 循环、并发探测、24h 缓冲、CSV、清理）
6. DashboardServer（API 与参考 1:1），用参考项目既有 data\ 样本验证 /api/summary 数值一致
7. ReportGenerator（当日报表 + overview）
8. 服务模式（UseWindowsService） + ServiceInstaller
9. 构建 + 前台跑一轮验证 CSV/看板/报表，再注册服务验证
10. README.md + .gitignore

## 8. 完成情况

- [x] 环境确认：.NET 10 SDK（C:\Program Files\dotnet\dotnet.exe）
- [x] 用户决策确认（5 目标 / 复用前端 / 服务部署 / 报表全要 / LocalSystem）
- [x] 删除参考项目开机启动：计划任务 NetworkMonitor 不存在；已删除
      `%APPDATA%\...\Startup\NetworkMonitor.vbs`
- [x] 代码实施与验证（2026-09-06）：
  - 构建通过，0 警告 0 错误
  - 报表数值与 TS 版逐项一致（2026-09-03 数据：网关 avg=2.1/max=136/jit=1.4、
    百度 TCP avg=140.5/max=1111/loss=2.1%、百度 TTFB probe=1252/avg=498.9 等全部吻合）
  - 前台采集 CSV 格式与参考一致（TTFB 每 5 秒一轮）
  - 看板 /api/meta、/api/latest、/api/summary、静态资源全部 200
  - 服务 NetworkMonitor 已注册（Automatic/LocalSystem）并运行，采集正常，
    日志写入 dist\logs\