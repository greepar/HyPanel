namespace HyPanel.Agent;

using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

/// <summary>
/// Whether this CPU can run x86-64-v3 binaries, such as Hysteria's <c>-avx</c> builds (compiled with GOAMD64=v3).
/// On Linux the answer comes from /proc/cpuinfo: a NativeAOT binary compiled for the baseline instruction set does not
/// reliably report the newer ISAs through the <c>IsSupported</c> intrinsics.
/// </summary>
internal static class CpuFeatures
{
    // GOAMD64=v3 requires AVX, AVX2, BMI1, BMI2, F16C, FMA, LZCNT (reported as "abm") and MOVBE.
    private static readonly string[] X86_64V3Flags = ["avx", "avx2", "bmi1", "bmi2", "f16c", "fma", "abm", "movbe"];

    private static readonly Lazy<bool> x86_64V3 = new(Detect);

    public static bool SupportsX86_64V3 => x86_64V3.Value;

    private static bool Detect()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return false;
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return HasX86_64V3Flags(File.ReadLines("/proc/cpuinfo"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
        return Avx2.IsSupported && Bmi1.IsSupported && Bmi2.IsSupported && Fma.IsSupported && Lzcnt.IsSupported;
    }

    internal static bool HasX86_64V3Flags(IEnumerable<string> cpuinfo)
    {
        var line = cpuinfo.FirstOrDefault(line => line.StartsWith("flags", StringComparison.Ordinal));
        if (line is null || line.IndexOf(':') is var colon && colon < 0) return false;
        var flags = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return X86_64V3Flags.All(flags.Contains);
    }
}
