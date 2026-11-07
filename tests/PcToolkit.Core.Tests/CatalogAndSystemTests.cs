using Microsoft.Win32;
using PcToolkit.Core.Changes;
using PcToolkit.Core.Tweaks;

namespace PcToolkit.Core.Tests;

public sealed class CatalogTests
{
    private static TweakCatalog LoadCatalog() => TweakCatalog.LoadEmbedded(new SystemTargetFactory());

    [Fact]
    public void EmbeddedCatalog_HasNoProblems()
    {
        TweakCatalog catalog = LoadCatalog();

        Assert.Empty(catalog.Problems);
        Assert.True(catalog.Tweaks.Count >= 15);
        Assert.Equal(catalog.Tweaks.Count, catalog.Tweaks.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Questionnaire_GamingDesktop_RecommendsOnlyLowRiskGamingTweaks()
    {
        TweakCatalog catalog = LoadCatalog();
        var answers = new Dictionary<string, string> { ["usage"] = "gaming", ["interface"] = "beauty", ["privacy"] = "no" };

        var ids = Questionnaire.Recommend(catalog, answers, isLaptop: false, windowsBuild: 19045).Select(t => t.Id).ToList();

        Assert.Contains("game-mode", ids);
        Assert.Contains("mouse-accel-off", ids);
        Assert.Contains("high-performance-plan", ids);
        Assert.DoesNotContain("hags", ids);
        Assert.DoesNotContain("sysmain-off", ids);
        Assert.DoesNotContain("menu-delay", ids);
    }

    [Fact]
    public void Questionnaire_Laptop_DoesNotRecommendHighPerformancePlan()
    {
        TweakCatalog catalog = LoadCatalog();
        var answers = new Dictionary<string, string> { ["usage"] = "gaming" };

        var ids = Questionnaire.Recommend(catalog, answers, isLaptop: true, windowsBuild: 19045).Select(t => t.Id).ToList();

        Assert.DoesNotContain("high-performance-plan", ids);
        Assert.Contains("game-mode", ids);
    }
}

/// <summary>Проверки настоящих параметров Windows. Пишут только в собственный тестовый раздел HKCU и удаляют его.</summary>
public sealed class SystemTargetTests
{
    [Fact]
    public void RegistryTarget_WritesReadsAndDeletes_InOwnTestKey()
    {
        string subKey = $@"Software\PcToolkit.Tests\{Guid.NewGuid():N}";
        try
        {
            var dword = new RegistryTarget($@"HKCU\{subKey}", "Number", RegistryValueKind.DWord);
            var text = new RegistryTarget($@"HKCU\{subKey}", "Text", RegistryValueKind.String);

            Assert.False(dword.Read().Exists);

            var max = new ValueSnapshot(true, "DWord", "4294967295");
            dword.Write(max);
            Assert.True(dword.Read().SameAs(max));

            var hello = new ValueSnapshot(true, "String", "привет");
            text.Write(hello);
            Assert.True(text.Read().SameAs(hello));

            dword.Write(ValueSnapshot.Missing("DWord"));
            Assert.False(dword.Read().Exists);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\PcToolkit.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void PowerSchemeTarget_ReadsActiveScheme()
    {
        var target = new PowerSchemeTarget();

        ValueSnapshot active = target.Read();

        Assert.True(active.Exists);
        Assert.True(Guid.TryParse(active.Data, out _));
        Assert.False(string.IsNullOrWhiteSpace(target.Describe(active)));
    }

    [Fact]
    public void ServiceStartTarget_ReadsExistingService()
    {
        var target = new ServiceStartTarget("EventLog");

        Assert.True(target.IsAvailable(out _));
        Assert.True(target.Read().Exists);
    }
}
