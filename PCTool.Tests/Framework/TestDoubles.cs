using PCTool.Core.Framework;

namespace PCTool.Tests.Framework;

/// <summary>Cách sửa giả để kiểm thử FixExecutor. Đếm số lần được gọi.</summary>
internal sealed class FakeFix : IFix
{
    public string Title { get; init; } = "Sửa thử";
    public IReadOnlyList<string> Actions { get; init; } = new[] { "Việc 1", "Việc 2" };
    public bool RequiresRestorePoint { get; init; }
    public bool IsUndoable { get; init; } = true;

    public Func<FixResult> OnExecute { get; init; } = () => FixResult.Succeeded("Xong");
    public Func<bool> OnVerify { get; init; } = () => true;

    public int ExecuteCalls { get; private set; }
    public int VerifyCalls { get; private set; }

    public Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        ExecuteCalls++;
        return Task.FromResult(OnExecute());
    }

    public Task<bool> VerifyAsync(CancellationToken ct)
    {
        VerifyCalls++;
        return Task.FromResult(OnVerify());
    }
}

internal sealed class FakeRestorePoints : IRestorePointService
{
    public RestorePointResult Result { get; init; } = new(RestorePointStatus.Created, "ok");
    public int Calls { get; private set; }
    public string? LastDescription { get; private set; }

    public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct)
    {
        Calls++;
        LastDescription = description;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeCheck : ICheck
{
    private readonly Func<IReadOnlyList<Finding>> _run;

    public FakeCheck(string id, Func<IReadOnlyList<Finding>> run)
    {
        Id = id;
        Name = "Kiểm tra " + id;
        _run = run;
    }

    public string Id { get; }
    public string Name { get; }

    public Task<IReadOnlyList<Finding>> RunAsync(CancellationToken ct) => Task.FromResult(_run());
}

/// <summary>Progress chạy đồng bộ (Progress&lt;T&gt; mặc định gửi qua luồng khác nên khó kiểm thử).</summary>
internal sealed class ListProgress<T> : IProgress<T>
{
    public List<T> Items { get; } = new();

    public void Report(T value) => Items.Add(value);
}

internal static class Make
{
    public static Finding Finding(string title, Severity severity) => new()
    {
        Id = "test." + title,
        Title = title,
        Cause = "Nguyên nhân thử",
        Severity = severity
    };
}
