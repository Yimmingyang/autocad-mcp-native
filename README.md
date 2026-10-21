# AutoCAD MCP Native Bridge

[![Node tests](https://github.com/yanmiong/autocad-mcp-native/actions/workflows/ci.yml/badge.svg)](https://github.com/yanmiong/autocad-mcp-native/actions/workflows/ci.yml) [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[中文](#中文) | [English](#english)

## 中文

**AutoCAD MCP Native Bridge** 是 Windows 本机 MCP 桥接项目。Node.js MCP 服务通过命名管道与 AutoCAD 进程内的 .NET 插件通信，使兼容 MCP 客户端能够读取图纸状态、检查几何，并通过 AutoCAD API 执行编辑操作。

> **实机验证范围：AutoCAD 2025 / R25.0 / Windows x64，已完成安装、服务配置和 `get_bridge_status` 连接测试。** 安装器按本机 AutoCAD API 和运行时编译 2020–2027 插件；除 2025 实机验证外，其它版本仍需对应 CAD 主机加载/连接测试，不将“能编译”当作“实机兼容”。

### Windows 安装器

仓库新增 `installer/` WinForms 安装器：扫描 AutoCAD 2020–2027 并显示构建号；安装时依据目标 AutoCAD 的 `acmgd.dll`、`acdbmgd.dll`、`accoremgd.dll` 和运行时配置在本机编译插件，生成对应 Bundle 后备份安装；部署便携 Node.js LTS、MCP 服务并更新 Codex 配置，提供 `get_bridge_status` 连接测试。AutoCAD 2020 使用 .NET Framework 4.7、2021–2024 使用 .NET Framework 4.8；2025/2026 按本机运行时选 .NET 8 或 10，2027 使用 .NET 10。编译和 Bundle 系列校验是安装门槛。AutoCAD 2027 对应 R26.0。

构建安装器：使用 Windows .NET 8 SDK 运行 `dotnet publish installer/AutoCadMcpInstaller.csproj -c Release -o installer/publish`。单文件输出为 `installer/publish/AutoCadMcpInstaller.exe`。

### 功能

- 读取活动图纸、单位、范围、图层、模型空间实体、尺寸和文字。
- 按类型、图层和条件筛选对象，读取实体详细属性。
- 创建/编辑线、圆、多段线、弧、椭圆、文字、尺寸、填充、图层和块。
- 执行复制、移动、旋转、镜像、偏移、阵列、删除和属性修改。
- 通过 `list_tools` / `cad_call` 发现和调用原生操作。
- `preview_drawing_plan` 与 `apply_drawing_plan` 支持批量计划验证和事务应用。
- MCP 使用 stdio；AutoCAD 端只监听本机命名管道 `AutoCAD_MCP_<年份>_V2`，不开放 TCP 端口、不使用 OAuth。

### 要求

- Windows x64；AutoCAD 2020–2027（安装时需要相应 API 文件和可识别的 .NET 运行时）。
- 安装器可部署便携 Node.js LTS 和从 Microsoft 官方源校验下载的 .NET SDK；编译阶段需要访问 GitHub 源码与 NuGet。
- 兼容 MCP 客户端（示例为 Codex）。安装器需要所选 AutoCAD 版本的 managed API DLL；独立插件构建需要对应主机的 API。

### 快速安装

从 [GitHub Releases](https://github.com/yanmiong/autocad-mcp-native/releases/latest) 下载 `AutoCadMcpInstaller.exe`，运行后选择 AutoCAD 版本并点“安装/修复”。安装期间关闭 AutoCAD。安装器会备份旧插件和 Codex MCP 配置，并只将本插件的 DLL 目录追加到对应 AutoCAD 配置的 `TRUSTEDPATHS`（不关闭 `SECURELOAD`）；打开 AutoCAD 后点“测试 MCP 连接”。

手动运行 MCP 服务或开发服务端时，可以从源码安装：

```powershell
git clone https://github.com/yanmiong/autocad-mcp-native.git
cd autocad-mcp-native
npm ci
npm test
npm run check
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

本地 MCP 测试共 7 项，不要求运行 AutoCAD；另有安装器兼容性测试覆盖 AutoCAD 系列映射、运行时识别与构建目标选择。两者均不等于实时 AutoCAD 宿主集成验证。插件按本机版本构建示例：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-AutoCADPlugin.ps1 `
  -CadYear 2025 -DotNetMajor 8 `
  -AutoCADDir "C:\Program Files\Autodesk\AutoCAD 2025" `
  -OutputDirectory "$env:TEMP\AutoCadMcpBuild"
```

### 路线图

- 对 AutoCAD 2020–2027 各运行时组合完成实机加载与连接测试，并在兼容矩阵中记录结果。
- 完善安装状态、更新与回滚界面。
- 对新发布的 AutoCAD 主版本及时增加运行时/API 映射和对应测试。

项目许可见 [`LICENSE`](LICENSE)；贡献流程见 [`CONTRIBUTING.md`](CONTRIBUTING.md)。

## English

AutoCAD MCP Native Bridge connects a Node.js MCP server to an in-process .NET plug-in in AutoCAD through a local Windows named pipe. It supports drawing inspection and native editing operations for MCP clients.

**Host integration verified so far: AutoCAD 2025 / R25.0 on Windows x64.** The installer compiles against the selected installation's API and runtime; other supported year/runtime combinations still require host load and pipe-connection tests. Do not manually load a prebuilt DLL into a different release. See the Chinese installation guide and [`docs/API.md`](docs/API.md). Licensed under MIT.

