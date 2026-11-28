use std::fs::{self, OpenOptions};
use std::io::{self, ErrorKind};
use std::os::windows::fs::MetadataExt;
use std::path::Path;

const REPARSE_POINT: u32 = 0x400;

pub fn copy_folder(source: &str, target: &str) -> String {
    match copy_tree(Path::new(source), Path::new(target)) {
        Ok((files, skipped)) => format!(
            "Đã sao chép {files} tệp vào {target}.\nĐã bỏ qua {skipped} liên kết hoặc mục đặc biệt."
        ),
        Err(error) => format!(
            "Sao chép chưa hoàn tất: {error}\nThư mục đích có thể chứa các tệp đã chép trước khi gặp lỗi."
        ),
    }
}

pub(super) fn ordinary_directory(path: &Path) -> bool {
    fs::symlink_metadata(path)
        .is_ok_and(|metadata| metadata.is_dir() && metadata.file_attributes() & REPARSE_POINT == 0)
}

pub(super) fn copy_tree(source: &Path, target: &Path) -> io::Result<(usize, usize)> {
    if !source.is_absolute() || !target.is_absolute() || !ordinary_directory(source) {
        return Err(io::Error::new(
            ErrorKind::InvalidInput,
            "Chọn đường dẫn tuyệt đối và thư mục nguồn có thật.",
        ));
    }
    let parent = target.parent().ok_or(ErrorKind::InvalidInput)?;
    let name = target.file_name().ok_or(ErrorKind::InvalidInput)?;
    let source = fs::canonicalize(source)?;
    let parent = fs::canonicalize(parent)?;
    let source_key = source.to_string_lossy().to_lowercase();
    let parent_key = parent.to_string_lossy().to_lowercase();
    if Path::new(&parent_key).starts_with(Path::new(&source_key)) {
        return Err(io::Error::new(
            ErrorKind::InvalidInput,
            "Thư mục đích phải nằm ngoài thư mục nguồn.",
        ));
    }
    if source.parent().is_none() {
        return Err(io::Error::new(
            ErrorKind::InvalidInput,
            "Hãy chọn thư mục dữ liệu thay vì toàn bộ ổ đĩa.",
        ));
    }
    let target = parent.join(name);
    fs::create_dir(&target)?;
    let mut pending = vec![(source, target)];
    let mut files = 0;
    let mut skipped = 0;
    while let Some((source, target)) = pending.pop() {
        for entry in fs::read_dir(source)? {
            let entry = entry?;
            let source = entry.path();
            let metadata = fs::symlink_metadata(&source)?;
            let target = target.join(entry.file_name());
            if metadata.file_attributes() & REPARSE_POINT != 0 {
                skipped += 1;
            } else if metadata.is_dir() {
                fs::create_dir(&target)?;
                pending.push((source, target));
            } else if metadata.is_file() {
                let mut source = fs::File::open(source)?;
                let mut target = OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(target)?;
                io::copy(&mut source, &mut target)?;
                files += 1;
            } else {
                skipped += 1;
            }
        }
    }
    Ok((files, skipped))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn backup_copies_nested_data_and_rejects_existing_destination() -> io::Result<()> {
        let root = std::env::temp_dir().join(format!("pctool-copy-test-{}", std::process::id()));
        fs::create_dir(&root)?;
        let result = (|| {
            let source = root.join("source");
            let backup = root.join("backup");
            fs::create_dir_all(source.join("nested"))?;
            fs::write(source.join("nested").join("dữ-liệu.txt"), b"content")?;
            assert!(copy_tree(&source, &source.join("bad")).is_err());
            assert_eq!(copy_tree(&source, &backup)?, (1, 0));
            assert_eq!(
                fs::read(backup.join("nested").join("dữ-liệu.txt"))?,
                b"content"
            );
            assert_eq!(
                copy_tree(&source, &backup).unwrap_err().kind(),
                ErrorKind::AlreadyExists
            );
            Ok(())
        })();
        fs::remove_dir_all(root)?;
        result
    }
}
