using AutoCadMcpInstaller;

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
}

var mapping = new Dictionary<string, string>
{
    ["2020"] = "R23.1", ["2021"] = "R24.0", ["2022"] = "R24.1", ["2023"] = "R24.2",
    ["2024"] = "R24.3", ["2025"] = "R25.0", ["2026"] = "R25.1", ["2027"] = "R26.0"
};
foreach (var (year, series) in mapping)
{
    Equal(series, CadCompatibility.Series(year), $"series {year}");
    Equal(year, CadCompatibility.YearFromSeries(series), $"reverse series {series}");
}
Equal("2020", CadCompatibility.ParseYear("AutoCAD 2020 (R23.1)"), "year parsing 2020");
Equal("2027", CadCompatibility.ParseYear("AutoCAD 2027 (R26.0)"), "year parsing 2027");
Equal<string?>(null, CadCompatibility.ParseYear("AutoCAD 2028"), "unsupported future year");
Equal("net47", CadCompatibility.PluginTargetFramework("2020", 8), "2020 target");
foreach (var year in new[] { "2021", "2022", "2023", "2024" })
    Equal("net48", CadCompatibility.PluginTargetFramework(year, 8), $"{year} target");
foreach (var year in new[] { "2025", "2026" })
{
    Equal("net8.0-windows", CadCompatibility.PluginTargetFramework(year, 8), $"{year} net8 target");
    Equal("net10.0-windows", CadCompatibility.PluginTargetFramework(year, 10), $"{year} net10 target");
}
Equal("net10.0-windows", CadCompatibility.PluginTargetFramework("2027", 10), "2027 target");
try { CadCompatibility.PluginTargetFramework("2027", 8); throw new InvalidOperationException("AutoCAD 2027 accepted a .NET 8 target"); }
catch (ArgumentOutOfRangeException) { }
var expectedAbcHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
Equal(expectedAbcHash, ReleaseAssetIntegrity.VerifySha256(System.Text.Encoding.UTF8.GetBytes("abc"), expectedAbcHash + "  fixture.zip"), "release hash verification");
try { ReleaseAssetIntegrity.VerifySha256(System.Text.Encoding.UTF8.GetBytes("abc"), new string('0', 64)); throw new InvalidOperationException("release hash mismatch was accepted"); }
catch (InvalidDataException) { }
try { ReleaseAssetIntegrity.VerifySha256(System.Text.Encoding.UTF8.GetBytes("abc"), "not-a-hash"); throw new InvalidOperationException("malformed release hash was accepted"); }
catch (InvalidDataException) { }

