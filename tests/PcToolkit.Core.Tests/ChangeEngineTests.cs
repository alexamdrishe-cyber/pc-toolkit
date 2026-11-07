using PcToolkit.Core.Changes;
using PcToolkit.Core.Tweaks;
using static PcToolkit.Core.Tests.FakeSystem;

namespace PcToolkit.Core.Tests;

public sealed class ChangeEngineTests : IDisposable
{
    private readonly string _journalPath = Path.Combine(Path.GetTempPath(), "PcToolkit.Tests", Guid.NewGuid().ToString("N") + ".json");
    private readonly FakeSystem _system = new();

    public void Dispose()
    {
        if (File.Exists(_journalPath))
            File.Delete(_journalPath);
    }

    private ChangeEngine CreateEngine(bool elevated = true) =>
        new(ChangeJournal.Load(_journalPath), _system, () => elevated, windowsBuild: 19045);

    [Fact]
    public void Apply_ChangesValues_AndRecordsJournal()
    {
        _system.Set("a", "1");
        TweakDefinition tweak = Tweak("t1", ("a", "2"), ("b", "5"));
        ChangeEngine engine = CreateEngine();

        ChangePlan plan = engine.PlanApply([tweak]);
        Assert.True(plan.CanExecute);
        Assert.Equal(2, plan.Items.Count);

        ChangeResult result = engine.Execute(plan);

        Assert.True(result.Success, result.Message);
        Assert.Equal("2", _system.Get("a"));
        Assert.Equal("5", _system.Get("b"));
        ChangeSet set = Assert.Single(engine.Journal.Sets);
        Assert.Equal(ChangeSetStatus.Applied, set.Status);
        Assert.All(set.Entries, entry => Assert.True(entry.Done));

        TweakStatus status = engine.GetStatus(tweak);
        Assert.Equal(TweakState.Applied, status.State);
        Assert.True(status.CanRevert);
    }

    [Fact]
    public void Plan_SkipsValuesThatAlreadyHaveDesiredValue()
    {
        _system.Set("a", "2");
        ChangeEngine engine = CreateEngine();

        ChangePlan plan = engine.PlanApply([Tweak("t1", ("a", "2"), ("b", "5"))]);

        PlannedChange item = Assert.Single(plan.Items);
        Assert.Equal("b", item.Target.DisplayName);
        Assert.Contains(plan.Skipped, text => text.Contains("уже имеет нужное значение"));
    }

    [Fact]
    public void Revert_RestoresPreviousValues_NotDefaults()
    {
        _system.Set("a", "1");
        TweakDefinition tweak = Tweak("t1", ("a", "2"), ("b", "5"));
        ChangeEngine engine = CreateEngine();
        Assert.True(engine.Execute(engine.PlanApply([tweak])).Success);

        ChangeResult result = engine.Execute(engine.PlanRevertTweak(tweak));

        Assert.True(result.Success, result.Message);
        Assert.Equal("1", _system.Get("a"));
        Assert.Null(_system.Get("b"));
        Assert.Empty(engine.ActiveEntries());
        Assert.Equal(TweakState.NotApplied, engine.GetStatus(tweak).State);
    }

    [Fact]
    public void RevertType_RestoresOnlyThatKind_AcrossSets()
    {
        // Так программа убирает настройки видеокарты, оставшиеся от прежнего экрана «Разгон».
        _system.Set("gpu1", "100");
        _system.Set("reg", "1");
        TweakDefinition gpuA = TypedTweak("g1", "gpu", "gpu1", "150");
        TweakDefinition gpuB = TypedTweak("g2", "gpu", "gpu2", "7");
        TweakDefinition other = Tweak("r1", ("reg", "2"));
        ChangeEngine engine = CreateEngine();
        Assert.True(engine.Execute(engine.PlanApply([gpuA])).Success);
        Assert.True(engine.Execute(engine.PlanApply([gpuB])).Success);
        Assert.True(engine.Execute(engine.PlanApply([other])).Success);

        ChangePlan plan = engine.PlanRevertType("gpu", "Вернуть заводские");
        Assert.Equal(2, plan.Items.Count);
        ChangeResult result = engine.Execute(plan);

        Assert.True(result.Success, result.Message);
        Assert.Equal("100", _system.Get("gpu1"));
        Assert.Null(_system.Get("gpu2"));
        Assert.Equal("2", _system.Get("reg"));
        Assert.DoesNotContain(engine.ActiveEntries().Values, e => e.Target.Type == "gpu");
        Assert.Empty(engine.PlanRevertType("gpu", "Вернуть заводские").Items);
    }

    private static TweakDefinition TypedTweak(string id, string type, string key, string value) => new()
    {
        Id = id,
        Title = id,
        Description = "тест",
        Category = "test",
        Changes = [new ChangeDefinition { Type = type, Path = key, Value = value }],
    };

    [Fact]
    public void Revert_DoesNotOverwriteChangeMadeOutsideTheApp()
    {
        _system.Set("a", "1");
        TweakDefinition tweak = Tweak("t1", ("a", "2"), ("b", "5"));
        ChangeEngine engine = CreateEngine();
        Assert.True(engine.Execute(engine.PlanApply([tweak])).Success);

        _system.Set("a", "9");
        ChangePlan plan = engine.PlanRevertTweak(tweak);
        ChangeResult result = engine.Execute(plan);

        Assert.True(result.Success, result.Message);
        Assert.Equal("9", _system.Get("a"));
        Assert.Null(_system.Get("b"));
        Assert.Contains(plan.Skipped, text => text.Contains("изменено вне приложения"));
        Assert.Empty(engine.ActiveEntries());
    }

