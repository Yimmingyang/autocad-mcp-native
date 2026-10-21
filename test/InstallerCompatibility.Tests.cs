using AutoCadMcpInstaller;

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
}

var mapping = new Dictionary<string, string>
{
    ["2020"] = "R23.1", ["2021"] = "R24.0", ["2022"] = "R24.1", ["2023"] = "R24.2",
    ["2024"] = "R24.3", ["2025"] = "R25.0", ["2026"] = "R25.1"
};
foreach (var (year, series) in mapping)
{
    Equal(series, CadCompatibility.Series(year), $"series {year}");
    Equal(year, CadCompatibility.YearFromSeries(series), $"reverse series {series}");
}
Equal("net47", CadCompatibility.PluginTargetFramework("2020", 8), "2020 target");
foreach (var year in new[] { "2021", "2022", "2023", "2024" })
    Equal("net48", CadCompatibility.PluginTargetFramework(year, 8), $"{year} target");
foreach (var year in new[] { "2025", "2026" })
{
    Equal("net8.0-windows", CadCompatibility.PluginTargetFramework(year, 8), $"{year} net8 target");
    Equal("net10.0-windows", CadCompatibility.PluginTargetFramework(year, 10), $"{year} net10 target");
}

var temp = Path.Combine(Path.GetTempPath(), "cadcompat-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    foreach (var file in new[] { "acad.exe", "acmgd.dll", "acdbmgd.dll", "accoremgd.dll" }) File.WriteAllText(Path.Combine(temp, file), "fixture");
    File.WriteAllText(Path.Combine(temp, "acdbmgd.runtimeconfig.json"), """{"runtimeOptions":{"frameworks":[{"name":"Microsoft.NETCore.App","version":"8.0.0"},{"name":"Microsoft.WindowsDesktop.App","version":"8.0.0"}]}}""");
    Equal(8, CadCompatibility.ReadRuntimeMajor(temp), "multi-framework runtime detection");
    Equal(true, CadCompatibility.IsSupported("2026", temp), "2026 .NET8 host detection");
    File.WriteAllText(Path.Combine(temp, "acdbmgd.runtimeconfig.json"), """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.1"}}}""");
    Equal(10, CadCompatibility.ReadRuntimeMajor(temp), "single-framework runtime detection");
    Equal(true, CadCompatibility.IsSupported("2025", temp), "2025 .NET10 host detection");
    File.WriteAllText(Path.Combine(temp, "acdbmgd.runtimeconfig.json"), """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"9.0.0"}}}""");
    Equal(false, CadCompatibility.IsSupported("2025", temp), "unsupported runtime gate");
    File.Delete(Path.Combine(temp, "acdbmgd.runtimeconfig.json"));
    Equal(true, CadCompatibility.IsSupported("2020", temp), "legacy framework host detection");
    Equal(false, CadCompatibility.IsSupported("2027", temp), "unknown CAD year gate");
    File.Delete(Path.Combine(temp, "acmgd.dll"));
    Equal(false, CadCompatibility.IsSupported("2020", temp), "missing API gate");
}
finally { Directory.Delete(temp, true); }
Console.WriteLine("PASS: 7 AutoCAD series mappings, 7 target frameworks, runtimeconfig parsing, and install gates.");
