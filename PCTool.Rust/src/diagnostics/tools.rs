use std::ffi::{OsStr, OsString};
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;

fn execute(program: &OsStr, args: &[OsString]) -> String {
    match Command::new(program)
        .args(args)
        .creation_flags(0x08000000)
        .output()
    {
        Ok(output) => {
            let stdout = String::from_utf8_lossy(&output.stdout);
            let stderr = String::from_utf8_lossy(&output.stderr);
            let status = if output.status.success() {
                "Đã hoàn thành.".to_owned()
            } else {
                format!("Lỗi từ công cụ: {}", output.status)
            };
            format!("{status}\n{stdout}\n{stderr}")
        }
        Err(error) => format!("Không mở được {}: {error}", program.to_string_lossy()),
    }
}

pub(super) fn invoke(program: &str, args: &[&str]) -> String {
    execute(
        OsStr::new(program),
        &args.iter().map(OsString::from).collect::<Vec<_>>(),
    )
}

pub(super) fn native_tool(program: &str, args: &[&str]) -> String {
    let Some(root) = std::env::var_os("SystemRoot") else {
        return "Không tìm thấy thư mục Windows.".into();
    };
    execute(
        &PathBuf::from(root)
            .join("System32")
            .join(program)
            .into_os_string(),
        &args.iter().map(OsString::from).collect::<Vec<_>>(),
    )
}

fn path_with_extension(value: &str, extension: &str) -> bool {
    let path = Path::new(value);
    path.is_absolute()
        && path
            .extension()
            .and_then(OsStr::to_str)
            .is_some_and(|actual| actual.eq_ignore_ascii_case(extension))
}

fn input_file(value: &str) -> bool {
    Path::new(value).is_absolute() && Path::new(value).is_file()
}

fn new_output(value: &str) -> bool {
    let path = Path::new(value);
    path.is_absolute()
        && path.file_name().is_some()
        && path
            .symlink_metadata()
            .is_err_and(|error| error.kind() == std::io::ErrorKind::NotFound)
        && path.parent().is_some_and(Path::is_dir)
}

fn valid_package_id(id: &str) -> bool {
    id.len() <= 128
        && id.contains('.')
        && id.split('.').all(|part| {
            part.as_bytes()
                .first()
                .is_some_and(u8::is_ascii_alphanumeric)
                && part
                    .bytes()
                    .all(|c| c.is_ascii_alphanumeric() || c == b'-' || c == b'_')
        })
}

fn valid_time(time: &str) -> bool {
    !time.is_empty()
        && time.bytes().all(|c| c == b'.' || c.is_ascii_digit())
        && time
            .parse::<f64>()
            .is_ok_and(|seconds| seconds.is_finite() && seconds >= 0.0)
}

fn input_lines(input: &str) -> Vec<&str> {
    input
        .lines()
        .map(str::trim)
        .filter(|line| !line.is_empty())
        .collect()
}

#[derive(Clone, Copy)]
pub enum Tool {
    Install,
    ExportApps,
    ImportApps,
    ExportDrivers,
    RestoreDrivers,
    MergePdf,
    SplitPdf,
    OpenPdf,
    ReadImage,
    CutVideo,
    BackupData,
    RestoreData,
    UninstallMsi,
    ExportWifi,
    ImportWifi,
    MergeVideo,
    BackupZalo,
    RestoreZalo,
    RotatePdf,
    ExportPdfImages,
    RecognizePdf,
}