    [Fact]
    public void Conflict_TwoTweaksSetSameParameterDifferently()
    {
        ChangeEngine engine = CreateEngine();

        ChangePlan plan = engine.PlanApply([Tweak("t1", ("a", "2")), Tweak("t2", ("a", "3"))]);

        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Problems, text => text.Contains("по-разному"));
    }

    [Fact]
    public void Conflict_DeclaredOnlyOnOneSide_IsReportedOnce()
    {
        TweakDefinition zeta = Tweak("zeta", ("a", "2"));
        zeta.ConflictsWith.Add("alpha");
        TweakDefinition alpha = Tweak("alpha", ("b", "2"));
        ChangeEngine engine = CreateEngine();

        ChangePlan plan = engine.PlanApply([zeta, alpha]);

        Assert.False(plan.CanExecute);
        Assert.Single(plan.Problems, text => text.Contains("несовместимы"));
    }

    [Fact]
    public void Conflict_ParameterOwnedByAnotherAppliedTweak()
    {
        ChangeEngine engine = CreateEngine();
        Assert.True(engine.Execute(engine.PlanApply([Tweak("t1", ("a", "2"))])).Success);

        ChangePlan plan = engine.PlanApply([Tweak("t2", ("a", "3"))]);

        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Problems, text => text.Contains("уже изменён настройкой «t1»"));
    }

    [Fact]
    public void Failure_RollsBackChangesAlreadyMadeInTheSet()
    {
        _system.Set("a", "1");
        _system.FailOnWrite.Add("c");
        ChangeEngine engine = CreateEngine();

        ChangeResult result = engine.Execute(engine.PlanApply([Tweak("t1", ("a", "2"), ("b", "3"), ("c", "4"))]));

        Assert.False(result.Success);
        Assert.Equal("1", _system.Get("a"));
        Assert.Null(_system.Get("b"));
        Assert.Null(_system.Get("c"));
        Assert.Equal(ChangeSetStatus.RolledBack, Assert.Single(engine.Journal.Sets).Status);
        Assert.Empty(engine.ActiveEntries());
    }

    [Fact]
    public void Recovery_RollsBackSetInterruptedByCrash()
    {
        // Журнал в состоянии «приложение упало посреди применения»: значение записано, набор не завершён.
        _system.Set("a", "2");
        ChangeJournal journal = ChangeJournal.Load(_journalPath);
        journal.Add(new ChangeSet
        {
            Kind = ChangeSetKind.Apply,
            Title = "прерванный набор",
            Status = ChangeSetStatus.Pending,
            Entries =
            {
                new ChangeEntry
                {
                    TweakId = "t1",
                    TargetKey = "fake:a",
                    TargetDisplay = "a",
                    Target = new ChangeDefinition { Type = "fake", Path = "a" },
                    Before = V("1"),
                    After = V("2"),
                    Done = true,
                },
            },
        });

        ChangeEngine engine = CreateEngine();
        (int recovered, string? problems) = engine.RecoverInterrupted();

        Assert.Equal(1, recovered);
        Assert.Null(problems);
        Assert.Equal("1", _system.Get("a"));
        Assert.Equal(ChangeSetStatus.RolledBack, Assert.Single(engine.Journal.Sets).Status);
    }

    [Fact]
    public void AdminParameter_WithoutElevation_BlocksPlan()
    {
        _system.AdminOnly.Add("a");
        ChangeEngine engine = CreateEngine(elevated: false);

        ChangePlan plan = engine.PlanApply([Tweak("t1", ("a", "2"))]);

        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Problems, text => text.Contains("права администратора"));
    }

    [Fact]
    public void Execute_RefusesIfValueChangedAfterPreview()
    {
        _system.Set("a", "1");
        ChangeEngine engine = CreateEngine();
        ChangePlan plan = engine.PlanApply([Tweak("t1", ("a", "2"))]);

        _system.Set("a", "7");
        ChangeResult result = engine.Execute(plan);

        Assert.False(result.Success);
        Assert.Equal("7", _system.Get("a"));
        Assert.Empty(engine.Journal.Sets);
    }

    [Fact]
    public void RevertSet_UndoesWholeSet()
    {
        _system.Set("a", "1");
        ChangeEngine engine = CreateEngine();
        ChangeResult applied = engine.Execute(engine.PlanApply([Tweak("t1", ("a", "2")), Tweak("t2", ("b", "3"))]));
        Assert.True(applied.Success);

        ChangeResult reverted = engine.Execute(engine.PlanRevertSet(applied.Set!.Id));

        Assert.True(reverted.Success, reverted.Message);
        Assert.Equal("1", _system.Get("a"));
        Assert.Null(_system.Get("b"));
        Assert.False(engine.PlanRevertSet(applied.Set.Id).CanExecute);
    }

    [Fact]
    public void Journal_SurvivesReload()
    {
        _system.Set("a", "1");
        ChangeEngine engine = CreateEngine();
        Assert.True(engine.Execute(engine.PlanApply([Tweak("t1", ("a", "2"))])).Success);

        ChangeJournal reloaded = ChangeJournal.Load(_journalPath);

        ChangeEntry entry = Assert.Single(Assert.Single(reloaded.Sets).Entries);
        Assert.Equal(V("1"), entry.Before);
        Assert.Equal(V("2"), entry.After);
        Assert.Equal("t1", entry.TweakId);
    }

    [Fact]
    public void Status_AppliedOutsideTheApp_CannotBeReverted()
    {
        _system.Set("a", "2");
        TweakDefinition tweak = Tweak("t1", ("a", "2"));

        TweakStatus status = CreateEngine().GetStatus(tweak);

        Assert.Equal(TweakState.AppliedOutside, status.State);
        Assert.False(status.CanRevert);
    }
}
