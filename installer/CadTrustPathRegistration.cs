using Microsoft.Win32;

namespace AutoCadMcpInstaller;

internal static class CadTrustPathRegistration
{
    internal static string Append(string existing, string path)
    {
        var target = Path.GetFullPath(path);
        var entries = (existing ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim('"'))
            .Where(x => x != ".")
            .ToList();
        foreach (var entry in entries)
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(entry);
                if (Path.GetFullPath(expanded).Equals(target, StringComparison.OrdinalIgnoreCase)) return existing ?? string.Empty;
            }
            catch { }
        }
        entries.Add($"\"{target}\"");
        return string.Join(';', entries);
    }

    internal static int RegisterForCurrentUser(string series, string bundle)
    {
        var trustedDirectory = Path.Combine(bundle, "Contents", "Windows");
        using var root = Registry.CurrentUser.OpenSubKey($@"Software\Autodesk\AutoCAD\{series}");
        if (root is null) return 0;
        var updated = 0;
        foreach (var productName in root.GetSubKeyNames().Where(x => x.StartsWith("ACAD-", StringComparison.OrdinalIgnoreCase)))
        {
            using var product = root.OpenSubKey(productName);
            using var profiles = product?.OpenSubKey("Profiles");
            if (profiles is null) continue;
            foreach (var profileName in profiles.GetSubKeyNames())
            {
                using var profile = profiles.OpenSubKey(profileName);
                using var variables = profile?.OpenSubKey("Variables", writable: true);
                if (variables is null) continue;
                var existing = variables.GetValue("TRUSTEDPATHS", "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
                var next = Append(existing, trustedDirectory);
                if (next.Equals(existing, StringComparison.Ordinal)) continue;
                variables.SetValue("TRUSTEDPATHS", next, RegistryValueKind.String);
                updated++;
            }
        }
        return updated;
    }
}
