using System.Text.Json;

namespace AutoCadMcpInstaller;

internal static class CadCompatibility
{
    private static readonly IReadOnlyDictionary<string, string> SeriesByYear = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["2020"] = "R23.1", ["2021"] = "R24.0", ["2022"] = "R24.1", ["2023"] = "R24.2",
        ["2024"] = "R24.3", ["2025"] = "R25.0", ["2026"] = "R25.1", ["2027"] = "R26.0"
    };

    public static string Series(string year) => SeriesByYear.TryGetValue(year, out var series) ? series : "Unknown";

    public static string? ParseYear(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"(?<!\d)20(20|21|22|23|24|25|26|27)(?!\d)");
        return match.Success ? match.Value : null;
    }

    public static string? YearFromSeries(string series) => SeriesByYear.FirstOrDefault(pair =>
        string.Equals(pair.Value, series, StringComparison.OrdinalIgnoreCase)).Key;

    public static int? ReadRuntimeMajor(string installDirectory)
    {
        var path = Path.Combine(installDirectory, "acdbmgd.runtimeconfig.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var options = doc.RootElement.GetProperty("runtimeOptions");
            if (options.TryGetProperty("framework", out var single) && single.GetProperty("name").GetString() == "Microsoft.NETCore.App")
                return ReadMajor(single);
            if (options.TryGetProperty("frameworks", out var many))
                foreach (var item in many.EnumerateArray())
                    if (item.GetProperty("name").GetString() == "Microsoft.NETCore.App") return ReadMajor(item);
        }
        catch { }
        return null;
    }

    private static int? ReadMajor(JsonElement framework) => Version.TryParse(
        framework.GetProperty("version").GetString()?.TrimStart('v'), out var version) ? version.Major : null;

    public static bool IsSupported(string year, string? installDirectory)
    {
        if (!int.TryParse(year, out var numericYear) || numericYear is < 2020 or > 2027 || string.IsNullOrWhiteSpace(installDirectory)) return false;
        if (!new[] { "acad.exe", "acmgd.dll", "acdbmgd.dll", "accoremgd.dll" }.All(name => File.Exists(Path.Combine(installDirectory, name)))) return false;
        if (numericYear <= 2024) return true;
        var runtime = ReadRuntimeMajor(installDirectory);
        return numericYear == 2027 ? runtime == 10 : runtime is 8 or 10;
    }

    public static string PluginTargetFramework(string year, int hostRuntimeMajor)
    {
        if (year == "2020") return "net47";
        if (int.TryParse(year, out var numericYear) && numericYear is >= 2021 and <= 2024) return "net48";
        if ((year is "2025" or "2026") && (hostRuntimeMajor is 8 or 10)) return $"net{hostRuntimeMajor}.0-windows";
        if (year == "2027" && hostRuntimeMajor == 10) return "net10.0-windows";
        throw new ArgumentOutOfRangeException(nameof(year), $"Unsupported AutoCAD {year} / .NET {hostRuntimeMajor} target.");
    }
}
