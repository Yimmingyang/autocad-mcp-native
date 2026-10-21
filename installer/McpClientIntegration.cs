using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace AutoCadMcpInstaller;

internal enum McpClientKind { Codex, DeepSeekHarness }

internal sealed record McpClientInstallation(McpClientKind Kind, string Name, bool IsDetected, string ConfigPath, string DetectionDetail)
{
    public override string ToString() => $"{Name} — {DetectionDetail}";
}

internal static class McpClientIntegration
{
    internal static IReadOnlyList<McpClientInstallation> Detect(string userProfile, string? localAppData, string? path, string? dshHome)
    {
        var codexConfig = Path.Combine(userProfile, ".codex", "config.toml");
        var codexExe = Path.Combine(localAppData ?? "", "Programs", "Codex", "Codex.exe");
        var codexCommand = HasCommand(path, "codex.exe", "codex.cmd");
        var codexDetected = File.Exists(codexConfig) || File.Exists(codexExe) || codexCommand || HasInstalledApp("OpenAI Codex", "Codex Desktop");

        var harnessHome = !string.IsNullOrWhiteSpace(dshHome) ? dshHome : Path.Combine(userProfile, ".dsh");
        var harnessPatch = Path.Combine(harnessHome, "cordis.patch.yml");
        var harnessCommand = HasCommand(path, "dsh.exe", "dsh.cmd", "dsh.ps1");
        var harnessDetected = Directory.Exists(harnessHome) || File.Exists(harnessPatch) || harnessCommand || HasInstalledApp("DeepSeek Harness");

        return
        [
            new(McpClientKind.Codex, "OpenAI Codex", codexDetected, codexConfig,
                File.Exists(codexConfig) ? "MCP 配置存在" : codexDetected ? "程序/命令已检测到" : "未检测到"),
            new(McpClientKind.DeepSeekHarness, "DeepSeek Harness", harnessDetected, harnessPatch,
                File.Exists(harnessPatch) ? "MCP 配置存在" : harnessCommand ? "dsh 命令已检测到" : harnessDetected ? "配置目录/程序已检测到" : "未检测到")
        ];
    }

    private static bool HasCommand(string? path, params string[] names)
    {
        foreach (var folder in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
                try { if (File.Exists(Path.Combine(folder.Trim('"'), name))) return true; } catch { }
        return false;
    }

    private static bool HasInstalledApp(params string[] displayNames)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root is null) continue;
                foreach (var keyName in root.GetSubKeyNames())
                {
                    using var app = root.OpenSubKey(keyName);
                    var name = app?.GetValue("DisplayName")?.ToString() ?? "";
                    if (displayNames.Any(candidate => name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) return true;
                }
            }
            catch { }
        }
        return false;
    }

    internal static string BuildHarnessPatch(string node, string entry, string cadYear)
    {
        var pipe = $@"\\.\pipe\AutoCAD_MCP_{cadYear}_V2";
        return string.Join(Environment.NewLine,
        [
            $"# BEGIN AUTOCAD_MCP_NATIVE {cadYear}",
            "- insert:",
            $"    - id: mcp-autocad-native-{cadYear}",
            "      name: '@deepseek-ai/dsh-mcp-client'",
            "      config:",
            "        serverName: autocad-native",
            "        transport: stdio",
            $"        command: {YamlQuote(node)}",
            "        args:",
            $"          - {YamlQuote(entry)}",
            "        env:",
            $"          AUTOCAD_MCP_PIPE: {YamlQuote(pipe)}",
            $"# END AUTOCAD_MCP_NATIVE {cadYear}"
        ]) + Environment.NewLine;
    }

    internal static string UpsertHarnessPatch(string existing, string patch, string cadYear)
    {
        var start = $"# BEGIN AUTOCAD_MCP_NATIVE {cadYear}";
        var end = $"# END AUTOCAD_MCP_NATIVE {cadYear}";
        var block = new Regex($"(?ms)^{Regex.Escape(start)}\\r?\\n.*?^{Regex.Escape(end)}\\r?\\n?");
        if (block.IsMatch(existing)) return block.Replace(existing, patch);
        if (existing.Contains($"id: mcp-autocad-native-{cadYear}", StringComparison.Ordinal))
            throw new InvalidOperationException($"DeepSeek Harness 配置中已经存在 AutoCAD MCP {cadYear} 条目（未由安装器管理）；为避免覆盖用户配置，请先在 Harness 的 cordis.patch.yml 中移除该条目。");
        return existing.TrimEnd() + (existing.Length == 0 ? "" : Environment.NewLine + Environment.NewLine) + patch;
    }

    internal static string YamlQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    internal static void WriteWithBackup(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Copy(path, path + ".backup-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"), overwrite: false);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
    }

    internal static string ConfigureHarness(McpClientInstallation client, string node, string entry, string cadYear)
    {
        if (client.Kind != McpClientKind.DeepSeekHarness) throw new ArgumentException("所选 MCP 客户端不是 DeepSeek Harness。", nameof(client));
        var patch = BuildHarnessPatch(node, entry, cadYear);
        var existing = File.Exists(client.ConfigPath) ? File.ReadAllText(client.ConfigPath) : "";
        var next = UpsertHarnessPatch(existing, patch, cadYear);
        WriteWithBackup(client.ConfigPath, next);
        return client.ConfigPath;
    }

    internal static string ConfigureCodex(McpClientInstallation client, string node, string entry, string cadYear)
    {
        if (client.Kind != McpClientKind.Codex) throw new ArgumentException("所选 MCP 客户端不是 Codex。", nameof(client));
        var existing = File.Exists(client.ConfigPath) ? File.ReadAllText(client.ConfigPath) : "";
        var escapedNode = node.Replace("\\", "\\\\", StringComparison.Ordinal);
        var escapedEntry = entry.Replace("\\", "\\\\", StringComparison.Ordinal);
        var pipe = $@"\\.\pipe\AutoCAD_MCP_{cadYear}_V2".Replace("\\", "\\\\", StringComparison.Ordinal);
        var block = $"[mcp_servers.autocad_native]{Environment.NewLine}command = \"{escapedNode}\"{Environment.NewLine}args = [\"{escapedEntry}\"]{Environment.NewLine}env = {{ AUTOCAD_MCP_PIPE = \"{pipe}\" }}{Environment.NewLine}startup_timeout_sec = 30{Environment.NewLine}tool_timeout_sec = 180{Environment.NewLine}";
        var section = new Regex(@"(?ms)^\[mcp_servers\.autocad_native\]\r?\n.*?(?=^\[|\z)");
        var next = section.IsMatch(existing) ? section.Replace(existing, block) : existing.TrimEnd() + Environment.NewLine + Environment.NewLine + block;
        WriteWithBackup(client.ConfigPath, next);
        return client.ConfigPath;
    }
}
