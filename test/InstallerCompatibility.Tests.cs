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
    var pluginTrustPath = Path.Combine(temp, "AutoCadMcp.bundle", "Contents", "Windows");
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
Console.WriteLine("PASS: 8 AutoCAD series mappings, 8 target frameworks, runtimeconfig parsing, install gates, release SHA-256 validation, npm path quoting, and scoped trusted-path registration.");
