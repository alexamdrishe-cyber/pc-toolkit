using PcToolkit.Core.Rating;

namespace PcToolkit.Core.Tests;

public sealed class RatingTests
{
    [Theory]
    [InlineData("AMD Ryzen 5 5600 6-Core Processor", "AMD Ryzen 5 5600")]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor  ", "AMD Ryzen 7 5800X3D")]
    [InlineData("Intel(R) Core(TM) i5-10400F CPU @ 2.90GHz", "Intel Core i5-10400F")]
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700K", "12th Gen Intel Core i7-12700K")]
    [InlineData("AMD Ryzen 5 5600G with Radeon Graphics", "AMD Ryzen 5 5600G")]
    [InlineData("AMD Radeon RX 6700 XT", "AMD Radeon RX 6700 XT")]
    [InlineData("NVIDIA GeForce RTX™ 4070", "NVIDIA GeForce RTX 4070")]
    public void ModelKeyGroupsSameModelWrittenDifferently(string model, string key)
    {
        Assert.Equal(key, RatingStats.ModelKey(model));
    }

    [Fact]
    public void EachMachineCountsOnceWhateverTheNumberOfRuns()
    {
        // Один ПК прогнал тест 20 раз с самым высоким результатом, ещё четыре — по разу:
        // медиана считается по компьютерам (10200), а не по прогонам (было бы 10600).
        var results = Enumerable.Range(0, 20).Select(_ => (10600.0, (double?)null, "aaaaaaaa"))
            .Concat(new[]
            {
                (10000.0, (double?)null, "bbbbbbbb"), (10100.0, (double?)null, "cccccccc"),
                (10200.0, (double?)null, "dddddddd"), (10300.0, (double?)null, "eeeeeeee"),
            });

        RatingModel model = RatingStats.Summarize("X", results)!;

        Assert.Equal(5, model.Machines);
        Assert.Equal(24, model.Results);
        Assert.Equal(10200, model.Median);
    }

    [Fact]
    public void FakeAndBrokenResultsDoNotMoveTheMedian()
    {
        var results = new List<(double, double?, string)>
        {
            (10150, 450, "m1"), (10210, 455, "m2"), (10250, 452, "m3"), (10300, 460, "m4"), (10190, 448, "m5"),
            (99000, 900, "cheat"), // подделка
            (2100, 100, "broken"), // перегрев или фоновая нагрузка
        };

        RatingModel model = RatingStats.Summarize("AMD Radeon RX 6700 XT", results)!;

        Assert.Equal(5, model.Machines);
        Assert.Equal(5, model.Results);
        Assert.InRange(model.Median, 10190, 10250);
        Assert.InRange(model.Top, 10250, 10300);
        Assert.True(model.Verified);
        Assert.InRange(model.Median2!.Value, 448, 460);
    }

    [Fact]
    public void FewMachinesAreNotVerified()
    {
        // Много прогонов, но всего с двух компьютеров — ещё не ориентир.
        var results = Enumerable.Range(0, 10).Select(i => (5000.0 + i, (double?)null, i % 2 == 0 ? "pc-one" : "pc-two"));

        RatingModel model = RatingStats.Summarize("Y", results)!;

        Assert.Equal(10, model.Results);
        Assert.Equal(2, model.Machines);
        Assert.False(model.Verified);
        Assert.Equal(RatingVerdict.Unknown, RatingStats.Compare(5000, model).Verdict);
    }

    [Fact]
    public void NoResultsGiveNoModel()
    {
        Assert.Null(RatingStats.Summarize("Z", Array.Empty<(double, double?, string)>()));
        Assert.Null(RatingStats.Summarize("Z", new[] { (0.0, (double?)null, "m1") }));
    }