impl Tool {
    pub fn label(self) -> &'static str {
        match self {
            Self::Install => "Cài bằng WinGet",
            Self::ExportApps => "Xuất danh sách ứng dụng",
            Self::ImportApps => "Cài lại từ danh sách",
            Self::ExportDrivers => "Xuất driver",
            Self::RestoreDrivers => "Nạp driver",
            Self::MergePdf => "Gộp PDF",
            Self::SplitPdf => "Tách PDF",
            Self::OpenPdf => "Mở PDF",
            Self::ReadImage => "Nhận dạng chữ",
            Self::CutVideo => "Cắt video",
            Self::BackupData => "Sao lưu thư mục",
            Self::RestoreData => "Khôi phục thư mục",
            Self::UninstallMsi => "Mở trình gỡ MSI",
            Self::ExportWifi => "Xuất hồ sơ Wi-Fi",
            Self::ImportWifi => "Nhập hồ sơ Wi-Fi",
            Self::MergeVideo => "Ghép MP4",
            Self::BackupZalo => "Sao lưu Zalo",
            Self::RestoreZalo => "Khôi phục Zalo",
            Self::RotatePdf => "Xoay trang",
            Self::ExportPdfImages => "Xuất ảnh PNG",
            Self::RecognizePdf => "OCR PDF ra văn bản",
        }
    }

    pub fn instructions(self) -> &'static str {
        match self {
            Self::ExportPdfImages | Self::RecognizePdf => {
                "Nhập ba dòng: PDF nguồn, đường dẫn thư mục mới (xuất ảnh) hoặc tệp TXT mới (OCR), và số trang hoặc khoảng trang (ví dụ 2-5)."
            }
            Self::BackupZalo | Self::RestoreZalo => {
                "Thoát hẳn Zalo. Nhập thư mục tuyệt đối: thư mục trống khi sao lưu hoặc thư mục bản sao khi khôi phục. Bỏ qua tài khoản đã có."
            }
            Self::RotatePdf => {
                "Nhập mỗi mục một dòng: PDF nguồn, PDF đích mới, góc (90, 180 hoặc 270), và tùy chọn phạm vi trang (ví dụ 1,3-5). Bỏ dòng cuối để xoay tất cả."
            }
            Self::ExportWifi | Self::ImportWifi => {
                "Xuất: nhập đường dẫn thư mục tuyệt đối đang trống. Nhập: dán đường dẫn tệp XML đã xuất. Mật khẩu có thể cần nhập lại khi chuyển máy."
            }
            Self::MergeVideo => {
                "Nhập mỗi video MP4 nguồn một dòng theo thứ tự cần ghép. Dòng cuối là đường dẫn MP4 đích mới. Các video cần cùng cấu hình luồng."
            }
            Self::UninstallMsi => {
                "Sao chép mã MSI trong mục Ứng dụng đã cài rồi dán vào đây. Windows Installer sẽ mở trình gỡ; chức năng này cần xác nhận."
            }
            Self::Install => {
                "Chọn ứng dụng trong danh sách. Có thể nhập thêm ID WinGet, mỗi ID một dòng. Facebook tạo lối tắt trên Desktop."
            }
            Self::ExportApps => {
                "Nhập đường dẫn tuyệt đối tới tệp .json mới (xuất) hoặc đã có (nhập)."
            }
            Self::ImportApps => Self::ExportApps.instructions(),
            Self::ExportDrivers => {
                "Nhập đường dẫn tuyệt đối của thư mục driver. Khi xuất, thư mục phải tồn tại và trống."
            }
            Self::RestoreDrivers => Self::ExportDrivers.instructions(),
            Self::MergePdf => {
                "Gộp: mỗi PDF nguồn một dòng, dòng cuối là PDF đích mới. Tách: PDF nguồn và mẫu đích mới có %d, mỗi mục một dòng."
            }
            Self::SplitPdf => Self::MergePdf.instructions(),
            Self::OpenPdf => "Nhập đường dẫn tuyệt đối của tệp PDF để mở bằng ứng dụng mặc định.",
            Self::ReadImage => {
                "Nhập đường dẫn ảnh PNG/JPEG/TIFF/BMP. OCR dùng Tesseract với dữ liệu tiếng Việt và tiếng Anh."
            }
            Self::BackupData | Self::RestoreData => {
                "Nhập hai đường dẫn tuyệt đối, mỗi đường dẫn một dòng: thư mục nguồn rồi thư mục đích mới."
            }
            Self::CutVideo => {
                "Nhập 4 dòng: tệp nguồn, tệp đích mới, giây bắt đầu, số giây cần cắt. Máy cần có FFmpeg."
            }
        }
    }

    pub fn run(self, input: &str) -> String {
        let lines = input_lines(input);
        let invalid = || format!("Dữ liệu nhập chưa hợp lệ.\n{}", self.instructions());
        match (self, lines.as_slice()) {
            (Self::Install, ids)
                if !ids.is_empty()
                    && ids
                        .iter()
                        .all(|id| *id == "facebook" || valid_package_id(id)) =>
            {
                ids.iter()
                    .map(|id| {
                        let result = if *id == "facebook" {
                            super::app_catalog::facebook_shortcut()
                        } else {
                            invoke(
                                "winget.exe",
                                &[
                                    "install",
                                    "--id",
                                    id,
                                    "--exact",
                                    "--source",
                                    "winget",
                                    "--disable-interactivity",
                                    "--accept-source-agreements",
                                    "--accept-package-agreements",
                                ],
                            )
                        };
                        format!("{id}\n{result}")
                    })
                    .collect::<Vec<_>>()
                    .join("\n")
            }
            (Self::ExportApps, [file]) if path_with_extension(file, "json") && new_output(file) => {
                invoke(
                    "winget.exe",
                    &[
                        "export",
                        "--output",
                        file,
                        "--accept-source-agreements",
                        "--disable-interactivity",
                    ],
                )
            }
            (Self::ImportApps, [file]) if path_with_extension(file, "json") && input_file(file) => {
                invoke(
                    "winget.exe",
                    &[
                        "import",
                        "--import-file",
                        file,
                        "--accept-source-agreements",
                        "--accept-package-agreements",
                        "--disable-interactivity",
                    ],
                )
            }
            (Self::ExportDrivers, [folder])
                if Path::new(folder).is_absolute() && Path::new(folder).is_dir() =>
            {
                if !std::fs::read_dir(folder).is_ok_and(|mut entries| entries.next().is_none()) {
                    return "Thư mục xuất driver phải trống và truy cập được.".into();
                }
                native_tool("pnputil.exe", &["/export-driver", "*", folder])
            }
            (Self::RestoreDrivers, [folder])
                if Path::new(folder).is_absolute() && Path::new(folder).is_dir() =>
            {
                let pattern = Path::new(folder)
                    .join("*.inf")
                    .to_string_lossy()
                    .into_owned();
                native_tool(
                    "pnputil.exe",
                    &["/add-driver", &pattern, "/subdirs", "/install"],
                )
            }
            (Self::MergePdf, [sources @ .., output])
                if sources.len() >= 2
                    && sources
                        .iter()
                        .all(|source| path_with_extension(source, "pdf") && input_file(source))
                    && path_with_extension(output, "pdf")
                    && new_output(output) =>
            {
                let mut args = vec!["--empty", "--pages"];
                args.extend_from_slice(sources);
                args.extend(["--", output]);
                invoke("qpdf.exe", &args)
            }
            (Self::SplitPdf, [source, output])
                if path_with_extension(source, "pdf")
                    && input_file(source)
                    && path_with_extension(output, "pdf")
                    && Path::new(output)
                        .file_name()
                        .and_then(OsStr::to_str)
                        .is_some_and(|name| name.matches("%d").count() == 1)
                    && new_output(output) =>
            {
                // Reserve a fresh folder so every PDF that qpdf generates has a new path.
                let requested = Path::new(output);
                let folder = requested.with_extension("pages");
                if let Err(error) = std::fs::create_dir(&folder) {
                    return format!("Không tạo được thư mục PDF đích: {error}");
                }
                let target = folder.join(requested.file_name().unwrap());
                format!(
                    "Thư mục kết quả: {}\n{}",
                    folder.display(),
                    invoke(
                        "qpdf.exe",
                        &["--split-pages", source, &target.to_string_lossy()]
                    )
                )
            }
            (Self::OpenPdf, [file]) if path_with_extension(file, "pdf") && input_file(file) => {
                super::run_script_with_input(
                    "Invoke-Item -LiteralPath $env:PCTOOL_DOCUMENT_PATH",
                    file,
                )
            }
            (Self::ReadImage, [file])
                if input_file(file)
                    && ["png", "jpg", "jpeg", "tif", "tiff", "bmp"]
                        .iter()
                        .any(|ext| path_with_extension(file, ext)) =>
            {
                invoke("tesseract.exe", &[file, "stdout", "-l", "vie+eng"])
            }
            (Self::CutVideo, [source, output, start, duration])
                if input_file(source)
                    && new_output(output)
                    && valid_time(start)
                    && valid_time(duration)
                    && duration.parse::<f64>().is_ok_and(|value| value > 0.0) =>
            {
                invoke(
                    "ffmpeg.exe",
                    &[
                        "-nostdin", "-n", "-ss", start, "-i", source, "-t", duration, output,
                    ],
                )
            }
            (Self::BackupData | Self::RestoreData, [source, target]) => {
                super::backup::copy_folder(source, target)
            }
            (Self::UninstallMsi, [code]) => super::installed::uninstall(code),

            (Self::ExportWifi, [folder])
                if Path::new(folder).is_absolute() && Path::new(folder).is_dir() =>
            {
                if !std::fs::read_dir(folder).is_ok_and(|mut entries| entries.next().is_none()) {
                    return "Chọn thư mục trống và truy cập được để lưu hồ sơ Wi-Fi.".into();
                }
                native_tool(
                    "netsh.exe",
                    &["wlan", "export", "profile", &format!("folder={folder}")],
                )
            }
            (Self::ImportWifi, [file]) if path_with_extension(file, "xml") && input_file(file) => {
                native_tool(
                    "netsh.exe",
                    &[
                        "wlan",
                        "add",
                        "profile",
                        &format!("filename={file}"),
                        "user=current",
                    ],
                )
            }
            (Self::MergeVideo, [sources @ .., output])
                if sources.len() >= 2
                    && sources
                        .iter()
                        .all(|file| path_with_extension(file, "mp4") && input_file(file))
                    && path_with_extension(output, "mp4")
                    && new_output(output) =>
            {
                super::video::merge(sources, output)
            }

            (Self::BackupZalo | Self::RestoreZalo, [folder])
                if Path::new(folder).is_absolute() && Path::new(folder).is_dir() =>
            {
                super::zalo::run(folder, matches!(self, Self::RestoreZalo))
            }
            (Self::RotatePdf, [source, output, angle, range @ ..])
                if path_with_extension(source, "pdf")
                    && input_file(source)
                    && path_with_extension(output, "pdf")
                    && new_output(output)
                    && matches!(*angle, "90" | "180" | "270")
                    && (range.is_empty() || (range.len() == 1 && valid_pdf_range(range[0]))) =>
            {
                let mut rotation = format!("--rotate=+{angle}");
                if let [pages] = range {
                    rotation.push(':');
                    rotation.push_str(pages);
                }
                invoke("qpdf.exe", &[&rotation, source, output])
            }

            (Self::ExportPdfImages | Self::RecognizePdf, [source, target, span])
                if path_with_extension(source, "pdf")
                    && input_file(source)
                    && new_output(target) =>
            {
                let Some(pages) = super::pdf_images::page_span(span) else {
                    return invalid();
                };
                match self {
                    Self::ExportPdfImages => super::pdf_images::export(source, target, pages),
                    Self::RecognizePdf if path_with_extension(target, "txt") => {
                        super::pdf_images::recognize(source, target, pages)
                    }
                    _ => invalid(),
                }
            }
            _ => invalid(),
        }
    }
}

