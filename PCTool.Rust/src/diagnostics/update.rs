const CURRENT: &str = env!("CARGO_PKG_VERSION");

pub const DOWNLOAD: &str = "https://github.com/Zinit-mid/Tool-c-i-win-d-n-d-p-PC/blob/pctool-rust/PCTool.Rust/download/PCTool-Rust.zip";

fn version(text: &str) -> Option<[u32; 3]> {
    let mut parts = text.trim().split('.');
    let value = [
        parts.next()?.parse().ok()?,
        parts.next()?.parse().ok()?,
        parts.next()?.parse().ok()?,
    ];
    parts.next().is_none().then_some(value)
}

fn comparison(remote: &str) -> (String, bool) {
    match (version(CURRENT), version(remote)) {
        (Some(local), Some(latest)) if latest > local => (
            format!(
                "Đang dùng {CURRENT}. Có bản Rust mới {}. Mở trang tải trên GitHub?",
                remote.trim()
            ),
            true,
        ),
        (Some(_), Some(_)) => (
            format!("Bạn đang dùng PCTool Rust {CURRENT}. Không có bản mới hơn."),
            false,
        ),
        _ => (
            format!(
                "Không kiểm tra được bản cập nhật Rust.\n{}",
                remote.trim().chars().take(500).collect::<String>()
            ),
            false,
        ),
    }
}

pub fn check() -> (String, bool) {
    let remote = super::run_script(
        r#"
$feed = 'https://raw.githubusercontent.com/Zinit-mid/Tool-c-i-win-d-n-d-p-PC/pctool-rust/PCTool.Rust/download/version.txt'
(Invoke-WebRequest -Uri $feed -UseBasicParsing -TimeoutSec 18).Content
"#,
    );
    comparison(&remote)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn checks_numeric_versions_and_rejects_invalid_feeds() {
        assert!(comparison("99.10.0\n").1);
        assert!(!comparison(CURRENT).1);
        assert!(!comparison("0.0.1").1);
        assert!(comparison("<html>").0.contains("Không kiểm tra được"));
        assert!(version("1.2.3.4").is_none());
    }
}