    [Fact]
    public void CompareTellsBelowNormalAndAbove()
    {
        var model = new RatingModel { Median = 10000, Low = 9600, High = 10300, Verified = true };

        Assert.Equal(RatingVerdict.Normal, RatingStats.Compare(9500, model).Verdict); // в пределах запаса 3 %
        Assert.Equal(RatingVerdict.Below, RatingStats.Compare(8500, model).Verdict);
        Assert.Equal(RatingVerdict.Above, RatingStats.Compare(10800, model).Verdict);
        Assert.Equal(-0.15, RatingStats.Compare(8500, model).Deviation, 3);
        Assert.Equal(RatingVerdict.Unknown, RatingStats.Compare(8500, null).Verdict);
    }

    [Fact]
    public void PercentileInterpolates()
    {
        double[] sorted = { 10, 20, 30, 40 };
        Assert.Equal(25, RatingStats.Percentile(sorted, 50));
        Assert.Equal(10, RatingStats.Percentile(sorted, 0));
        Assert.Equal(40, RatingStats.Percentile(sorted, 100));
        Assert.Equal(0, RatingStats.Percentile(Array.Empty<double>(), 50));
    }

    [Fact]
    public void SubmissionValidationRejectsJunk()
    {
        RatingSubmission Good() => new()
        {
            ClientId = Guid.NewGuid().ToString("N"),
            Kind = RatingKinds.Cpu,
            Model = "AMD Ryzen 5 5600",
            Score = 12345,
            Score2 = 1500,
            TestVersion = 2,
            Machine = new string('a', 64),
            AppVersion = "0.5.0",
        };

        Assert.Null(Good().Validate());
        Assert.NotNull(new RatingSubmission { Kind = "cpu" }.Validate());

        RatingSubmission bad = Good();
        bad.Kind = "fps";
        Assert.Equal("неизвестный вид теста", bad.Validate());

        bad = Good();
        bad.Score = 9_999_999;
        Assert.Equal("результат вне разумных пределов", bad.Validate());

        bad = Good();
        bad.Score = double.NaN;
        Assert.Equal("результат вне разумных пределов", bad.Validate());

        bad = Good();
        bad.Machine = "not-a-hash-value!";
        Assert.Equal("нет номера компьютера", bad.Validate());

        bad = Good();
        bad.Model = " ";
        Assert.Equal("нет модели устройства", bad.Validate());

        bad = Good();
        bad.TestVersion = 0;
        Assert.Equal("неизвестная версия теста", bad.Validate());

        bad = Good();
        bad.Conditions = Enumerable.Range(0, 20).ToDictionary(i => "k" + i, i => "v");
        Assert.Equal("слишком много сведений", bad.Validate());
    }
}

public sealed class RatingClientTests
{
    [Fact]
    public void ConfigGivesHttpsServerOnly()
    {
        Assert.Equal("https://brave-otter-1234.ru.tuna.am", RatingClient.ParseConfig("""{"ratingUrl":"https://brave-otter-1234.ru.tuna.am/"}"""));
        Assert.Null(RatingClient.ParseConfig("""{"ratingUrl":"http://example.com"}"""));
        Assert.Null(RatingClient.ParseConfig("""{"ratingUrl":""}"""));
        Assert.Null(RatingClient.ParseConfig("""{"other":1}"""));
        Assert.Null(RatingClient.ParseConfig("[]"));

        // Свой сервер в домашней сети — можно и http
        Assert.Equal("http://192.168.1.20:8080", RatingClient.NormalizeServer("http://192.168.1.20:8080/", allowHttp: true));
        Assert.Null(RatingClient.NormalizeServer("ftp://x", allowHttp: true));
        Assert.Null(RatingClient.NormalizeServer("https://x.ru/?a=1", allowHttp: false));
    }

