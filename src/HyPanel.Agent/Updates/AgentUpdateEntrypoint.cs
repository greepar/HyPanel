namespace HyPanel.Agent.Updates;

using System.Diagnostics;
using System.Text.Json;

internal static class AgentUpdateEntrypoint
{
    public static bool TryRun(string[] args, out Task<int> result)
    {
        if (args is ["--self-test"])
        {
            Console.Out.WriteLine($"{BuildInfo.Version}\t{BuildInfo.RuntimeIdentifier}");
            result = Task.FromResult(string.IsNullOrWhiteSpace(BuildInfo.Version)
                                     || string.IsNullOrWhiteSpace(BuildInfo.RuntimeIdentifier) ? 1 : 0);
            return true;
        }
        if (args is ["--update-helper", var pidText, var targetPath, var stagedPath, var updateIdText]
            && int.TryParse(pidText, out var pid) && Guid.TryParse(updateIdText, out var updateId))
        {
            result = RunWindowsHelperAsync(pid, targetPath, stagedPath, updateId);
            return true;
        }
        result = Task.FromResult(0);
        return false;
    }

    private static async Task<int> RunWindowsHelperAsync(int parentPid, string targetPath, string stagedPath, Guid updateId)
    {
        if (!OperatingSystem.IsWindows() || updateId == Guid.Empty || Path.GetFileName(targetPath) != "HyPanel.Agent.exe") return 2;
        var helperPath = Path.GetFullPath(Environment.ProcessPath ?? string.Empty);
        var target = Path.GetFullPath(targetPath);
        var staged = Path.GetFullPath(stagedPath);
        if (!IsValidWindowsHelperPaths(helperPath, target, staged)) return 2;
        var dataDirectory = Environment.GetEnvironmentVariable("HYPANEL_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDirectory)) return 2;
        var statePath = Path.Combine(dataDirectory, AgentUpdateStateStore.FileName);
        if (!File.Exists(statePath)) return 2;
        var state = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(statePath),
            AgentSyncJsonSerializerContext.Default.AgentUpdateLocalState);
        if (state is null || state.UpdateId != updateId || state.Status != HyPanel.Shared.Contracts.AgentUpdateStatus.Applying
            || !Path.GetFullPath(state.InstallPath).Equals(target, StringComparison.OrdinalIgnoreCase)) return 2;
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            var parentPath = Path.GetFullPath(parent.MainModule?.FileName ?? string.Empty);
            if (!parentPath.Equals(target, StringComparison.OrdinalIgnoreCase)) return 2;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await parent.WaitForExitAsync(timeout.Token);
            var previous = AgentUpdater.PreviousPath(target);
            AgentUpdater.ReplacePreservingOld(staged, target, previous);
            using var start = Process.Start(new ProcessStartInfo("sc.exe", "start HyPanelAgent")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (start is null) throw new InvalidOperationException();
            await start.WaitForExitAsync();
            return start.ExitCode == 0 ? 0 : 3;
        }
        catch
        {
            var previous = AgentUpdater.PreviousPath(target);
            if (File.Exists(previous))
            {
                AgentUpdater.TryDelete(target + ".failed");
                if (File.Exists(target)) File.Move(target, target + ".failed", overwrite: true);
                File.Move(previous, target, overwrite: true);
            }
            try
            {
                using var restart = Process.Start(new ProcessStartInfo("sc.exe", "start HyPanelAgent")
                { UseShellExecute = false, CreateNoWindow = true });
                if (restart is not null) await restart.WaitForExitAsync();
            }
            catch { }
            return 3;
        }
    }

    internal static bool IsValidWindowsHelperPaths(string helperPath, string targetPath, string stagedPath)
    {
        var target = Path.GetFullPath(targetPath);
        return Path.GetFullPath(helperPath).Equals(target + ".update-helper.exe", StringComparison.OrdinalIgnoreCase)
               && Path.GetFullPath(stagedPath).Equals(AgentUpdater.StagedExecutablePath(target),
                   StringComparison.OrdinalIgnoreCase)
               && Path.GetFileName(target).Equals("HyPanel.Agent.exe", StringComparison.OrdinalIgnoreCase);
    }
}
