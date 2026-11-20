using PCTool.Core.Framework;

namespace PCTool.Core.Optimize;

/// <summary>
/// Dọn một loại rác thông qua khung chung (<see cref="FixExecutor"/>): có chạy thử, có khóa Chế độ an toàn,
/// có ghi nhật ký và kiểm tra lại sau khi dọn.
/// </summary>
public sealed class CleanupFix : IFix
{
    private readonly CleanupItem _item;
    private long _sizeBefore;

    public CleanupFix(CleanupItem item) => _item = item ?? throw new ArgumentNullException(nameof(item));

    public CleanupItem Item => _item;

    public string Title => $"Dọn {_item.Name}";

    public IReadOnlyList<string> Actions
    {
        get
        {
            if (_item.IsRecycleBin)
                return new[] { $"Dọn sạch thùng rác trên mọi ổ ({_item.SizeText}) — không thể khôi phục sau khi dọn" };

            var places = string.Join("; ", _item.Paths.Where(p => !string.IsNullOrWhiteSpace(p)));
            return new[]
            {
                $"Xóa {_item.FileCount} file ({_item.SizeText}) trong: {places}",
                "File đang được dùng sẽ được bỏ qua, không ảnh hưởng file cá nhân"
            };
        }
    }

    // Chỉ là file tạm/cache, tự tạo lại khi cần: không cần điểm khôi phục.
    public bool RequiresRestorePoint => false;

    // Đã xóa thì không khôi phục được (riêng thùng rác càng không).
    public bool IsUndoable => false;

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        _sizeBefore = _item.SizeBytes;
        progress.Report($"Đang dọn: {_item.Name}");

        var result = await Task.Run(() => CleanupScanner.Clean(_item, dryRun: false, ct), ct);

        var message = $"{result.DeletedFiles} file, {CleanupItem.FormatSize(result.FreedBytes)}" +
                      (result.SkippedFiles > 0 ? $", bỏ qua {result.SkippedFiles} file đang dùng" : "");
        return FixResult.Succeeded(message);
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        if (_sizeBefore <= 0) return true;
        await Task.Run(() => CleanupScanner.Scan(_item, ct), ct);
        return _item.SizeBytes < _sizeBefore;
    }
}

/// <summary>Quét các loại rác (chỉ đọc) và đưa ra từng loại có rác kèm cách dọn.</summary>
public sealed class CleanupCheck : ICheck
{
    private const long WarnBytes = 2L * 1024 * 1024 * 1024;

    private readonly Func<IReadOnlyList<CleanupItem>> _items;

    public CleanupCheck() : this(() => CleanupScanner.BuildItems()) { }

    public CleanupCheck(Func<IReadOnlyList<CleanupItem>> items) => _items = items;

    public string Id => "optimize.cleanup";
    public string Name => "Kiểm tra rác trong máy";

    public async Task<IReadOnlyList<Finding>> RunAsync(CancellationToken ct)
    {
        var findings = new List<Finding>();
        foreach (var item in _items())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Run(() => CleanupScanner.Scan(item, ct), ct);
            if (item.SizeBytes <= 0) continue;

            findings.Add(new Finding
            {
                Id = "cleanup." + (item.Id.Length > 0 ? item.Id : item.Name),
                Title = $"{item.Name}: {item.SizeText}",
                Cause = $"{item.Description}. Có thể dọn {item.FileCount} mục để giải phóng {item.SizeText}.",
                Severity = item.SizeBytes >= WarnBytes ? Severity.Warning : Severity.Info,
                Fixes = new IFix[] { new CleanupFix(item) }
            });
        }
        return findings;
    }
}
