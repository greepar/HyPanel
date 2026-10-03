namespace HyPanel.Agent.Networking;

using System.Net.NetworkInformation;
using System.Text;

/// <summary>Reads and writes kernel network settings through /proc and /sys instead of the sysctl binary.</summary>
public static class KernelSettings
{
    private const string Root = "/proc/sys/";

    public static string Read(string key) => File.ReadAllText(Path(key)).Trim();

    public static bool TryRead(string key, out string value)
    {
        try { value = Read(key); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { value = ""; return false; }
    }

    public static void Write(string key, string value)
    {
        try { File.WriteAllText(Path(key), value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"无法设置内核参数 {key}={value}：{ex.Message}", ex);
        }
    }

    /// <summary>Sets a parameter only when it is not already at the wanted value.</summary>
    public static void Ensure(string key, string value)
    {
        if (!TryRead(key, out var current) || current != value) Write(key, value);
    }

    /// <summary>Raises a reverse-path filter from strict to loose, leaving disabled and loose values alone.</summary>
    public static void RelaxStrictReversePath(string key)
    {
        if (TryRead(key, out var current) && current == "1") Write(key, "2");
    }

    public static string Describe(params string[] keys) =>
        string.Join("\n", keys.Select(key => $"{key} = {(TryRead(key, out var value) ? value : "(不可读)")}"));

    public static string DescribeInterface(string name)
    {
        var text = new StringBuilder();
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == name);
        if (nic is null) return $"接口 {name} 不存在";
        text.Append($"{name}: 状态 {nic.OperationalStatus}");
        if (File.Exists($"/sys/class/net/{name}/mtu")) text.Append($" mtu {File.ReadAllText($"/sys/class/net/{name}/mtu").Trim()}");
        foreach (var address in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            text.Append($"\n  inet {address.Address}/{address.PrefixLength}");
        return text.ToString();
    }

    // Keys are /proc/sys relative paths such as net/ipv4/ip_forward; only Agent-built keys are passed in.
    private static string Path(string key) =>
        key.Length == 0 || key.StartsWith('/') || key.Contains("..") ? throw new ArgumentException(key) : Root + key;
}
