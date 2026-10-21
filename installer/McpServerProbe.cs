using System.Diagnostics;
using System.Text.Json;

namespace AutoCadMcpInstaller;

internal sealed record McpToolProbe(string Name, string? Description);

internal static class McpServerProbe
{
    internal static async Task<IReadOnlyList<McpToolProbe>> ListToolsAsync(string node, string entry, string pipeName, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(node)
        {
            WorkingDirectory = Path.GetDirectoryName(entry)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(entry);
        start.Environment["AUTOCAD_MCP_PIPE"] = pipeName;
        using var process = new Process { StartInfo = start };
        process.Start();
        process.BeginErrorReadLine();
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            await SendAsync(process, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "AutoCAD MCP Installer Diagnostics", version = "0.6.0" } } }, cancel.Token);
            using (var initialized = JsonDocument.Parse(await ReadLineAsync(process, cancel.Token)))
                if (initialized.RootElement.TryGetProperty("error", out var error)) throw new InvalidDataException("MCP initialize failed: " + error);
            await SendAsync(process, new { jsonrpc = "2.0", method = "notifications/initialized" }, cancel.Token);
            await SendAsync(process, new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } }, cancel.Token);
            using var response = JsonDocument.Parse(await ReadLineAsync(process, cancel.Token));
            var root = response.RootElement;
            if (root.TryGetProperty("error", out var listError)) throw new InvalidDataException("MCP tools/list failed: " + listError);
            if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("MCP tools/list returned no tools array.");
            return tools.EnumerateArray().Select(tool => new McpToolProbe(
                tool.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                tool.TryGetProperty("description", out var description) ? description.GetString() : null))
                .Where(tool => tool.Name.Length > 0).ToArray();
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { }
            try { if (!process.HasExited && !process.WaitForExit(1200)) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static Task SendAsync(Process process, object message, CancellationToken token)
        => process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);

    private static async Task<string> ReadLineAsync(Process process, CancellationToken token)
        => await process.StandardOutput.ReadLineAsync(token) ?? throw new EndOfStreamException("MCP server closed stdout before replying.");
}