fn valid_pdf_range(range: &str) -> bool {
    range.split(',').all(|item| {
        let number = |value: &str| {
            value.bytes().all(|byte| byte.is_ascii_digit())
                && value.parse::<u32>().is_ok_and(|page| page > 0)
        };
        match item.split_once('-') {
            Some((start, end)) => number(start) && number(end),
            None => number(item),
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn validates_pdf_page_ranges() {
        assert!(valid_pdf_range("1,3-5"));
        for invalid in ["", "0", "1,", "1-2-3", "1 --replace-input"] {
            assert!(!valid_pdf_range(invalid));
        }
    }

    #[test]
    fn validates_package_ids_and_times() {
        assert!(valid_package_id("Google.Chrome"));
        assert!(!valid_package_id("Google.Chrome --override"));
        assert!(!valid_package_id("-Google.Chrome"));
        assert!(valid_time("12.50"));
        assert!(!valid_time("1e5"));
        assert!(!valid_time("-1"));
    }

    #[test]
    fn every_tool_rejects_missing_input_without_starting_a_process() {
        for tool in [
            Tool::Install,
            Tool::ExportApps,
            Tool::ImportApps,
            Tool::ExportDrivers,
            Tool::RestoreDrivers,
            Tool::MergePdf,
            Tool::SplitPdf,
            Tool::OpenPdf,
            Tool::ReadImage,
            Tool::CutVideo,
            Tool::BackupData,
            Tool::RestoreData,
            Tool::UninstallMsi,
            Tool::ExportWifi,
            Tool::ImportWifi,
            Tool::MergeVideo,
            Tool::BackupZalo,
            Tool::RestoreZalo,
            Tool::RotatePdf,
            Tool::ExportPdfImages,
            Tool::RecognizePdf,
        ] {
            assert!(tool.run("").starts_with("Dữ liệu nhập chưa hợp lệ"));
        }
    }
}
