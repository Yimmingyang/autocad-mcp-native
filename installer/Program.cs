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
    public string Series => CadCompatibility.Series(Version);
}

internal sealed class MainForm : Form
{
    private readonly ListView list = new() { Dock = DockStyle.Top, Height = 190, View = View.Details, FullRowSelect = true };
    private readonly Label status = new() { Dock = DockStyle.Top, Height = 70, Text = "正在检测本机 AutoCAD...", Padding = new Padding(8) };
    private readonly Button refresh = new() { Text = "重新检测", Width = 110 };
    private readonly Button install = new() { Text = "安装/修复", Width = 110 };
    private readonly Button open = new() { Text = "打开 AutoCAD", Width = 110 };
    private readonly Button testConnection = new() { Text = "测试 MCP 连接", Width = 130 };
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private List<CadInstall> installations = [];
    private const string LatestReleaseUrl = "https://github.com/yanmiong/autocad-mcp-native/releases/latest";
    private const string LatestAssetBaseUrl = "https://github.com/yanmiong/autocad-mcp-native/releases/latest/download/";

    public MainForm()
    {
        Text = "AutoCAD MCP 安装器"; Width = 680; Height = 390; StartPosition = FormStartPosition.CenterScreen;
        list.Columns.Add("AutoCAD 版本 / 构建", 210); list.Columns.Add("系列", 95); list.Columns.Add("插件状态", 340);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(8) };
        buttons.Controls.AddRange([refresh, install, open, testConnection]);
        Controls.Add(buttons); Controls.Add(status); Controls.Add(list);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AutoCadMcpInstaller/0.5.6");
        refresh.Click += (_, _) => Detect(); install.Click += async (_, _) => await InstallSelected(); open.Click += (_, _) => OpenSelected();
        testConnection.Click += async (_, _) => await TestConnection();
        Shown += (_, _) => Detect();
    }

    private void Detect()
    {
        var selectedYear = Selected()?.Version;
        installations = FindInstallations(); list.Items.Clear();
        foreach (var cad in installations)
        {
            var supported = IsSupportedBuild(cad);
            var item = new ListViewItem($"AutoCAD {cad.Version} / {cad.Build ?? "build unknown"}"); item.SubItems.Add(cad.Series);
            var bundle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "ApplicationPlugins", "AutoCadMcp.bundle", "PackageContents.xml");
            item.SubItems.Add(supported ? (IsBundleInstalledFor(cad, bundle) ? "该系列插件已安装（启动 CAD 后检测连接）" : "可按本机 API 构建并安装") : "缺少 AutoCAD API 或 .NET 运行时信息");
            item.Tag = cad; list.Items.Add(item);
        }
        if (installations.Count == 0) status.Text = "未发现 AutoCAD 2020–2026。安装 AutoCAD 后点“重新检测”。";
        else status.Text = $"检测到 {installations.Count} 个 AutoCAD 安装。安装时按本机 API/运行时编译插件；AutoCAD 2020–2026 中仅处理能识别 API 文件和运行时的版本。";
        if (list.Items.Count > 0)
        {
            var item = list.Items.Cast<ListViewItem>().FirstOrDefault(x => (x.Tag as CadInstall)?.Version == selectedYear) ?? list.Items[0];
            item.Selected = true;
        }
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

    private static bool IsBundleInstalledFor(CadInstall cad, string bundle)
    {
        try
        {
            var manifest = XDocument.Load(Path.Combine(bundle, "PackageContents.xml"));
            var requirements = manifest.Root?.Element("Components")?.Element("RuntimeRequirements");
            return string.Equals(requirements?.Attribute("SeriesMin")?.Value, cad.Series, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(bundle, "Contents", "Windows", "AutoCadMcpPluginV2.dll"));
        }
        catch { return false; }
    }

    private static bool IsSupportedBuild(CadInstall cad) => CadCompatibility.IsSupported(cad.Version, cad.Location);

    private async Task<(string Bundle, string Hash)> BuildForInstalledCad(CadInstall cad, string temp)
    {
        if (string.IsNullOrWhiteSpace(cad.Location)) throw new InvalidOperationException($"未找到 AutoCAD {cad.Version} 安装目录，无法匹配本机 API 编译。");
        var year = int.Parse(cad.Version);
        var dotnetMajor = year <= 2024 ? 8 : CadCompatibility.ReadRuntimeMajor(cad.Location ?? throw new InvalidOperationException("AutoCAD install path is missing.")) ?? throw new InvalidOperationException($"无法从 {cad.Location}\\acdbmgd.runtimeconfig.json 判断 AutoCAD {cad.Version} 的 .NET 运行时。");
        var sdk = await EnsureDotNetSdk(dotnetMajor);
        status.Text = $"正在获取 MCP 源码并针对 AutoCAD {cad.Version} 编译...";
        var tag = await GetLatestReleaseTag();
        var sourceZip = Path.Combine(temp, "source.zip");
        var sourceHash = await DownloadVerifiedReleaseAsset("autocad-mcp-source.zip", sourceZip);
        var sourceRoot = Path.Combine(temp, "source"); Directory.CreateDirectory(sourceRoot); ZipFile.ExtractToDirectory(sourceZip, sourceRoot);
        var project = Directory.GetFiles(sourceRoot, "AutoCadMcpPlugin.Legacy.csproj", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidDataException($"Release {tag} 未包含多版本插件构建工程；请先发布包含 Legacy.csproj 的源码版本。");
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, ".."));
        var buildOutput = Path.Combine(temp, "compiled");
        var args = new[] { "build", project, "-c", "Release", $"-p:CadYear={cad.Version}", $"-p:AutoCADDir={cad.Location}", $"-p:DotNetMajor={dotnetMajor}", "-o", buildOutput, "--nologo", "--verbosity", "quiet" };
        var result = await RunCommand(sdk, args, repoRoot);
        if (result.ExitCode != 0) throw new InvalidOperationException($"AutoCAD {cad.Version} 插件构建失败（{result.ExitCode}）：{result.Output}");

        var bundle = Path.Combine(temp, "generated", "AutoCadMcp.bundle");
        var payload = Path.Combine(bundle, "Contents", "Windows"); Directory.CreateDirectory(payload);
        foreach (var file in Directory.GetFiles(buildOutput).Where(x => !x.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
            File.Copy(file, Path.Combine(payload, Path.GetFileName(file)), true);
        var series = cad.Series;
        var manifest = new XDocument(new XElement("ApplicationPackage",
            new XAttribute("SchemaVersion", "1.0"), new XAttribute("AutodeskProduct", "AutoCAD"),
            new XAttribute("Name", $"AutoCAD MCP {cad.Version}"), new XAttribute("AppVersion", tag.TrimStart('v')),
            new XAttribute("ProductCode", "{" + Guid.NewGuid().ToString() + "}"),
            new XAttribute("UpgradeCode", "{7B20D7B7-18F7-4B77-A17D-AED95E33C105}"),
            new XElement("CompanyDetails", new XAttribute("Name", "AutoCAD MCP Native")),
            new XElement("Components",
                new XElement("RuntimeRequirements", new XAttribute("OS", "Win64"), new XAttribute("Platform", "AutoCAD*"), new XAttribute("SeriesMin", series), new XAttribute("SeriesMax", series)),
                new XElement("ComponentEntry", new XAttribute("AppName", "AutoCadMcpPluginV2"), new XAttribute("ModuleName", "./Contents/Windows/AutoCadMcpPluginV2.dll"), new XAttribute("AppType", ".Net"), new XAttribute("LoadOnAutoCADStartup", "True")))));
        manifest.Save(Path.Combine(bundle, "PackageContents.xml"));
        var plugin = Path.Combine(payload, "AutoCadMcpPluginV2.dll");
        if (!File.Exists(plugin)) throw new InvalidDataException("构建成功但输出目录缺少 AutoCadMcpPluginV2.dll。");
        await using var stream = File.OpenRead(plugin);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        return (bundle, hash);
    }

    private async Task<string> EnsureDotNetSdk(int major)
    {
        var sdkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCadMcp", "runtime", $"dotnet-sdk-{major}");
        var dotnetPath = Path.Combine(sdkRoot, "dotnet.exe");
        if (File.Exists(dotnetPath))
        {
            var listed = await RunCapture(dotnetPath, "--list-sdks", Environment.CurrentDirectory);
            if (listed.Split('\n').Any(x => x.TrimStart().StartsWith(major + ".", StringComparison.Ordinal))) return dotnetPath;
        }
        var pathDotnet = FindOnPath("dotnet.exe");
        if (pathDotnet is not null)
        {
            var listed = await RunCapture(pathDotnet, "--list-sdks", Environment.CurrentDirectory);
            if (listed.Split('\n').Any(x => x.TrimStart().StartsWith(major + ".", StringComparison.Ordinal))) return pathDotnet;
        }

        status.Text = $"未检测到 .NET {major} SDK，正在从 Microsoft 官方发行清单下载并校验便携 SDK...";
        using var metadata = JsonDocument.Parse(await http.GetStringAsync($"https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/{major}.0/releases.json"));
        var root = metadata.RootElement;
        var latest = root.GetProperty("latest-sdk").GetString() ?? throw new InvalidDataException("Microsoft .NET 发布清单没有 latest-sdk。");
        var release = root.GetProperty("releases").EnumerateArray().FirstOrDefault(x => x.TryGetProperty("sdk", out var sdk) && sdk.TryGetProperty("version", out var version) && version.GetString() == latest);
        if (release.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Microsoft .NET 发布清单中找不到最新 SDK 条目。");
        var package = release.GetProperty("sdk").GetProperty("files").EnumerateArray().FirstOrDefault(x => x.GetProperty("rid").GetString() == "win-x64" && x.GetProperty("name").GetString() == "dotnet-sdk-win-x64.zip");
        if (package.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Microsoft .NET 发布清单中找不到 win-x64 SDK 压缩包。");
        var zip = Path.Combine(Path.GetTempPath(), $"dotnet-sdk-{latest}-win-x64.zip");
        await using (var input = await http.GetStreamAsync(package.GetProperty("url").GetString()!))
        await using (var output = File.Create(zip)) await input.CopyToAsync(output);
        await using (var verify = File.OpenRead(zip))
        {
            var actual = Convert.ToHexString(await SHA512.HashDataAsync(verify)).ToLowerInvariant();
            var expected = package.GetProperty("hash").GetString();
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Microsoft .NET SDK SHA-512 校验失败，未提取。");
        }
        var stage = sdkRoot + ".stage-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(stage); ZipFile.ExtractToDirectory(zip, stage);
        if (!File.Exists(Path.Combine(stage, "dotnet.exe"))) throw new InvalidDataException(".NET SDK 压缩包结构不完整。");
        var backup = sdkRoot + ".backup-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        if (Directory.Exists(sdkRoot)) Directory.Move(sdkRoot, backup);
        try { Directory.Move(stage, sdkRoot); } catch { if (Directory.Exists(backup)) Directory.Move(backup, sdkRoot); throw; }
        return dotnetPath;
    }

    private async Task<string> GetLatestReleaseTag()
    {
        using var response = await http.GetAsync(LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri ?? throw new InvalidDataException("GitHub 最新 Release 没有返回最终地址。");
        var tag = Uri.UnescapeDataString(finalUri.Segments.Last().Trim('/'));
        if (!tag.StartsWith('v') || tag.Length < 2) throw new InvalidDataException("GitHub 最新 Release 地址中未识别到版本 tag。");
        return tag;
    }

    private async Task<string> DownloadVerifiedReleaseAsset(string assetName, string destination)
    {
        var url = LatestAssetBaseUrl + Uri.EscapeDataString(assetName);
        var bytes = await http.GetByteArrayAsync(url);
        var checksum = (await http.GetStringAsync(url + ".sha256")).Trim();
        string actual;
        try { actual = ReleaseAssetIntegrity.VerifySha256(bytes, checksum); }
        catch (InvalidDataException e) { throw new InvalidDataException($"发行资产 {assetName} 的校验失败。", e); }
        await File.WriteAllBytesAsync(destination, bytes);
        return actual;
    }

    private static async Task<(int ExitCode, string Output)> RunCommand(string exe, IEnumerable<string> arguments, string cwd)
    {
        var p = new Process { StartInfo = new ProcessStartInfo(exe) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in arguments) p.StartInfo.ArgumentList.Add(argument);
        p.Start(); var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
        return (p.ExitCode, (await stdout) + (await stderr));
    }

    private static string? ParseCadYear(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"(?<!\d)20(20|21|22|23|24|25|26)(?!\d)");
        return match.Success ? match.Value : null;
    }

    private static string? YearFromSeries(string value) => CadCompatibility.YearFromSeries(value);

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
        if (!IsSupportedBuild(cad)) { status.Text = $"AutoCAD {cad.Version}（{cad.Series}, build {cad.Build ?? "unknown"}）已检测到，但缺少本机 API 文件或无法识别 .NET 运行时，暂未开始安装。"; return; }
        if (Process.GetProcessesByName("acad").Length > 0) { status.Text = "请先保存并退出 AutoCAD，再重试安装。"; return; }
        install.Enabled = false;
        var pluginInstalled = false;
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), "autocad-mcp-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            status.Text = $"正在为 AutoCAD {cad.Version} 获取源码和构建工具，并针对本机 API 编译插件...";
            var (bundle, hash) = await BuildForInstalledCad(cad, temp);
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
            var mcp = await InstallMcpServerAndConfigureClient(cad.Version);
            Detect();
            status.Text = $"AutoCAD 插件已安装（SHA-256 {hash[..12]}…）。{mcp} 启动/重启 AutoCAD 后点“测试 MCP 连接”。";
        }
        catch (Exception e) { status.Text = (pluginInstalled ? "AutoCAD 插件已安装，但 MCP 服务配置未完成：" : "安装未完成：") + e.Message; }
        finally { install.Enabled = true; }
    }

    private async Task<string> InstallMcpServerAndConfigureClient(string cadYear)
    {
        status.Text = "正在检查 Node.js 运行时...";
        var (node, npm, version) = await EnsureNodeRuntime();

        var installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCadMcp", "server");
        Directory.CreateDirectory(installRoot);
        status.Text = "正在下载并安装 MCP 服务依赖...";
        var temp = Path.Combine(Path.GetTempPath(), "autocad-mcp-server-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var zip = Path.Combine(temp, "server.zip"); var hash = await DownloadVerifiedReleaseAsset("autocad-mcp-server.zip", zip);
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
        var pipeName = $@"\\.\pipe\AutoCAD_MCP_{cadYear}_V2".Replace("\\", "\\\\", StringComparison.Ordinal);
        var block = $"[mcp_servers.autocad_native]{Environment.NewLine}command = \"{escapedNode}\"{Environment.NewLine}args = [\"{escapedEntry}\"]{Environment.NewLine}env = {{ AUTOCAD_MCP_PIPE = \"{pipeName}\" }}{Environment.NewLine}startup_timeout_sec = 30{Environment.NewLine}tool_timeout_sec = 180{Environment.NewLine}";
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
            var cad = Selected();
            if (cad is null) throw new InvalidOperationException("先选择 AutoCAD 版本。");
            using var pipe = new NamedPipeClientStream(".", $"AutoCAD_MCP_{cad.Version}_V2", PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2500);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { id = "installer-status", tool = "get_bridge_status", args = new { } }));
            var response = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var json = JsonDocument.Parse(response ?? throw new IOException("管道关闭，没有收到响应"));
            if (!json.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) throw new IOException(response);
            status.Text = "连接成功：AutoCAD 原生插件已响应 get_bridge_status。";
        }
        catch (Exception e) { status.Text = "尚未连通：" + e.Message + "。请打开所选 AutoCAD 版本，并重启 Codex 后重试。"; }
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
