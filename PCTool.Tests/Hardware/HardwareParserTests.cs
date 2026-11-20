using PCTool.Core.Hardware;

namespace PCTool.Tests.Hardware;

public sealed class HardwareParserTests
{
    private const string Full = """
    {
      "cpu":[{"Name":"Intel(R) Core(TM) i5-10400 CPU","NumberOfCores":6,"NumberOfLogicalProcessors":12,"MaxClockSpeed":2900}],
      "totalRam":17048674304,
      "ram":[{"Capacity":8589934592,"Speed":2666},{"Capacity":8589934592,"Speed":2666}],
      "gpu":[{"Name":"NVIDIA GeForce RTX 3060","DriverVersion":"31.0.15.5123","AdapterRAM":4293918720}],
      "board":[{"Manufacturer":"ASUSTeK","Product":"PRIME B460M-A"}],
      "os":[{"Caption":"Microsoft Windows 11 Pro","Version":"10.0.22631","BuildNumber":"22631","OSArchitecture":"64-bit","LastBoot":"2026-11-10T08:00:00.0000000+07:00"}],
      "disks":[{"FriendlyName":"Samsung SSD 970","MediaType":"SSD","Size":500107862016,"HealthStatus":"Healthy","OperationalStatus":"OK"}],
      "volumes":[{"DeviceID":"C:","VolumeName":"Windows","Size":500000000000,"FreeSpace":100000000000}],
      "batteryCharge":[{"EstimatedChargeRemaining":80}],
      "batteryDesign":[{"DesignedCapacity":50000}],
      "batteryFull":[{"FullChargedCapacity":40000}]
    }
    """;

    [Fact]
    public void Parse_FullDocument_ReadsEverything()
    {
        var r = HardwareParser.Parse(Full);

        var cpu = Assert.Single(r.Cpus);
        Assert.Equal(6, cpu.Cores);
        Assert.Equal(12, cpu.LogicalProcessors);
        Assert.Equal(17048674304, r.TotalRamBytes);
        Assert.Equal(2, r.RamModules);
        Assert.Equal(2666, r.RamSpeedMhz);
        Assert.Equal("NVIDIA GeForce RTX 3060", Assert.Single(r.Gpus).Name);
        Assert.Equal("PRIME B460M-A", r.Board?.Product);
        Assert.Equal("22631", r.Os?.Build);
        Assert.NotNull(r.Os?.LastBoot);
        Assert.Equal("Healthy", Assert.Single(r.Disks).HealthStatus);
        Assert.Equal(20, Assert.Single(r.Volumes).FreePercent, 3);
        Assert.Equal(80, r.Battery?.ChargePercent);
        Assert.Equal(20, r.Battery?.WearPercent ?? -1, 3);
    }

    [Fact]
    public void Parse_SingleObjectInsteadOfArray_IsAccepted()
    {
        var r = HardwareParser.Parse("""{"cpu":{"Name":"X","NumberOfCores":2,"NumberOfLogicalProcessors":4,"MaxClockSpeed":3000}}""");

        Assert.Equal("X", Assert.Single(r.Cpus).Name);
    }

    [Fact]
    public void Parse_Desktop_HasNoBattery()
    {
        var r = HardwareParser.Parse("""{"cpu":[],"batteryCharge":[],"batteryDesign":[],"batteryFull":[]}""");

        Assert.Null(r.Battery);
    }

    [Fact]
    public void Parse_MissingAndNullFields_DoNotThrow()
    {
        var r = HardwareParser.Parse("""{"gpu":[{"Name":"GPU","DriverVersion":null,"AdapterRAM":null}],"disks":[{"FriendlyName":"D"}]}""");

        Assert.Null(Assert.Single(r.Gpus).DriverVersion);
        Assert.Equal(0, r.Disks[0].SizeBytes);
        Assert.Equal("", r.Disks[0].HealthStatus);
    }

    [Fact]
    public void Parse_NumbersAsStrings_AreAccepted()
    {
        var r = HardwareParser.Parse("""{"volumes":[{"DeviceID":"D:","Size":"1000","FreeSpace":"250"}]}""");

        Assert.Equal(25, r.Volumes[0].FreePercent, 3);
    }

    [Theory]
    [InlineData("không phải json")]
    [InlineData("[1,2]")]
    public void Parse_BadInput_Throws(string json)
    {
        Assert.ThrowsAny<Exception>(() => HardwareParser.Parse(json));
    }

    [Fact]
    public void ToText_ContainsKeyFacts()
    {
        var text = HardwareParser.Parse(Full).ToText();

        Assert.Contains("RTX 3060", text);
        Assert.Contains("RAM: 15.9 GB", text);
        Assert.Contains("Ổ C:", text);
        Assert.Contains("Pin: đã chai khoảng 20%", text);
    }
}
