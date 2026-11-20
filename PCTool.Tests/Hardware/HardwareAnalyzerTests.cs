using PCTool.Core.Framework;
using PCTool.Core.Hardware;
using PCTool.Core.Windows;

namespace PCTool.Tests.Hardware;

public sealed class HardwareAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private const long Gb = 1073741824L;

    private static HardwareReport Healthy() => new()
    {
        TotalRamBytes = 16 * Gb,
        Disks = new[] { new DiskInfo("SSD", "SSD", 500 * Gb, "Healthy", "OK") },
        Volumes = new[] { new VolumeInfo("C:", "", 500 * Gb, 200 * Gb) },
        Os = new OsInfo("Windows 11", "10", "22631", "64-bit", Now.AddDays(-2))
    };

    [Fact]
    public void HealthyMachine_HasNoFindings() =>
        Assert.Empty(HardwareAnalyzer.Analyze(Healthy(), Now));

    [Theory]
    [InlineData("Unhealthy", Severity.Critical)]
    [InlineData("Warning", Severity.Warning)]
    public void DiskHealth_IsReported(string health, Severity expected)
    {
        var report = new HardwareReport { Disks = new[] { new DiskInfo("HDD", "HDD", 1000 * Gb, health, "OK") } };

        var f = Assert.Single(HardwareAnalyzer.Analyze(report, Now));

        Assert.Equal(expected, f.Severity);
        Assert.Contains("HDD", f.Title);
        Assert.Empty(f.Fixes);   // phần cứng hỏng thì app không tự sửa
    }

    [Fact]
    public void UnknownDiskHealth_IsNotReportedAsProblem()
    {
        var report = new HardwareReport { Disks = new[] { new DiskInfo("X", "", 1 * Gb, "", "") } };

        Assert.Empty(HardwareAnalyzer.Analyze(report, Now));
    }

    [Theory]
    [InlineData(4, Severity.Critical)]
    [InlineData(9, Severity.Warning)]
    public void LowFreeSpace_IsReported(int freePercent, Severity expected)
    {
        var report = new HardwareReport { Volumes = new[] { new VolumeInfo("D:", "", 100 * Gb, freePercent * Gb) } };

        Assert.Equal(expected, Assert.Single(HardwareAnalyzer.Analyze(report, Now)).Severity);
    }

    [Fact]
    public void VolumeWithZeroSize_IsIgnored()
    {
        var report = new HardwareReport { Volumes = new[] { new VolumeInfo("E:", "", 0, 0) } };

        Assert.Empty(HardwareAnalyzer.Analyze(report, Now));
    }

    [Theory]
    [InlineData(2, Severity.Warning)]
    [InlineData(6, Severity.Info)]
    public void LowRam_IsReported(int gb, Severity expected)
    {
        var report = new HardwareReport { TotalRamBytes = gb * Gb };

        Assert.Equal(expected, Assert.Single(HardwareAnalyzer.Analyze(report, Now)).Severity);
    }

    [Theory]
    [InlineData(55000, Severity.Warning)]   // chai 45%
    [InlineData(30000, Severity.Critical)]  // chai 70%
    public void WornBattery_IsReported(long full, Severity expected)
    {
        var report = new HardwareReport { Battery = new BatteryInfo(100000, full, null) };

        Assert.Equal(expected, Assert.Single(HardwareAnalyzer.Analyze(report, Now)).Severity);
    }

    [Fact]
    public void GoodBattery_IsNotReported()
    {
        var report = new HardwareReport { Battery = new BatteryInfo(50000, 48000, 90) };

        Assert.Empty(HardwareAnalyzer.Analyze(report, Now));
    }

    [Fact]
    public void LongUptime_IsInfo()
    {
        var report = new HardwareReport { Os = new OsInfo("W", "1", "1", "64", Now.AddDays(-20)) };

        Assert.Equal(Severity.Info, Assert.Single(HardwareAnalyzer.Analyze(report, Now)).Severity);
    }

    [Fact]
    public void Findings_AreSortedMostSevereFirst()
    {
        var report = new HardwareReport
        {
            TotalRamBytes = 6 * Gb,
            Disks = new[] { new DiskInfo("D", "HDD", Gb, "Unhealthy", "OK") }
        };

        var severities = HardwareAnalyzer.Analyze(report, Now).Select(f => f.Severity).ToArray();

        Assert.Equal(new[] { Severity.Critical, Severity.Info }, severities);
    }
}

public sealed class WmiHardwareReaderTests
{
    [Fact]
    public async Task ReadAsync_ParsesRunnerOutput()
    {
        var reader = new WmiHardwareReader((_, _) =>
            Task.FromResult(new PowerShellResult(0, """{"totalRam":8589934592}""", "")));

        var report = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(8589934592, report.TotalRamBytes);
    }

    [Fact]
    public async Task ReadAsync_EmptyOutput_ThrowsWithReason()
    {
        var reader = new WmiHardwareReader((_, _) =>
            Task.FromResult(new PowerShellResult(1, "", "Access denied")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(CancellationToken.None));

        Assert.Contains("Access denied", ex.Message);
    }

    [Fact]
    public async Task ReadAsync_GarbageOutput_ThrowsInvalidOperation()
    {
        var reader = new WmiHardwareReader((_, _) =>
            Task.FromResult(new PowerShellResult(0, "{ hỏng", "")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public void Script_IsReadOnly_NoModifyingCommands()
    {
        var s = WmiHardwareReader.Script;

        foreach (var bad in new[] { "Set-", "Remove-", "Stop-", "Start-", "Restart-", "New-Item", "Invoke-", "Out-File" })
            Assert.DoesNotContain(bad, s);
    }

    [Fact]
    public async Task HealthCheck_ReturnsFindingsFromReader()
    {
        var reader = new WmiHardwareReader((_, _) =>
            Task.FromResult(new PowerShellResult(0, """{"totalRam":2147483648}""", "")));
        var check = new HardwareHealthCheck(reader, () => DateTimeOffset.UnixEpoch);

        var findings = await check.RunAsync(CancellationToken.None);

        Assert.Equal("ram.low", Assert.Single(findings).Id);
    }
}
