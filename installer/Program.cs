using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace AutoCadMcpInstaller;

internal sealed record CadInstall(string Version, string ProductKey, string? Location, string? Build = null)
{
    public string Series => Version switch { "2020" => "R23.1", "2021" => "R24.0", "2022" => "R24.1", "2023" => "R24.2", "2024" => "R24.3", "2025" => "R25.0", "2026" => "R26.0", _ => "Unknown" };
}

internal sealed class MainForm : Form
{
    private readonly ListView list = new() { Dock = DockStyle.Top, Height = 190, View = View.Details, FullRowSelect = true };
    private readonly Label status = new() { Dock = DockStyle.Top, Height = 70, Text = "正在检测本机 AutoCAD...", Padding = new Padding(8) };
    private readonly Button refresh = new() { Text = "重新检测", Width = 110 };
    private readonly Button install = new() { Text = "安装/修复", Width = 110 };
    private readonly Button open = new() { Text = "打开 AutoCAD", Width = 110 };
    private readonly Button testConnection = new() { Text = "测试 MCP 连接", Width = 130 };
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(4) };
    private List<CadInstall> installations = [];
    private const string Repo = "https://api.github.com/repos/yanmiong/autocad-mcp-native";

    public MainForm()
    {
        Text = "AutoCAD MCP 安装器"; Width = 680; Height = 390; StartPosition = FormStartPosition.CenterScreen;
        list.Columns.Add("AutoCAD 版本 / 构建", 210); list.Columns.Add("系列", 95); list.Columns.Add("插件状态", 340);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(8) };
        buttons.Controls.AddRange([refresh, install, open, testConnection]);
        Controls.Add(buttons); Controls.Add(status); Controls.Add(list);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AutoCadMcpInstaller/0.1");
        refresh.Click += (_, _) => Detect(); install.Click += async (_, _) => await InstallSelected(); open.Click += (_, _) => OpenSelected();
        testConnection.Click += async (_, _) => await TestConnection();
        Shown += (_, _) => Detect();
    }

    private void Detect()
    {
        installations = FindInstallations(); list.Items.Clear();
        foreach (var cad in installations)
        {
            var supported = IsSupportedBuild(cad);
            var item = new ListViewItem($"AutoCAD {cad.Version} / {cad.Build ?? "build unknown"}"); item.SubItems.Add(cad.Series);
            var bundle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "ApplicationPlugins", "AutoCadMcp.bundle", "PackageContents.xml");
            item.SubItems.Add(supported ? (File.Exists(bundle) ? "已安装（启动 CAD 后检测连接）" : "可安装 MCP 0.5.2") : "尚无与此 CAD 构建匹配的插件包");
            item.Tag = cad; list.Items.Add(item);
        }
        if (installations.Count == 0) status.Text = "未发现 AutoCAD 2020–2026。安装 AutoCAD 后点“重新检测”。";
        else status.Text = $"检测到 {installations.Count} 个 AutoCAD 安装。只开放实测构建；AutoCAD 2025 的 .NET 10 更新版（1.4+）及其他版本未开放安装，避免装入错误运行时插件。";
        if (list.Items.Count > 0) list.Items[0].Selected = true;
    }

    private static List<CadInstall> FindInstallations()
    {
        var found = new Dictionary<string, CadInstall>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view);
                using var root = baseKey.OpenSubKey("SOFTWARE\\Autodesk\\AutoCAD"); if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(name);
                    var product = key?.GetValue("ProductName")?.ToString() ?? "";
                    var year = ParseCadYear(product + " " + name);
                    if (year is null) year = YearFromSeries(name);
                    if (year is null) continue;
                    found.TryAdd(year, new CadInstall(year, name, key?.GetValue("Location")?.ToString()));
                }
            }
            catch { }
        }
        // The Autodesk registration tree can omit ProductName/Location. Also inspect
        // uninstall metadata and conventional product paths so those installations
        // are still visible without assuming a single edition/language folder.
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall");
                if (uninstall is null) continue;
                foreach (var subName in uninstall.GetSubKeyNames())
                {
                    using var sub = uninstall.OpenSubKey(subName);
                    var display = sub?.GetValue("DisplayName")?.ToString() ?? "";
                    var year = ParseCadYear(display);
                    if (year is null || !display.Contains("AutoCAD", StringComparison.OrdinalIgnoreCase)) continue;
                    var loc = sub?.GetValue("InstallLocation")?.ToString();
                    if (string.IsNullOrWhiteSpace(loc) || !File.Exists(Path.Combine(loc, "acad.exe")))
                        loc = FindConventionalLocation(year);
                    if (found.TryGetValue(year, out var existing))
                    {
                        if (string.IsNullOrWhiteSpace(existing.Location) && !string.IsNullOrWhiteSpace(loc))
                            found[year] = existing with { Location = loc };
                    }
                    else found.Add(year, new CadInstall(year, subName, loc));
                }
            }
            catch { }
        }
        foreach (var year in new[] { "2020", "2021", "2022", "2023", "2024", "2025", "2026" })
        {
            if (found.TryGetValue(year, out var existing) && !string.IsNullOrWhiteSpace(existing.Location)) continue;
            var loc = FindConventionalLocation(year);
            if (loc is null) continue;
            if (existing is not null) found[year] = existing with { Location = loc };
            else found[year] = new CadInstall(year, "Detected by installation path", loc);
        }
        foreach (var year in found.Keys.ToArray())
        {
            var cad = found[year];
            var exe = cad.Location is null ? null : Path.Combine(cad.Location, "acad.exe");
            string? build = null;
            if (exe is not null && File.Exists(exe))
            {
                try { build = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? FileVersionInfo.GetVersionInfo(exe).FileVersion; } catch { }
            }
            found[year] = cad with { Build = build };
        }
        return found.Values.OrderByDescending(x => int.Parse(x.Version)).ToList();
    }

    private static bool IsSupportedBuild(CadInstall cad) =>
        cad.Version == "2025" && cad.Build is not null &&
        (cad.Build.StartsWith("R25.0.", StringComparison.OrdinalIgnoreCase) || cad.Build.StartsWith("25.0.", StringComparison.Ordinal));

    private static string? ParseCadYear(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"(?<!\d)20(20|21|22|23|24|25|26)(?!\d)");
        return match.Success ? match.Value : null;
    }

    private static string? YearFromSeries(string value) => value.ToUpperInvariant() switch
    {
        "R23.1" => "2020", "R24.0" => "2021", "R24.1" => "2022", "R24.2" => "2023",
        "R24.3" => "2024", "R25.0" => "2025", "R26.0" => "2026", _ => null
    };

    private static string? FindConventionalLocation(string year)
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var autodesk = Path.Combine(root, "Autodesk");
            if (!Directory.Exists(autodesk)) continue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(autodesk, "*" + year + "*", SearchOption.TopDirectoryOnly))
                {
                    var exe = Path.Combine(dir, "acad.exe");
                    if (File.Exists(exe)) return dir;
                }
            }
            catch { }
        }
        return null;
    }

    private CadInstall? Selected() => list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as CadInstall;

    private async Task InstallSelected()
    {
        var cad = Selected(); if (cad is null) { status.Text = "先选择一个 AutoCAD 版本。"; return; }
        if (!IsSupportedBuild(cad)) { status.Text = $"AutoCAD {cad.Version}（{cad.Series}, build {cad.Build ?? "unknown"}）已检测到，但没有该构建匹配且已验证的 .NET/API 插件。本安装器已阻止安装。"; return; }
        if (Process.GetProcessesByName("acad").Length > 0) { status.Text = "请先保存并退出 AutoCAD，再重试安装。"; return; }
        install.Enabled = false;
        var pluginInstalled = false;
        try
        {
            status.Text = "正在从 GitHub 获取最新发行包...";
            using var release = await http.GetStreamAsync(Repo + "/releases/latest"); using var doc = await JsonDocument.ParseAsync(release);
            var assets = doc.RootElement.GetProperty("assets").EnumerateArray();
            var asset = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == "autocad-mcp-r25.0.zip");
            if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("GitHub Release 中没有 autocad-mcp-r25.0.zip 安装资产。待维护者发布安装包后即可使用此安装功能。");
            var temp = Path.Combine(Path.GetTempPath(), "autocad-mcp-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            var zip = Path.Combine(temp, "bundle.zip"); await File.WriteAllBytesAsync(zip, await http.GetByteArrayAsync(asset.GetProperty("browser_download_url").GetString()!));
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(zip))).ToLowerInvariant();
            var expected = asset.GetProperty("digest").GetString()?.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(expected) && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("下载文件 SHA-256 校验失败，未安装。");
            ZipFile.ExtractToDirectory(zip, temp);
            var bundle = Directory.GetDirectories(temp, "AutoCadMcp.bundle", SearchOption.AllDirectories).FirstOrDefault() ?? throw new InvalidDataException("安装包内找不到 AutoCadMcp.bundle。");
            var manifestPath = Path.Combine(bundle, "PackageContents.xml");
            var xml = XDocument.Load(manifestPath);
            var minSeries = xml.Root?.Element("Components")?.Element("RuntimeRequirements")?.Attribute("SeriesMin")?.Value;
            if (!string.Equals(minSeries, cad.Series, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"包系列号 {minSeries ?? "missing"} 与所选 AutoCAD {cad.Version} ({cad.Series}) 不匹配。未安装。");
            var dll = Path.Combine(bundle, "Contents", "Windows", "AutoCadMcpPluginV2.dll");
            if (!File.Exists(dll)) throw new InvalidDataException("bundle 中缺少插件 DLL，未安装。");
            var targetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "ApplicationPlugins"); Directory.CreateDirectory(targetRoot);
            var target = Path.Combine(targetRoot, "AutoCadMcp.bundle"); var stage = target + ".stage-" + Guid.NewGuid().ToString("N"); var backup = target + ".backup-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            CopyDirectory(bundle, stage); if (Directory.Exists(target)) Directory.Move(target, backup);
            try { Directory.Move(stage, target); } catch { if (Directory.Exists(backup)) Directory.Move(backup, target); throw; }
            pluginInstalled = true;
            var mcp = await InstallMcpServerAndConfigureClient();
            Detect();
            status.Text = $"AutoCAD 插件已安装（SHA-256 {hash[..12]}…）。{mcp} 启动/重启 AutoCAD 后点“测试 MCP 连接”。";
        }
        catch (Exception e) { status.Text = (pluginInstalled ? "AutoCAD 插件已安装，但 MCP 服务配置未完成：" : "安装未完成：") + e.Message; }
        finally { install.Enabled = true; }
    }

    private async Task<string> InstallMcpServerAndConfigureClient()
    {
        status.Text = "正在检查 Node.js 运行时...";
        var (node, npm, version) = await EnsureNodeRuntime();

        var installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCadMcp", "server");
        Directory.CreateDirectory(installRoot);
        status.Text = "正在下载并安装 MCP 服务依赖...";
        using var release = await http.GetStreamAsync(Repo + "/releases/latest"); using var doc = await JsonDocument.ParseAsync(release);
        var asset = doc.RootElement.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == "autocad-mcp-server.zip");
        if (asset.ValueKind == JsonValueKind.Undefined) return "插件已安装；此 Release 尚无 MCP 服务包，客户端未配置。";
        var temp = Path.Combine(Path.GetTempPath(), "autocad-mcp-server-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var zip = Path.Combine(temp, "server.zip"); await File.WriteAllBytesAsync(zip, await http.GetByteArrayAsync(asset.GetProperty("browser_download_url").GetString()!));
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(zip))).ToLowerInvariant();
        var expected = asset.GetProperty("digest").GetString()?.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(expected) && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("MCP 服务包 SHA-256 校验失败，未配置。");
        var staged = Path.Combine(temp, "server"); Directory.CreateDirectory(staged); ZipFile.ExtractToDirectory(zip, staged);
        if (!File.Exists(Path.Combine(staged, "package.json")) || !File.Exists(Path.Combine(staged, "src", "index.js"))) throw new InvalidDataException("MCP 服务包结构不完整。");
        var deps = await RunNpm(npm, staged);
        if (deps.ExitCode != 0) throw new InvalidOperationException("npm ci 失败：" + deps.Output);
        var old = installRoot + ".backup-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        if (Directory.Exists(installRoot)) Directory.Move(installRoot, old);
        try { Directory.Move(staged, installRoot); }
        catch { if (Directory.Exists(old)) Directory.Move(old, installRoot); throw; }

        var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var config = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        var escapedNode = node.Replace("\\", "\\\\", StringComparison.Ordinal);
        var escapedEntry = Path.Combine(installRoot, "src", "index.js").Replace("\\", "\\\\", StringComparison.Ordinal);
        var block = $"[mcp_servers.autocad_native]{Environment.NewLine}command = \"{escapedNode}\"{Environment.NewLine}args = [\"{escapedEntry}\"]{Environment.NewLine}startup_timeout_sec = 30{Environment.NewLine}tool_timeout_sec = 180{Environment.NewLine}";
        var section = new System.Text.RegularExpressions.Regex(@"(?ms)^\[mcp_servers\.autocad_native\]\r?\n.*?(?=^\[|\z)");
        config = section.IsMatch(config) ? section.Replace(config, block) : config.TrimEnd() + Environment.NewLine + Environment.NewLine + block;
        if (File.Exists(configPath)) File.Copy(configPath, configPath + ".backup-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        File.WriteAllText(configPath, config, new UTF8Encoding(false));
        return $"MCP 服务已部署并写入 Codex 配置（Node {version}）。";
    }

    private async Task<(string Node, string Npm, string Version)> EnsureNodeRuntime()
    {
        var node = FindOnPath("node.exe"); var npm = FindOnPath("npm.cmd");
        var privateNode = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCadMcp", "runtime", "node");
        if (node is null && File.Exists(Path.Combine(privateNode, "node.exe"))) node = Path.Combine(privateNode, "node.exe");
        if (npm is null && File.Exists(Path.Combine(privateNode, "npm.cmd"))) npm = Path.Combine(privateNode, "npm.cmd");
        if (node is not null && npm is not null)
        {
            var v = await RunCapture(node, "--version", Environment.CurrentDirectory);
            if (Version.TryParse(v.Trim().TrimStart('v'), out var installed) && installed.Major >= 20) return (node, npm, installed.ToString());
        }

        // Keep a private portable Node LTS runtime under LocalAppData; no machine-wide install or admin elevation.
        status.Text = "未发现可用 Node.js，正在从 nodejs.org 校验并部署便携版 LTS...";
        using var indexDoc = JsonDocument.Parse(await http.GetStringAsync("https://nodejs.org/dist/index.json"));
        var release = indexDoc.RootElement.EnumerateArray().FirstOrDefault(x => x.TryGetProperty("lts", out var lts) && lts.ValueKind != JsonValueKind.False && lts.ValueKind != JsonValueKind.Null);
        if (release.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("nodejs.org 没有返回 LTS 版本列表。");
        var version = release.GetProperty("version").GetString()!;
        var archiveName = $"node-{version}-win-x64.zip"; var baseUrl = $"https://nodejs.org/dist/{version}/";
        var shasums = await http.GetStringAsync(baseUrl + "SHASUMS256.txt");
        var checksumLine = shasums.Split('\n').FirstOrDefault(line => line.TrimEnd('\r').EndsWith("  " + archiveName, StringComparison.Ordinal));
        if (checksumLine is null) throw new InvalidDataException("官方 SHASUMS256.txt 未列出所需 Node.js 压缩包。");
        var expected = checksumLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        var temp = Path.Combine(Path.GetTempPath(), "autocad-mcp-node-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var archive = Path.Combine(temp, archiveName); await File.WriteAllBytesAsync(archive, await http.GetByteArrayAsync(baseUrl + archiveName));
        var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archive))).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.Ordinal)) throw new InvalidDataException("Node.js 官方 SHA-256 校验失败，未部署运行时。");
        ZipFile.ExtractToDirectory(archive, temp);
        var extracted = Path.Combine(temp, $"node-{version}-win-x64");
        var nodePath = Path.Combine(extracted, "node.exe"); var npmPath = Path.Combine(extracted, "npm.cmd");
        if (!File.Exists(nodePath) || !File.Exists(npmPath)) throw new InvalidDataException("Node.js 官方压缩包结构不完整。");
        var runtime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCadMcp", "runtime", "node");
        Directory.CreateDirectory(Path.GetDirectoryName(runtime)!); var previous = runtime + ".backup-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        if (Directory.Exists(runtime)) Directory.Move(runtime, previous);
        try { Directory.Move(extracted, runtime); } catch { if (Directory.Exists(previous)) Directory.Move(previous, runtime); throw; }
        return (Path.Combine(runtime, "node.exe"), Path.Combine(runtime, "npm.cmd"), version.TrimStart('v'));
    }

    private static string? FindOnPath(string name)
    {
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var candidate = Path.Combine(entry.Trim('"'), name); if (File.Exists(candidate)) return candidate; } catch { }
        }
        return null;
    }

    private static async Task<string> RunCapture(string exe, string args, string cwd)
    {
        var p = new Process { StartInfo = new ProcessStartInfo(exe, args) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        p.Start(); var output = await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync(); return output;
    }

    private static async Task<(int ExitCode, string Output)> RunNpm(string npm, string cwd)
    {
        var p = new Process { StartInfo = new ProcessStartInfo("cmd.exe") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        p.StartInfo.ArgumentList.Add("/d"); p.StartInfo.ArgumentList.Add("/c"); p.StartInfo.ArgumentList.Add($"\"{npm}\" ci --no-audit --no-fund");
        p.Start(); var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
        return (p.ExitCode, (await stdout) + (await stderr));
    }

    private async Task TestConnection()
    {
        status.Text = "正在连接 AutoCAD 本机命名管道..."; testConnection.Enabled = false;
        try
        {
            using var pipe = new NamedPipeClientStream(".", "AutoCAD_MCP_2025_V2", PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2500);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { id = "installer-status", tool = "get_bridge_status", args = new { } }));
            var response = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var json = JsonDocument.Parse(response ?? throw new IOException("管道关闭，没有收到响应"));
            if (!json.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) throw new IOException(response);
            status.Text = "连接成功：AutoCAD 原生插件已响应 get_bridge_status。";
        }
        catch (Exception e) { status.Text = "尚未连通：" + e.Message + "。请打开 AutoCAD 2025，并重启 Codex 后重试。"; }
        finally { testConnection.Enabled = true; }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source)) CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private void OpenSelected()
    {
        var cad = Selected(); if (cad is null) { status.Text = "先选择一个 AutoCAD 版本。"; return; }
        var exe = cad.Location is null ? null : Path.Combine(cad.Location, "acad.exe");
        if (exe is null || !File.Exists(exe))
        {
            var resolved = FindConventionalLocation(cad.Version);
            exe = resolved is null ? null : Path.Combine(resolved, "acad.exe");
        }
        if (exe is null || !File.Exists(exe)) { status.Text = $"AutoCAD {cad.Version} 已检测到，但注册表未给出有效 acad.exe 路径。"; return; }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); status.Text = $"已启动 AutoCAD {cad.Version}。插件状态需在 AutoCAD 内通过 get_bridge_status 确认。";
    }
}

internal static class Program
{
    [STAThread] private static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new MainForm()); }
}
