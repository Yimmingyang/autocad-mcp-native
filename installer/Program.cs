using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AutoCadMcpInstaller;

internal sealed record CadInstall(string Version, string ProductKey, string? Location)
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
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(4) };
    private List<CadInstall> installations = [];
    private const string Repo = "https://api.github.com/repos/yanmiong/autocad-mcp-native";

    public MainForm()
    {
        Text = "AutoCAD MCP 安装器"; Width = 680; Height = 390; StartPosition = FormStartPosition.CenterScreen;
        list.Columns.Add("AutoCAD 版本", 150); list.Columns.Add("系列", 110); list.Columns.Add("插件状态", 340);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(8) };
        buttons.Controls.AddRange([refresh, install, open]);
        Controls.Add(buttons); Controls.Add(status); Controls.Add(list);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AutoCadMcpInstaller/0.1");
        refresh.Click += (_, _) => Detect(); install.Click += async (_, _) => await InstallSelected(); open.Click += (_, _) => OpenSelected();
        Shown += (_, _) => Detect();
    }

    private void Detect()
    {
        installations = FindInstallations(); list.Items.Clear();
        foreach (var cad in installations)
        {
            var supported = cad.Version == "2025";
            var item = new ListViewItem($"AutoCAD {cad.Version}"); item.SubItems.Add(cad.Series);
            var bundle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "ApplicationPlugins", "AutoCadMcp.bundle", "PackageContents.xml");
            item.SubItems.Add(supported ? (File.Exists(bundle) ? "已安装（启动 CAD 后检测连接）" : "可安装 MCP 0.5.0") : "此版本插件构建尚未发布");
            item.Tag = cad; list.Items.Add(item);
        }
        if (installations.Count == 0) status.Text = "未发现 AutoCAD 2020–2026。安装 AutoCAD 后点“重新检测”。";
        else status.Text = $"检测到 {installations.Count} 个 AutoCAD 安装。当前仓库只有 AutoCAD 2025 插件包已验证；其他版本已识别，但尚无对应构建，安装器不会误装 2025 DLL。";
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
                    using var key = root.OpenSubKey(name); var product = key?.GetValue("ProductName")?.ToString() ?? "";
                    var match = System.Text.RegularExpressions.Regex.Match(product + " " + name, @"20(20|21|22|23|24|25|26)");
                    if (!match.Success) continue;
                    var v = match.Value; found.TryAdd(v, new CadInstall(v, name, key?.GetValue("Location")?.ToString()));
                }
            }
            catch { }
        }
        return found.Values.OrderByDescending(x => x.Version).ToList();
    }

    private CadInstall? Selected() => list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as CadInstall;

    private async Task InstallSelected()
    {
        var cad = Selected(); if (cad is null) { status.Text = "先选择一个 AutoCAD 版本。"; return; }
        if (cad.Version != "2025") { status.Text = $"AutoCAD {cad.Version}（{cad.Series}）已检测到；对应 .NET/API 插件构建还未发布。本安装器已阻止不兼容 DLL 安装。"; return; }
        if (Process.GetProcessesByName("acad").Length > 0) { status.Text = "请先保存并退出 AutoCAD，再重试安装。"; return; }
        install.Enabled = false;
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
            var targetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "ApplicationPlugins"); Directory.CreateDirectory(targetRoot);
            var target = Path.Combine(targetRoot, "AutoCadMcp.bundle"); var stage = target + ".stage-" + Guid.NewGuid().ToString("N"); var backup = target + ".backup-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            CopyDirectory(bundle, stage); if (Directory.Exists(target)) Directory.Move(target, backup);
            try { Directory.Move(stage, target); } catch { if (Directory.Exists(backup)) Directory.Move(backup, target); throw; }
            status.Text = $"插件已安装（SHA-256 {hash[..12]}…）。打开/重启 AutoCAD 2025 后，再在 MCP 客户端调用 get_bridge_status 验证连接。";
            Detect();
        }
        catch (Exception e) { status.Text = "安装未完成：" + e.Message; }
        finally { install.Enabled = true; }
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
        if (exe is null || !File.Exists(exe)) { status.Text = $"AutoCAD {cad.Version} 已检测到，但注册表未给出有效 acad.exe 路径。"; return; }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); status.Text = $"已启动 AutoCAD {cad.Version}。插件状态需在 AutoCAD 内通过 get_bridge_status 确认。";
    }
}

internal static class Program
{
    [STAThread] private static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new MainForm()); }
}