    [Fact]
    public void MachineHashIsStableAndHidesTheNumber()
    {
        string guid = "6f1f2c3a-1234-4bcd-9abc-0123456789ab";
        string hash = RatingClient.Hash(guid);
        Assert.Equal(64, hash.Length);
        Assert.True(hash.All(Uri.IsHexDigit));
        Assert.Equal(hash, RatingClient.Hash(guid));
        Assert.NotEqual(hash, RatingClient.Hash(guid.Replace('6', '7')));
        Assert.DoesNotContain("6f1f2c3a", hash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepoNameIsChecked()
    {
        Assert.Equal("someone/pc-toolkit", PcToolkit.Core.Updates.ProjectLinks.ValidRepo(" someone/pc-toolkit/ "));
        Assert.Equal("", PcToolkit.Core.Updates.ProjectLinks.ValidRepo("https://github.com/someone/pc-toolkit"));
        Assert.Equal("", PcToolkit.Core.Updates.ProjectLinks.ValidRepo("someone"));
        Assert.Equal("", PcToolkit.Core.Updates.ProjectLinks.ValidRepo(null));
    }

    [Fact]
    public void RecordsBecomeSubmissions()
    {
        var cpu = new PcToolkit.Core.Benchmarks.CpuBenchmarkRecord
        {
            At = DateTimeOffset.Now,
            Cpu = "AMD Ryzen 5 5600 6-Core Processor",
            Threads = 12,
            Scores = new()
            {
                new(PcToolkit.Core.Benchmarks.CpuWorkload.Integer, 11.24, 75, ""),
                new(PcToolkit.Core.Benchmarks.CpuWorkload.Float, 72.9, 480, ""),
                new(PcToolkit.Core.Benchmarks.CpuWorkload.Cache, 680, 4500, ""),
            },
        };
        RatingSubmission s = RatingSubmissions.FromCpu(cpu)!;
        Assert.Equal(RatingKinds.Cpu, s.Kind);
        Assert.Equal(1000, s.Score2);
        Assert.True(s.Score > 5000);
        Assert.Equal(cpu.Id.ToString("N"), s.ClientId);
        Assert.Equal("12", s.Conditions!["threads"]);

        // Результат с другого ПК (из файла) и неполный замер в рейтинг не идут.
        cpu.Source = "friend.json";
        Assert.Null(RatingSubmissions.FromCpu(cpu));
        Assert.Null(RatingSubmissions.FromCpu(new PcToolkit.Core.Benchmarks.CpuBenchmarkRecord { Cpu = "X" }));

        var disk = new PcToolkit.Core.Benchmarks.Disk.DiskBenchmarkRecord
        {
            At = DateTimeOffset.Now,
            Model = "Samsung SSD 980 · SSD NVMe",
            FileSize = 1L << 30,
            Results = new()
            {
                new("SEQ1M Q8T1", 3456.7, 3000, 0, 0, 0, 0),
                new("RND4K Q1T1", 65.432, 150, 0, 0, 0, 0),
            },
        };
        RatingSubmission d = RatingSubmissions.FromDisk(disk)!;
        Assert.Equal(3456.7, d.Score);
        Assert.Equal(65.43, d.Score2);
        Assert.Equal("1024", d.Conditions!["fileMb"]);

        var memory = new PcToolkit.Core.Benchmarks.Memory.MemoryBenchmarkRecord { Memory = "DDR4-3200, 2 модуля, два канала", Read = 45.678, Latency = 71.23 };
        RatingSubmission m = RatingSubmissions.FromMemory(memory)!;
        Assert.Equal(45.68, m.Score);
        Assert.Equal(71.2, m.Score2);
        Assert.Null(RatingSubmissions.FromMemory(new PcToolkit.Core.Benchmarks.Memory.MemoryBenchmarkRecord { Read = 40 }));

        var gpu = new PcToolkit.Core.Benchmarks.Gpu.GpuBenchmarkRecord { Gpu = "AMD Radeon RX 6700 XT", Fill = 23.2, Gflops = 12077, Bandwidth = 320 };
        Assert.Equal(10000, RatingSubmissions.FromGpu(gpu)!.Score);
    }
}
