# AutoCAD MCP Native Bridge

[![Node tests](https://github.com/yanmiong/autocad-mcp-native/actions/workflows/ci.yml/badge.svg)](https://github.com/yanmiong/autocad-mcp-native/actions/workflows/ci.yml) [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[中文](#中文) | [English](#english)

## 中文

**AutoCAD MCP Native Bridge** 是 Windows 本机 MCP 桥接项目。Node.js MCP 服务通过命名管道与 AutoCAD 进程内的 .NET 插件通信，使兼容 MCP 客户端能够读取图纸状态、检查几何，并通过 AutoCAD API 执行编辑操作。

> **当前验证范围：AutoCAD 2025 / R25.0、Windows x64。** 本仓库包含 0.5.0 操作层和 2025 插件包。AutoCAD 2020–2024、2026 尚无本仓库提供并验证的对应插件构建；不要把 2025 DLL 加载到其它版本。跨版本安装 GUI 是后续路线，不代表当前版本已支持所有 2020+。

### Windows 安装器

仓库新增 `installer/` WinForms 安装器：扫描 AutoCAD 2020–2026 并显示构建号；对已验证的 AutoCAD 2025 `25.0.*` 包执行下载、SHA-256 校验与备份安装；部署便携 Node.js LTS、MCP 服务并更新 Codex 配置，提供实际 `get_bridge_status` 连接测试。**检测到版本不等于该版本已被插件支持**；当前仅放行已测的 AutoCAD 2025 `25.0.*` 构建，2020–2024、2025 其他更新构建和 2026 尚无对应插件包，安装器会阻止不匹配安装。

构建安装器：使用 Windows .NET 8 SDK 运行 `dotnet publish installer/AutoCadMcpInstaller.csproj -c Release -o installer/publish`。单文件输出为 `installer/publish/AutoCadMcpInstaller.exe`。

### 功能

- 读取活动图纸、单位、范围、图层、模型空间实体、尺寸和文字。
- 按类型、图层和条件筛选对象，读取实体详细属性。
- 创建/编辑线、圆、多段线、弧、椭圆、文字、尺寸、填充、图层和块。
- 执行复制、移动、旋转、镜像、偏移、阵列、删除和属性修改。
- 通过 `list_tools` / `cad_call` 发现和调用原生操作。
- `preview_drawing_plan` 与 `apply_drawing_plan` 支持批量计划验证和事务应用。
- MCP 使用 stdio；AutoCAD 端只监听本机命名管道 `AutoCAD_MCP_2025_V2`，不开放 TCP 端口、不使用 OAuth。

### 要求

- Windows x64；AutoCAD 2025 (R25.0)。
- Node.js 20 LTS+；首次 `npm ci` 需要 npm registry 网络。
- 兼容 MCP 客户端（示例为 Codex）。插件从源码构建需要 AutoCAD 2025 API DLL。

### 快速安装

```powershell
git clone https://github.com/yanmiong/autocad-mcp-native.git
cd autocad-mcp-native
npm ci
npm test
npm run check
.\release\Install-AutoCadMcp.ps1
```

先关闭 AutoCAD 再运行插件安装脚本。启动 AutoCAD 2025 并打开一张图纸后，在 `%USERPROFILE%\.codex\config.toml` 添加（将路径替换为实际目录）：

```toml
[mcp_servers.autocad_2025]
command = "node"
args = ["C:\\Tools\\autocad-mcp-native\\src\\index.js"]
startup_timeout_sec = 30
tool_timeout_sec = 180
```

重启 Codex，先调用 `get_bridge_status`、`drawing_summary` 等只读工具确认连接，再进行写操作。完整安装、更新、卸载说明见 [`docs/安装与使用.md`](docs/安装与使用.md)，故障排查见 [`docs/故障排查.md`](docs/故障排查.md)，工具参数见 [`docs/API.md`](docs/API.md)。

### 安全建议

- 初次使用采用只读检查；编辑前备份源 DWG，确认活动图纸路径。
- 复杂修改先预览绘图计划，再显式应用；超时后先检查当前图纸，不要盲目重放。
- 不要加载来源不明的插件，不要为了方便全局关闭 AutoCAD 安全加载。
- 不要将含客户设计、个人路径、日志或凭据的内容提交到公开仓库。

### 开发与测试

```powershell
npm ci
npm test
npm run check
```

本地测试共 7 项，不要求运行 AutoCAD；它们不等于实时 AutoCAD 集成验证。插件源码构建示例：

```powershell
dotnet build .\plugin\AutoCadMcpPlugin.csproj -p:AutoCADDir="C:\Program Files\Autodesk\AutoCAD 2025"
```

### 路线图

- 按 AutoCAD/运行时版本构建并实测插件包。
- 安装 GUI：检测 AutoCAD、下载版本匹配的包、哈希校验、安装/配置客户端、命名管道连接状态、更新与回滚。
- 支持 AutoCAD 2020+ 必须逐版本确认运行时和 API 兼容性，不能仅凭版本检测宣称支持。

项目许可见 [`LICENSE`](LICENSE)；贡献流程见 [`CONTRIBUTING.md`](CONTRIBUTING.md)。

## English

AutoCAD MCP Native Bridge connects a Node.js MCP server to an in-process .NET plug-in in AutoCAD through a local Windows named pipe. It supports drawing inspection and native editing operations for MCP clients.

**Validated target: AutoCAD 2025 / R25.0 on Windows x64.** This release does not include verified plug-in builds for AutoCAD 2020–2024 or 2026. Do not load the 2025 DLL into other releases. See the Chinese installation guide and [`docs/API.md`](docs/API.md). Licensed under MIT.

