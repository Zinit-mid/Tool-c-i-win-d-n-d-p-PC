use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{SystemTime, UNIX_EPOCH};

pub fn page_span(spec: &str) -> Option<(u32, u32)> {
    let page = |text: &str| {
        if !text.bytes().all(|byte| byte.is_ascii_digit()) {
            return None;
        }
        text.parse::<u32>()
            .ok()
            .filter(|number| *number > 0 && *number <= i32::MAX as u32)
    };
    let (first, last) = spec.split_once('-').unwrap_or((spec, spec));
    let (first, last) = (page(first)?, page(last)?);
    (first <= last).then_some((first, last))
}

fn run(program: &str, args: &[&str]) -> io::Result<Vec<u8>> {
    let output = Command::new(program)
        .args(args)
        .creation_flags(0x08000000)
        .output()
        .map_err(|error| io::Error::other(format!("Không mở được {program}: {error}")))?;
    if !output.status.success() {
        return Err(io::Error::other(format!(
            "{program} báo lỗi ({}): {}",
            output.status,
            String::from_utf8_lossy(&output.stderr)
        )));
    }
    Ok(output.stdout)
}

fn page_number(path: &Path) -> Option<u32> {
    if path.extension()?.to_str()? != "png" {
        return None;
    }
    let suffix = path.file_stem()?.to_str()?.strip_prefix("trang-")?;
    suffix.parse().ok()
}

fn render(pdf: &str, folder: &Path, pages: (u32, u32)) -> io::Result<Vec<(u32, PathBuf)>> {
    let (first, last) = pages;
    let prefix = folder.join("trang");
    run(
        "pdftoppm.exe",
        &[
            "-png",
            "-r",
            "300",
            "-f",
            &first.to_string(),
            "-l",
            &last.to_string(),
            pdf,
            &prefix.to_string_lossy(),
        ],
    )?;
    let mut images = Vec::new();
    for item in fs::read_dir(folder)? {
        let path = item?.path();
        if let Some(number) = page_number(&path) {
            images.push((number, path));
        }
    }
    images.sort_unstable_by_key(|(number, _)| *number);
    if images.is_empty() {
        return Err(io::Error::other(
            "Không xuất được trang nào. Hãy kiểm tra phạm vi trang.",
        ));
    }
    Ok(images)
}

pub fn export(pdf: &str, folder: &str, pages: (u32, u32)) -> String {
    let result = (|| {
        fs::create_dir(folder)?;
        let images = render(pdf, Path::new(folder), pages)?;
        Ok::<_, io::Error>(format!(
            "Đã xuất {} ảnh PNG (300 DPI) vào {folder}.",
            images.len()
        ))
    })();
    result.unwrap_or_else(|error| format!("Không hoàn thành xuất ảnh: {error}"))
}

struct TemporaryPages(PathBuf);

impl TemporaryPages {
    fn create() -> io::Result<Self> {
        let timestamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(io::Error::other)?;
        let folder = std::env::temp_dir().join(format!(
            "pctool-pdf-{}-{}",
            std::process::id(),
            timestamp.as_nanos()
        ));
        fs::create_dir(&folder)?;
        Ok(Self(folder))
    }
}

impl Drop for TemporaryPages {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

pub fn recognize(pdf: &str, target: &str, pages: (u32, u32)) -> String {
    let result = (|| {
        run("tesseract.exe", &["--version"])?;
        let temporary = TemporaryPages::create()?;
        let images = render(pdf, &temporary.0, pages)?;
        let mut document = Vec::new();
        for (number, path) in &images {
            let text = run(
                "tesseract.exe",
                &[&path.to_string_lossy(), "stdout", "-l", "vie+eng"],
            )?;
            let text = String::from_utf8_lossy(&text);
            writeln!(document, "=== Trang {number} ===\n{}\n", text.trim())?;
        }
        OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(target)?
            .write_all(&document)?;
        Ok::<_, io::Error>(format!(
            "Đã nhận dạng {} trang và lưu văn bản UTF-8 tại {target}.",
            images.len()
        ))
    })();
    result.unwrap_or_else(|error| format!("Không hoàn thành OCR PDF: {error}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_page_spans_and_image_page_numbers() {
        assert_eq!(page_span("2"), Some((2, 2)));
        assert_eq!(page_span("2-15"), Some((2, 15)));
        for invalid in ["0", "2-1", "-1", "1,2", "1--2", "2147483648"] {
            assert_eq!(page_span(invalid), None);
        }
        assert_eq!(page_number(Path::new("trang-010.png")), Some(10));
        assert_eq!(page_number(Path::new("other-001.png")), None);
    }
}
