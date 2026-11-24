using System.Diagnostics;
using System.Text;

namespace HyPanel.Agent.Backends.Infrastructure;

/// <summary>Runs a backend binary in its config-test mode and returns its complaint, if any.</summary>
internal static class ConfigTest
{
    private const int MaximumOutput = 16 * 1024;

    public static async ValueTask<string?> RunAsync(string binary, IEnumerable<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(binary)
        {
            WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment) start.Environment[key] = value;
        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null) return;
            lock (output) if (output.Length < MaximumOutput) output.AppendLine(line);
        }
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        if (!process.Start()) return "无法运行内核检查配置。";
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return "内核检查配置超时。";
        }
        if (process.ExitCode == 0) return null;
        string text;
        lock (output) text = output.ToString();
        // The last meaningful lines carry the reason; keep the message short enough to show on the service.
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)
                           || line.Contains("fail", StringComparison.OrdinalIgnoreCase)
                           || line.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            .TakeLast(3).ToArray();
        var reason = lines.Length > 0 ? string.Join(" ", lines) : text.Trim();
        if (reason.Length > 600) reason = reason[..600] + "…";
        return $"内核拒绝了新配置：{(reason.Length == 0 ? $"退出码 {process.ExitCode}" : reason)}";
    }
}
