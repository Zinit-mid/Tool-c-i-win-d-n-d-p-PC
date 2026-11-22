using System.Text.Json;
using PCTool.Core.Windows;

namespace PCTool.Core.License;

public enum LicenseKind { Windows, Office, Other }

public sealed record LicenseEntry(LicenseKind Kind, string Name, int Status)
{
    public bool Licensed => Status == 1;

    public string StatusText => Status switch
    {
        1 => "Đã kích hoạt",
        0 => "Chưa được cấp phép",
        2 => "Đang trong thời gian ân hạn ban đầu",
        3 => "Đang trong thời gian ân hạn",
        4 => "Bản sao không chính hãng, đang trong thời gian ân hạn",
        5 => "Chưa kích hoạt (đang nhắc kích hoạt)",
        6 => "Đang trong thời gian ân hạn mở rộng",
        _ => $"Không rõ (mã {Status})"
    };
}

/// <summary>Chỉ ĐỌC trạng thái bản quyền. PCTool không kích hoạt, không đổi khóa, không bẻ khóa; cũng không hiện khóa sản phẩm.</summary>
public static class LicenseChecker
{
    public const string Script = """
$ErrorActionPreference = 'Stop'
$items = @(Get-CimInstance SoftwareLicensingProduct -Filter "PartialProductKey IS NOT NULL" | ForEach-Object {
  [ordered]@{ name = [string]$_.Name; app = [string]$_.ApplicationId; status = [int]$_.LicenseStatus }
})
ConvertTo-Json -InputObject @($items) -Compress -Depth 3
""";

    private const string WindowsApp = "55c92734-d682-4d71-983e-d6ec3f16059f";
    private const string OfficeApp = "0ff1ce15-a989-479d-af46-f275c6370663";

    public static IReadOnlyList<LicenseEntry> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var res = new List<LicenseEntry>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return res;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var app = (e.TryGetProperty("app", out var a) ? a.GetString() : "")?.ToLowerInvariant() ?? "";
            var name = new string((e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            int status = e.TryGetProperty("status", out var s) && s.TryGetInt32(out var x) ? x : -1;
            var kind = app == WindowsApp ? LicenseKind.Windows : app == OfficeApp ? LicenseKind.Office : LicenseKind.Other;
            if (kind == LicenseKind.Other || name.Length == 0) continue;
            res.Add(new LicenseEntry(kind, name, status));
        }
        return res;
    }

    public static async Task<IReadOnlyList<LicenseEntry>> ReadAsync(ScriptRunner? run, CancellationToken ct)
    {
        run ??= (s, c) => PowerShellRunner.RunAsync(s, c, 60);
        var r = await run(Script, ct);
        if (r.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.Error) ? "Không đọc được trạng thái bản quyền." : r.Error.Trim());
        return Parse(string.IsNullOrWhiteSpace(r.Output) ? "[]" : r.Output);
    }
}
