using System.Text.Json;
using PCTool.Core.Framework;
using PCTool.Core.Windows;

namespace PCTool.Core.Tweaks;

/// <summary>
/// Một thiết lập Windows nhỏ, đổi được qua 1 giá trị DWORD trong HKCU (tài khoản đang dùng). Danh sách CỐ ĐỊNH trong code,
/// không nhận đường dẫn registry từ ngoài. KHÔNG có mục nào về bảo mật (UAC, SmartScreen, tường lửa, Defender, thông báo bảo mật).
/// </summary>
public sealed record RegistryTweak(string Id, string Title, string Note, string Key, string Name, int OnValue, int OffValue);

public static class TweakCatalog
{
    private const string Adv = @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    public static readonly IReadOnlyList<RegistryTweak> All = new[]
    {
        new RegistryTweak("hideext", "Hiện đuôi file (.txt, .exe, .docx...)", "Giúp nhận ra file lạ như anh.jpg.exe", Adv, "HideFileExt", 0, 1),
        new RegistryTweak("launchto", "Explorer mở vào This PC (thay vì Quick access)", "Áp dụng khi mở cửa sổ Explorer mới", Adv, "LaunchTo", 1, 2),
        new RegistryTweak("taskbarleft", "Windows 11: căn nút Start sang trái", "Chỉ có tác dụng trên Windows 11", Adv, "TaskbarAl", 0, 1),
        new RegistryTweak("taskview", "Ẩn nút Task View trên thanh taskbar", "Bật = ẩn nút, Tắt = hiện lại", Adv, "ShowTaskViewButton", 0, 1),
        new RegistryTweak("darkapps", "Chế độ tối cho ứng dụng", "Chỉ đổi giao diện ứng dụng, không đổi thanh taskbar", @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0, 1),
        new RegistryTweak("thispc", "Hiện biểu tượng This PC trên Desktop", "Mở lại cửa sổ hoặc bấm F5 trên Desktop để thấy", @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", 0, 1)
    };

    public static RegistryTweak? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    public static string ReadScript() => "$ErrorActionPreference = 'SilentlyContinue'\n$r = @()\n" +
        string.Concat(All.Select(t => $"$v = (Get-ItemProperty -LiteralPath '{t.Key}' -Name '{t.Name}' -ErrorAction SilentlyContinue).'{t.Name}'\n$r += [ordered]@{{ id = '{t.Id}'; value = $v }}\n")) +
        "@($r) | ConvertTo-Json -Compress -Depth 3\n";

    public enum State { On, Off, Default }

    /// <summary>Đọc kết quả script đọc: id → trạng thái. Id lạ bị bỏ qua; giá trị khác Bật/Tắt hoặc không có thì là "Mặc định".</summary>
    public static IReadOnlyDictionary<string, State> ParseStates(string json)
    {
        var res = All.ToDictionary(t => t.Id, _ => State.Default);
        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList()
                : doc.RootElement.ValueKind == JsonValueKind.Object ? new List<JsonElement> { doc.RootElement } : throw new FormatException();
            foreach (var e in items)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var idp) || idp.ValueKind != JsonValueKind.String) continue;
                var t = Find(idp.GetString() ?? "");
                if (t == null) continue;
                if (e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
                    res[t.Id] = n == t.OnValue ? State.On : n == t.OffValue ? State.Off : State.Default;
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { throw new FormatException("Không đọc được trạng thái thiết lập."); }
        return res;
    }

    public static async Task<IReadOnlyDictionary<string, State>> ReadAsync(ScriptRunner? run, CancellationToken ct)
    {
        run ??= (s, c) => PowerShellRunner.RunAsync(s, c, 60);
        var r = await run(ReadScript(), ct);
        if (r.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.Error) ? "Không đọc được thiết lập." : r.Error.Trim());
        return ParseStates(r.Output);
    }
}

/// <summary>Bật/tắt một thiết lập trong danh sách cố định. Hoàn tác được bằng nút ngược lại.</summary>
public sealed class ApplyTweakFix : IFix
{
    private readonly RegistryTweak _t;
    private readonly bool _on;
    private readonly ScriptRunner _run;

