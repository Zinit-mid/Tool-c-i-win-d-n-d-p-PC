pub const CATALOG: &[(&str, &str, &str)] = &[
    ("Zalo", "VNGCorp.Zalo", "Liên lạc"),
    ("Google Chrome", "Google.Chrome", "Trình duyệt"),
    ("Facebook", "facebook", "Liên lạc"),
    ("UniKey", "UniKey.UniKey", "Tiện ích"),
    ("7-Zip", "7zip.7zip", "Tiện ích"),
    ("WinRAR", "RARLab.WinRAR", "Tiện ích"),
    (
        "HiBit Uninstaller",
        "HiBitSoftware.HiBitUninstaller",
        "Tiện ích",
    ),
    ("Google Drive", "Google.GoogleDrive", "Lưu trữ"),
    ("OneDrive", "Microsoft.OneDrive", "Lưu trữ"),
    ("Firefox", "Mozilla.Firefox", "Trình duyệt"),
    ("Cốc Cốc", "CocCoc.CocCoc", "Trình duyệt"),
    ("Brave", "Brave.Brave", "Trình duyệt"),
    (
        "K-Lite Codec Pack",
        "CodecGuide.K-LiteCodecPack.Standard",
        "Xem phim",
    ),
    ("VLC", "VideoLAN.VLC", "Xem phim"),
    (
        "Videomass (cắt/ghép video)",
        "GianlucaPernigotto.Videomass",
        "Cắt ghép video",
    ),
    ("Foxit Reader", "Foxit.FoxitReader", "Đọc PDF"),
    ("SumatraPDF", "SumatraPDF.SumatraPDF", "Đọc PDF"),
    ("UltraViewer", "DucFabulous.UltraViewer", "Điều khiển từ xa"),
    ("TeamViewer", "TeamViewer.TeamViewer", "Điều khiển từ xa"),
    ("AnyDesk", "AnyDesk.AnyDesk", "Điều khiển từ xa"),
    ("Telegram", "Telegram.TelegramDesktop", "Liên lạc"),
    ("Discord", "Discord.Discord", "Liên lạc"),
    ("Zoom", "Zoom.Zoom", "Liên lạc"),
    ("Viber", "Rakuten.Viber", "Liên lạc"),
    ("Microsoft Teams", "Microsoft.Teams", "Liên lạc"),
];

pub fn facebook_shortcut() -> String {
    super::run_script(
        r#"
$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
if ([string]::IsNullOrWhiteSpace($desktop)) { throw 'Không tìm được thư mục Desktop.' }
$path = Join-Path $desktop 'Facebook.url'
if (Test-Path -LiteralPath $path) { 'Lối tắt Facebook.url đã có trên Desktop.'; return }
$stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew)
try {
    $bytes = [Text.Encoding]::ASCII.GetBytes("[InternetShortcut]`r`nURL=https://www.facebook.com/`r`n")
    $stream.Write($bytes, 0, $bytes.Length)
} finally { $stream.Dispose() }
'Đã tạo lối tắt Facebook trên Desktop.'
"#,
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn preserves_all_catalog_items_without_duplicate_ids() {
        let ids: std::collections::HashSet<_> = CATALOG.iter().map(|entry| entry.1).collect();
        assert_eq!(CATALOG.len(), 25);
        assert_eq!(ids.len(), CATALOG.len());
        assert!(ids.contains("facebook"));
    }
}