var temp = Path.Combine(Path.GetTempPath(), "cadcompat-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var user = Path.Combine(temp, "user"); Directory.CreateDirectory(user);
    Directory.CreateDirectory(Path.Combine(user, ".codex"));
    File.WriteAllText(Path.Combine(user, ".codex", "config.toml"), "# fixture");
    var harnessHome = Path.Combine(user, ".dsh"); Directory.CreateDirectory(harnessHome);
    var clients = McpClientIntegration.Detect(user, Path.Combine(user, "AppData"), "", harnessHome);
    Equal(true, clients.Single(x => x.Kind == McpClientKind.Codex).IsDetected, "Codex detection");
    Equal(true, clients.Single(x => x.Kind == McpClientKind.DeepSeekHarness).IsDetected, "DeepSeek Harness detection");
    var nodeFixture = Path.Combine(temp, "Node Runtime", "node.exe");
    var entryFixture = Path.Combine(temp, "MCP Server", "index.js");
    var harnessPatch = McpClientIntegration.BuildHarnessPatch(nodeFixture, entryFixture, "2025");
    if (!harnessPatch.Contains("transport: stdio", StringComparison.Ordinal)
        || !harnessPatch.Contains("AUTOCAD_MCP_PIPE: '\\\\.\\pipe\\AutoCAD_MCP_2025_V2'", StringComparison.Ordinal)
        || !harnessPatch.Contains(McpClientIntegration.YamlQuote(nodeFixture), StringComparison.Ordinal))
        throw new InvalidOperationException("DeepSeek Harness stdio patch is missing its command, node paths, or AutoCAD named pipe.");
    var once = McpClientIntegration.UpsertHarnessPatch("# existing harness configuration", harnessPatch, "2025");
    var twice = McpClientIntegration.UpsertHarnessPatch(once, McpClientIntegration.BuildHarnessPatch(nodeFixture, entryFixture, "2025"), "2025");
    Equal(once, twice, "DeepSeek Harness managed patch idempotency");
    if (!twice.StartsWith("# existing harness configuration", StringComparison.Ordinal)) throw new InvalidOperationException("DeepSeek Harness configuration did not preserve existing content.");
    try { McpClientIntegration.UpsertHarnessPatch("- insert:\n    - id: mcp-autocad-native-2025\n", harnessPatch, "2025"); throw new InvalidOperationException("unmanaged Harness entry was overwritten"); }
    catch (InvalidOperationException e) when (e.Message.Contains("未由安装器管理", StringComparison.Ordinal)) { }
    var harnessClient = clients.Single(x => x.Kind == McpClientKind.DeepSeekHarness) with { ConfigPath = Path.Combine(harnessHome, "cordis.patch.yml") };
    File.WriteAllText(harnessClient.ConfigPath, "# preserve this user setting\n");
    var writtenHarnessConfig = McpClientIntegration.ConfigureHarness(harnessClient, nodeFixture, entryFixture, "2025");
    Equal(harnessClient.ConfigPath, writtenHarnessConfig, "Harness config path");
    var persistedHarness = File.ReadAllText(harnessClient.ConfigPath);
    if (!persistedHarness.Contains("# preserve this user setting", StringComparison.Ordinal)
        || !persistedHarness.Contains("mcp-autocad-native-2025", StringComparison.Ordinal))
        throw new InvalidOperationException("Harness integration did not preserve user config and write its MCP entry.");
    if (!Directory.GetFiles(harnessHome, "cordis.patch.yml.backup-*").Any()) throw new InvalidOperationException("Harness configuration backup was not created.");
    var codexClient = clients.Single(x => x.Kind == McpClientKind.Codex) with { ConfigPath = Path.Combine(user, ".codex", "config.toml") };
    var writtenCodexConfig = McpClientIntegration.ConfigureCodex(codexClient, nodeFixture, entryFixture, "2025");
    Equal(codexClient.ConfigPath, writtenCodexConfig, "Codex config path");
    if (!File.ReadAllText(codexClient.ConfigPath).Contains("[mcp_servers.autocad_native]", StringComparison.Ordinal))
        throw new InvalidOperationException("Codex MCP config block was not written.");

    var spacedRuntime = Path.Combine(temp, "Program Files", "Node.js");
    Directory.CreateDirectory(Path.Combine(spacedRuntime, "node_modules", "npm", "bin"));
    var npmCmd = Path.Combine(spacedRuntime, "npm.cmd");
    var npmCli = Path.Combine(spacedRuntime, "node_modules", "npm", "bin", "npm-cli.js");
    File.WriteAllText(npmCmd, "fixture"); File.WriteAllText(npmCli, "fixture");
    var npmStart = NpmRunner.CreateStartInfo(Path.Combine(spacedRuntime, "node.exe"), npmCmd, Path.Combine(temp, "server folder"));
    Equal(Path.Combine(spacedRuntime, "node.exe"), npmStart.FileName, "npm runner executable");
    Equal(Path.Combine(temp, "server folder"), npmStart.WorkingDirectory, "npm runner working directory with spaces");
    Equal(npmCli, npmStart.ArgumentList[0], "npm cli path with spaces");
    Equal("ci", npmStart.ArgumentList[1], "npm ci invocation");
    Equal("--no-audit", npmStart.ArgumentList[2], "npm audit flag");
    Equal("--no-fund", npmStart.ArgumentList[3], "npm fund flag");
    var pluginTrustPath = Path.Combine(temp, "AutoCadMcp.bundle", "...");
    var originalTrustedPaths = "\"%APPDATA%\\Autodesk\\ApplicationPlugins\";C:\\ExistingTrustedFolder";
    var updatedTrustedPaths = CadTrustPathRegistration.Append(originalTrustedPaths, pluginTrustPath);
    if (!updatedTrustedPaths.Contains("C:\\ExistingTrustedFolder", StringComparison.Ordinal)
        || !updatedTrustedPaths.Contains($"\"{pluginTrustPath}\"", StringComparison.Ordinal))
        throw new InvalidOperationException("AutoCAD trusted paths did not preserve existing locations and add the plug-in directory.");
    Equal(updatedTrustedPaths, CadTrustPathRegistration.Append(updatedTrustedPaths, pluginTrustPath), "trusted path deduplication");
    Equal($"\"{pluginTrustPath}\"", CadTrustPathRegistration.Append(".", pluginTrustPath), "trusted path empty-sentinel handling");
    foreach (var file in new[] { "acad.exe", "acmgd.dll", "acdbmgd.dll", "accoremgd.dll" }) File.WriteAllText(Path.Combine(temp, file), "fixture");
    File.WriteAllText(Path.Combine(temp, "acdbmgd.runtimeconfig.json"), """{"runtimeOptions":{"frameworks":[{"name":"Microsoft.NETCore.App","version":"8.0.0"},{"name":"Microsoft.WindowsDesktop.App","version":"8.0.0"}]}}""");
    Equal(8, CadCompatibility.ReadRuntimeMajor(temp), "multi-framework runtime detection");
    Equal(true, CadCompatibility.IsSupported("2026", temp), "2026 .NET8 host detection");
    File.WriteAllText(Path.Combine(temp, "acdbmgd.runtimeconfig.json"), """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.1"}}}""");
    Equal(10, CadCompatibility.ReadRuntimeMajor(temp), "single-framework runtime detection");
    Equal(true, CadCompatibility.IsSupported("2025", temp), "2025 .NET10 host detection");
    Equal(true, CadCompatibility.IsSupported("2027", temp), "2027 .NET10 host detection");
    File.WriteAllText(Path.Combine(temp, "acdbmgd.runtimeconfig.json"), """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"9.0.0"}}}""");
    Equal(false, CadCompatibility.IsSupported("2025", temp), "unsupported runtime gate");
    File.WriteAllText(Path.Combine(temp, "acdbmgd.runtimeconfig.json"), """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}""");
    Equal(false, CadCompatibility.IsSupported("2027", temp), "2027 .NET8 rejection");
    File.Delete(Path.Combine(temp, "acdbmgd.runtimeconfig.json"));
    Equal(true, CadCompatibility.IsSupported("2020", temp), "legacy framework host detection");
    Equal(false, CadCompatibility.IsSupported("2027", temp), "unknown CAD year gate");
    File.Delete(Path.Combine(temp, "acmgd.dll"));
    Equal(false, CadCompatibility.IsSupported("2020", temp), "missing API gate");
}
finally { Directory.Delete(temp, true); }
var repo = new DirectoryInfo(Directory.GetCurrentDirectory());
while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "src", "index.js"))) repo = repo.Parent;
if (repo is null) throw new DirectoryNotFoundException("Could not locate the repository's MCP server entry point.");
var node = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
    .Select(folder => Path.Combine(folder.Trim('"'), "node.exe")).FirstOrDefault(File.Exists)
    ?? throw new FileNotFoundException("Node.js is required for MCP stdio integration tests.");
var toolList = await McpServerProbe.ListToolsAsync(node, Path.Combine(repo.FullName, "src", "index.js"), @"\\.\pipe\AutoCAD_MCP_TEST_V2", TimeSpan.FromSeconds(10));
if (toolList.Count < 10 || !toolList.Any(tool => tool.Name == "get_bridge_status") || !toolList.Any(tool => tool.Name == "draw_line"))
    throw new InvalidOperationException($"MCP stdio integration test did not discover expected AutoCAD tools: {string.Join(", ", toolList.Select(tool => tool.Name))}");
Console.WriteLine($"PASS: 8 AutoCAD series mappings, 8 target frameworks, runtimeconfig parsing, install gates, release SHA-256 validation, npm path quoting, scoped trust paths, Codex/DeepSeek Harness detection and configuration, and stdio MCP handshake ({toolList.Count} tools discovered).");
