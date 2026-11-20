using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Tests.Framework;

public sealed class RestorePointServiceTests
{
    [Theory]
    [InlineData("a'; Remove-Item C:\\x -Recurse; '")]
    [InlineData("x$(calc)y")]
    [InlineData("dòng 1\r\ndòng 2`n")]
    public void Sanitize_RemovesCharactersThatCouldBreakOutOfTheQuotedString(string input)
    {
        var safe = RestorePointService.Sanitize(input);

        Assert.DoesNotContain("'", safe);
        Assert.DoesNotContain(";", safe);
        Assert.DoesNotContain("$", safe);
        Assert.DoesNotContain("`", safe);
        Assert.DoesNotContain("\n", safe);
        Assert.DoesNotContain("\r", safe);
    }

    [Fact]
    public void Sanitize_KeepsVietnameseText()
    {
        Assert.Equal("PCTool: Tắt ứng dụng khởi động", RestorePointService.Sanitize("PCTool: Tắt ứng dụng khởi động"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("';$`")]
    public void Sanitize_EmptyResult_FallsBackToDefaultName(string? input)
    {
        Assert.Equal("PCTool", RestorePointService.Sanitize(input));
    }

    [Fact]
    public void Sanitize_LimitsLength()
    {
        Assert.True(RestorePointService.Sanitize(new string('a', 500)).Length <= 100);
    }

    [Fact]
    public void BuildScript_PutsDescriptionInsideOneQuotedLiteral()
    {
        var script = RestorePointService.BuildScript("Mô tả '; Stop-Computer; '");

        // Chỉ có đúng 1 cặp nháy đơn bao quanh mô tả trong dòng Checkpoint-Computer.
        var line = script.Split('\n').Single(l => l.Contains("Checkpoint-Computer"));
        Assert.Equal(4, line.Count(c => c == '\''));   // '<mô tả>' và 'MODIFY_SETTINGS'
        Assert.DoesNotContain(";", line.Split("-Description")[1].Split("-RestorePointType")[0]);
    }

    [Fact]
    public void ParseOutput_Created()
    {
        Assert.Equal(RestorePointStatus.Created, RestorePointService.ParseOutput("CREATED\r\n").Status);
    }

    [Fact]
    public void ParseOutput_Skipped()
    {
        Assert.Equal(RestorePointStatus.Skipped, RestorePointService.ParseOutput("SKIPPED").Status);
    }

    [Fact]
    public void ParseOutput_Failed_KeepsReason()
    {
        var r = RestorePointService.ParseOutput("FAILED:Bảo vệ hệ thống đang tắt");

        Assert.Equal(RestorePointStatus.Failed, r.Status);
        Assert.Contains("Bảo vệ hệ thống đang tắt", r.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("kết quả lạ")]
    public void ParseOutput_Unknown_IsFailed_NeverCreated(string? output)
    {
        Assert.Equal(RestorePointStatus.Failed, RestorePointService.ParseOutput(output).Status);
    }

    [Fact]
    public async Task CreateAsync_UsesRunnerOutput()
    {
        string? scriptSeen = null;
        var service = new RestorePointService((script, _) =>
        {
            scriptSeen = script;
            return Task.FromResult(new PowerShellResult(0, "CREATED", ""));
        });

        var result = await service.CreateAsync("PCTool: thử", CancellationToken.None);

        Assert.Equal(RestorePointStatus.Created, result.Status);
        Assert.Contains("Checkpoint-Computer", scriptSeen);
    }

    [Fact]
    public async Task CreateAsync_RunnerThrows_ReturnsFailed_InsteadOfCrashing()
    {
        var service = new RestorePointService((_, _) => throw new InvalidOperationException("không có PowerShell"));

        var result = await service.CreateAsync("x", CancellationToken.None);

        Assert.Equal(RestorePointStatus.Failed, result.Status);
        Assert.Contains("không có PowerShell", result.Message);
    }

    [Fact]
    public async Task CreateAsync_Cancellation_Propagates()
    {
        var service = new RestorePointService((_, _) => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.CreateAsync("x", CancellationToken.None));
    }
}
