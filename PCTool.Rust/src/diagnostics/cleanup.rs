use std::fs;
use std::io;
use std::os::windows::fs::MetadataExt;
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime};

const REPARSE_POINT: u32 = 0x400;
const MINIMUM_AGE: Duration = Duration::from_secs(7 * 24 * 60 * 60);

fn temp_root() -> io::Result<PathBuf> {
    let root = std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .filter(|path| path.is_absolute())
        .ok_or_else(|| io::Error::other("Không tìm thấy đường dẫn LocalAppData."))?
        .join("Temp");
    for path in root.ancestors() {
        let metadata = fs::symlink_metadata(path)?;
        if !metadata.is_dir() || metadata.file_attributes() & REPARSE_POINT != 0 {
            return Err(io::Error::other(
                "Đường dẫn thư mục tạm có liên kết chuyển hướng.",
            ));
        }
    }
    Ok(root)
}

pub fn report(delete: bool) -> String {
    let result = temp_root().and_then(|root| {
        let (files, bytes, skipped) = scan(&root, SystemTime::now(), delete)?;
        let verb = if delete { "Đã xóa" } else { "Có thể xóa" };
        Ok(format!(
            "{verb} {files} tệp ({:.2} MiB).\nĐường dẫn: {}\nChỉ xét tệp cấp đầu tiên đã qua ít nhất 7 ngày kể từ lần sửa đổi.\nMục không xử lý được: {skipped}.",
            bytes as f64 / 1_048_576.0,
            root.display(),
        ))
    });
    result.unwrap_or_else(|error| format!("Không xử lý được thư mục tạm: {error}"))
}

fn scan(root: &Path, now: SystemTime, delete: bool) -> io::Result<(usize, u64, usize)> {
    let mut files = 0;
    let mut bytes = 0u64;
    let mut skipped = 0;
    for entry in fs::read_dir(root)? {
        let Ok(entry) = entry else {
            skipped += 1;
            continue;
        };
        let path = entry.path();
        let Ok(metadata) = fs::symlink_metadata(&path) else {
            skipped += 1;
            continue;
        };
        if !metadata.is_file() || metadata.file_attributes() & REPARSE_POINT != 0 {
            continue;
        }
        let Ok(modified) = metadata.modified() else {
            skipped += 1;
            continue;
        };
        if !now
            .duration_since(modified)
            .is_ok_and(|age| age >= MINIMUM_AGE)
        {
            continue;
        }
        if delete && fs::remove_file(&path).is_err() {
            skipped += 1;
            continue;
        }
        files += 1;
        bytes = bytes.saturating_add(metadata.len());
    }
    Ok((files, bytes, skipped))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn preview_and_delete_only_eligible_top_level_files() -> io::Result<()> {
        let root = std::env::temp_dir().join(format!("pctool-cleanup-test-{}", std::process::id()));
        fs::create_dir(&root)?;
        let result = (|| {
            fs::create_dir(root.join("nested"))?;
            fs::write(root.join("old.tmp"), b"123")?;
            fs::write(root.join("nested").join("keep.tmp"), b"456")?;
            let now = SystemTime::now();
            assert_eq!(scan(&root, now, true)?, (0, 0, 0));
            let future = now + MINIMUM_AGE + Duration::from_secs(60);
            assert_eq!(scan(&root, future, false)?, (1, 3, 0));
            assert!(root.join("old.tmp").exists());
            assert_eq!(scan(&root, future, true)?, (1, 3, 0));
            assert!(!root.join("old.tmp").exists());
            assert!(root.join("nested").join("keep.tmp").exists());
            Ok(())
        })();
        fs::remove_dir_all(&root)?;
        result
    }
}