    public ApplyTweakFix(string tweakId, bool on, ScriptRunner? run = null)
    {
        _t = TweakCatalog.Find(tweakId) ?? throw new ArgumentException("Thiết lập không có trong danh sách.", nameof(tweakId));
        _on = on;
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 30));
    }

    private int Value => _on ? _t.OnValue : _t.OffValue;

    public string Title => (_on ? "Bật: " : "Tắt: ") + _t.Title;
    public IReadOnlyList<string> Actions => new[]
    {
        $"Đặt một giá trị đăng ký của tài khoản đang dùng ({_t.Name} = {Value})",
        _t.Note,
        "Không đổi gì khác, không đụng cài đặt bảo mật. Hoàn tác bằng nút ngược lại; có thể cần mở lại Explorer hoặc đăng xuất để thấy"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public string SetScript() => $$"""
$ErrorActionPreference = 'Stop'
$k = '{{_t.Key}}'
if (-not (Test-Path -LiteralPath $k)) { New-Item -Path $k -Force | Out-Null }
Set-ItemProperty -LiteralPath $k -Name '{{_t.Name}}' -Value {{Value}} -Type DWord
""";

    public string ReadScript() => $"(Get-ItemProperty -LiteralPath '{_t.Key}' -Name '{_t.Name}' -ErrorAction Stop).'{_t.Name}'";

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        var r = await _run(SetScript(), ct);
        return r.ExitCode == 0
            ? FixResult.Succeeded("Đã đổi. Nếu chưa thấy, mở lại cửa sổ Explorer hoặc đăng xuất rồi đăng nhập lại.")
            : FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không đổi được thiết lập (Windows có thể khóa mục này)." : r.Error.Trim());
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        var r = await _run(ReadScript(), ct);
        return r.ExitCode == 0 && r.Output.Trim() == Value.ToString();
    }
}

public enum PowerPlan { Balanced, HighPerformance, PowerSaver }

/// <summary>Đổi gói nguồn điện bằng powercfg với 3 GUID cố định của Windows. Gói không có trên máy (một số laptop) thì báo lỗi, không tạo gói mới.</summary>
public sealed class SetPowerPlanFix : IFix
{
    public static string Guid(PowerPlan p) => p switch
    {
        PowerPlan.Balanced => "381b4222-f694-41f0-9685-ff5bb260df2e",
        PowerPlan.HighPerformance => "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        PowerPlan.PowerSaver => "a1841308-3541-4fab-bc81-f71556f20b4a",
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };

    private static string Vn(PowerPlan p) => p switch { PowerPlan.Balanced => "Cân bằng", PowerPlan.HighPerformance => "Hiệu năng cao", _ => "Tiết kiệm pin" };

    private readonly PowerPlan _plan;
    private readonly ScriptRunner _run;

    public SetPowerPlanFix(PowerPlan plan, ScriptRunner? run = null)
    {
        _plan = plan;
        _run = run ?? ((s, ct) => PowerShellRunner.RunAsync(s, ct, 30));
    }

    public string Title => "Đổi gói nguồn điện: " + Vn(_plan);
    public IReadOnlyList<string> Actions => new[]
    {
        $"Chuyển gói nguồn điện đang dùng sang \"{Vn(_plan)}\" bằng powercfg",
        "Chỉ chọn gói có sẵn trong Windows; không tạo hay xóa gói nào. Hiệu năng cao tốn pin và nóng hơn",
        "Hoàn tác bằng cách chọn lại gói Cân bằng"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => true;

    public string SetScript() => $"$ErrorActionPreference = 'Stop'\npowercfg /setactive {Guid(_plan)}\nif ($LASTEXITCODE -ne 0) {{ [Console]::Error.WriteLine('Máy này không có gói nguồn điện đó.'); exit 1 }}";
    public const string ReadScript = "powercfg /getactivescheme";

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        var r = await _run(SetScript(), ct);
        return r.ExitCode == 0 ? FixResult.Succeeded("Đã đổi gói nguồn điện.")
            : FixResult.Failed(string.IsNullOrWhiteSpace(r.Error) ? "Không đổi được gói nguồn điện." : r.Error.Trim());
    }

    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        var r = await _run(ReadScript, ct);
        return r.ExitCode == 0 && r.Output.Contains(Guid(_plan), StringComparison.OrdinalIgnoreCase);
    }
}
