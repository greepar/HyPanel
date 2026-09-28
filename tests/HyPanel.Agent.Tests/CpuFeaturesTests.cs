using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class CpuFeaturesTests
{
    [TestMethod]
    public void HasX86_64V3Flags_RequiresTheFullGoAmd64V3Set()
    {
        const string haswell = "flags\t\t: fpu sse sse2 ssse3 fma cx16 sse4_1 sse4_2 movbe popcnt avx f16c abm bmi1 avx2 bmi2";
        const string sandyBridge = "flags\t\t: fpu sse sse2 ssse3 cx16 sse4_1 sse4_2 popcnt avx";

        Assert.IsTrue(CpuFeatures.HasX86_64V3Flags(["processor\t: 0", haswell]));
        Assert.IsFalse(CpuFeatures.HasX86_64V3Flags([sandyBridge]));
        Assert.IsFalse(CpuFeatures.HasX86_64V3Flags(["processor\t: 0"]));
    }
}
