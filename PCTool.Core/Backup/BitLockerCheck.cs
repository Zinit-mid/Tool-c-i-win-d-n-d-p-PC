using System.Text.Json;
using PCTool.Core.Framework;
using PCTool.Core.Optimize;
using PCTool.Core.Windows;

namespace PCTool.Core.Backup;

/// <summary>Chỉ ĐỌC tình trạng BitLocker để người dùng không mất dữ liệu khi cài lại Windows. Không tắt/mở khóa gì cả.</summary>
public sealed class BitLockerCheck : ScriptCheck
{
    public const string ScriptText = """
$ErrorActionPreference = 'SilentlyContinue'
if (-not (Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue)) { '{"available":false,"volumes":[]}'; return }
$v = @(Get-BitLockerVolume | Select-Object @{n='Mount';e={"$($_.MountPoint)"}},@{n='Status';e={"$($_.VolumeStatus)"}},@{n='Protection';e={"$($_.ProtectionStatus)"}},@{n='Percent';e={[double]$_.EncryptionPercentage}},@{n='Lock';e={"$($_.LockStatus)"}})
[ordered]@{ available = $true; volumes = $v } | ConvertTo-Json -Depth 3 -Compress
""";

    public BitLockerCheck(ScriptRunner? run = null) : base(run) { }

    public override string Id => "backup.bitlocker";
    public override string Name => "Kiểm tra BitLocker";
    protected override string Script => ScriptText;
    protected override IReadOnlyList<Finding> Analyze(JsonElement data) => Evaluate(data);

    public static IReadOnlyList<Finding> Evaluate(JsonElement data)
    {
        if (Js.Bool(data, "available") == false)
            return new[]
            {
                new Finding
                {
                    Id = "bitlocker.unavailable", Severity = Severity.Info,
                    Title = "Không đọc được tình trạng BitLocker",
                    Cause = "Máy không có công cụ BitLocker (thường gặp ở Windows Home) hoặc app không đọc được. " +
                            "Nếu máy có \"Mã hóa thiết bị\", hãy kiểm tra trong Cài đặt trước khi cài lại Windows."
                }
            };

        var list = new List<Finding>();
        foreach (var v in Js.Items(data, "volumes"))
        {
            string mount = Js.Str(v, "Mount"), status = Js.Str(v, "Status"), prot = Js.Str(v, "Protection"), lck = Js.Str(v, "Lock");
            bool decrypted = status.Equals("FullyDecrypted", StringComparison.OrdinalIgnoreCase);
            bool protectionOn = prot.Equals("On", StringComparison.OrdinalIgnoreCase) || prot == "1";
            if (decrypted && !protectionOn) continue;
            if (mount.Length == 0) mount = "(không rõ ổ)";

            bool suspended = !decrypted && !protectionOn;
            list.Add(new Finding
            {
                Id = suspended ? "bitlocker.suspended" : "bitlocker.encrypted",
                Severity = suspended ? Severity.Info : Severity.Warning,
                Title = suspended ? $"Ổ {mount} đã mã hóa, bảo vệ đang tạm tắt" : $"Ổ {mount} đang bật BitLocker",
                Cause = (suspended
                            ? "Dữ liệu trên ổ vẫn được mã hóa dù bảo vệ đang tạm tắt. "
                            : "") +
                        "Trước khi cài lại Windows, đổi phần cứng hoặc cập nhật BIOS, hãy chắc chắn bạn còn khóa khôi phục (48 chữ số, thường lưu trong tài khoản Microsoft hoặc nơi bạn đã lưu). " +
                        "Mất khóa là mất dữ liệu trên ổ này. App không tắt BitLocker thay bạn.",
                TechnicalDetail = $"Mount={mount}; Status={status}; Protection={prot}; Lock={lck}; Percent={Js.Num(v, "Percent"):0.#}"
            });
        }
        return list;
    }
}
