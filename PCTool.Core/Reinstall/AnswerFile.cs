using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace PCTool.Core.Reinstall;

/// <summary>Trang tải ISO chính thức của Microsoft. PCTool chỉ MỞ trang bằng trình duyệt, không tự tải gì.</summary>
public static class IsoDownloadPages
{
    public const string Windows11 = "https://www.microsoft.com/software-download/windows11";
    public const string Windows10 = "https://www.microsoft.com/software-download/windows10";

    public static bool IsAllowed(string? url) => url is Windows11 or Windows10;

    public static void Open(string url, Action<string> opener)
    {
        if (!IsAllowed(url)) throw new ArgumentException("Chỉ mở trang tải chính thức của Microsoft đã định sẵn.", nameof(url));
        opener(url);
    }
}

/// <param name="Language">Mã ngôn ngữ dạng vi-VN, en-US (chọn sẵn ngôn ngữ cho bộ cài).</param>
/// <param name="EditionIndex">Số thứ tự phiên bản trong ISO; null nghĩa là để màn hình của Microsoft hỏi.</param>
/// <param name="BypassWin11Checks">Bỏ qua kiểm tra TPM/Secure Boot/RAM/ổ/CPU của Windows 11. Không chính thức.</param>
public sealed record AnswerFileOptions(string Language, int? EditionIndex = null, bool BypassWin11Checks = false);

/// <summary>Kiểu khởi động của USB: Uefi (GPT, mặc định) hoặc Legacy (MBR, cho máy BIOS cũ).</summary>
public enum UsbBootMode { Uefi, Legacy }

public sealed record UsbWriteOptions(AnswerFileOptions? Answer = null, UsbBootMode Mode = UsbBootMode.Uefi)
{
    public static readonly UsbWriteOptions Default = new();
}

/// <summary>
/// File trả lời tự động autounattend.xml TỐI THIỂU: chọn sẵn ngôn ngữ, chấp nhận điều khoản, bỏ qua màn nhập key (key để trống, không kích hoạt gì),
/// tùy chọn chọn sẵn phiên bản và bỏ qua kiểm tra phần cứng Windows 11.
/// KHÔNG BAO GIỜ có DiskConfiguration / InstallTo / WillWipeDisk: việc chọn và xóa ổ cài luôn do bạn làm ở màn hình của Microsoft.
/// </summary>
public static class AnswerFileBuilder
{
    private static readonly Regex Lang = new(@"^[a-z]{2,3}-[A-Za-z0-9]{2,8}$", RegexOptions.Compiled);

    /// <summary>Các thành phần cấm xuất hiện trong file: chúng làm bộ cài xóa/chọn ổ hoặc đặt tài khoản mà không hỏi.</summary>
    public static readonly string[] Forbidden =
    {
        "DiskConfiguration", "WillWipeDisk", "InstallTo", "InstallToAvailablePartition", "CreatePartition", "ModifyPartition",
        "Format", "ComputerName", "Password", "Credentials", "AutoLogon", "LocalAccount", "AdministratorPassword"
    };

    private static readonly string[] BypassValues = { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck", "BypassStorageCheck", "BypassCPUCheck" };

    public static IReadOnlyList<string> Validate(AnswerFileOptions o, IsoInfo iso)
    {
        var errors = new List<string>();
        if (!Lang.IsMatch(o.Language ?? "")) errors.Add("Mã ngôn ngữ không hợp lệ (ví dụ vi-VN, en-US).");
        if (o.EditionIndex is { } i && !iso.Images.Any(x => x.Index == i)) errors.Add("Số thứ tự phiên bản không có trong ISO.");
        if (o.BypassWin11Checks && !iso.Images.Any(x => x.Build >= 22000))
            errors.Add("Chỉ bỏ qua kiểm tra phần cứng được với ISO Windows 11.");
        return errors;
    }

    public static string Build(AnswerFileOptions o)
    {
        if (!Lang.IsMatch(o.Language ?? "")) throw new ArgumentException("Mã ngôn ngữ không hợp lệ.", nameof(o));
        if (o.EditionIndex is < 1 or > 99) throw new ArgumentException("Số thứ tự phiên bản không hợp lệ.", nameof(o));

        var lang = SecurityElement.Escape(o.Language);
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">""");
        sb.AppendLine("""  <settings pass="windowsPE">""");
        sb.AppendLine("""    <component name="Microsoft-Windows-International-Core-WinPE" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">""");
        sb.AppendLine($"      <SetupUILanguage><UILanguage>{lang}</UILanguage></SetupUILanguage>");
        sb.AppendLine($"      <InputLocale>{lang}</InputLocale><SystemLocale>{lang}</SystemLocale><UILanguage>{lang}</UILanguage><UserLocale>{lang}</UserLocale>");
        sb.AppendLine("    </component>");
        sb.AppendLine("""    <component name="Microsoft-Windows-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">""");
        sb.AppendLine("      <ImageInstall><OSImage>");
        sb.AppendLine("        <WillShowUI>Always</WillShowUI>");
        if (o.EditionIndex is { } idx)
            sb.AppendLine($"""        <InstallFrom><MetaData wcm:action="add"><Key>/IMAGE/INDEX</Key><Value>{idx}</Value></MetaData></InstallFrom>""");
        sb.AppendLine("      </OSImage></ImageInstall>");
        sb.AppendLine("      <UserData><AcceptEula>true</AcceptEula><ProductKey><Key></Key></ProductKey></UserData>");
        if (o.BypassWin11Checks)
        {
            sb.AppendLine("      <RunSynchronous>");
            for (int n = 0; n < BypassValues.Length; n++)
                sb.AppendLine($"""        <RunSynchronousCommand wcm:action="add"><Order>{n + 1}</Order><Path>reg add HKLM\SYSTEM\Setup\LabConfig /v {BypassValues[n]} /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand>""");
            sb.AppendLine("      </RunSynchronous>");
        }
        sb.AppendLine("    </component>");
        sb.AppendLine("  </settings>");
        sb.AppendLine("</unattend>");

        var xml = sb.ToString();
        foreach (var bad in Forbidden)
            if (xml.Contains(bad, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"File trả lời chứa thành phần bị cấm: {bad}.");
        return xml;
    }
}
