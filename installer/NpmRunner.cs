using System.Diagnostics;

namespace AutoCadMcpInstaller;

internal static class NpmRunner
{
    internal static ProcessStartInfo CreateStartInfo(string node, string npm, string workingDirectory)
    {
        var npmCli = Path.Combine(Path.GetDirectoryName(npm)!, "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(npmCli)) throw new FileNotFoundException("Node.js npm CLI was not found beside npm.cmd.", npmCli);
        var info = new ProcessStartInfo(node)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add(npmCli);
        info.ArgumentList.Add("ci");
        info.ArgumentList.Add("--no-audit");
        info.ArgumentList.Add("--no-fund");
        return info;
    }

    internal static async Task<(int ExitCode, string Output)> RunAsync(string node, string npm, string workingDirectory)
    {
        using var process = new Process { StartInfo = CreateStartInfo(node, npm, workingDirectory) };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await stdout) + (await stderr));
    }
}
