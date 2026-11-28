use std::fs;
use std::io;
use std::path::{Component, Path, PathBuf, Prefix};
use std::time::{SystemTime, UNIX_EPOCH};

use super::tools::invoke;

fn concat_path(path: &str) -> Option<String> {
    let file = Path::new(path);
    let is_local_drive = matches!(
        file.components().next(),
        Some(Component::Prefix(prefix)) if matches!(prefix.kind(), Prefix::Disk(_))
    );
    if !is_local_drive || !file.is_absolute() || path.contains(['\r', '\n', '\0']) {
        return None;
    }
    let escaped = path.replace('\\', "/").replace('\'', "'\\''");
    Some(format!("file '{escaped}'\n"))
}

struct Manifest {
    folder: PathBuf,
}

impl Manifest {
    fn create() -> io::Result<Self> {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(io::Error::other)?;
        let folder = std::env::temp_dir().join(format!(
            "pctool-concat-{}-{}",
            std::process::id(),
            now.as_nanos()
        ));
        fs::create_dir(&folder)?;
        Ok(Self { folder })
    }

    fn path(&self) -> PathBuf {
        self.folder.join("inputs.ffconcat")
    }
}

impl Drop for Manifest {
    fn drop(&mut self) {
        let _ = fs::remove_file(self.path());
        let _ = fs::remove_dir(&self.folder);
    }
}

pub fn merge(sources: &[&str], output: &str) -> String {
    let Some(entries) = sources
        .iter()
        .map(|source| concat_path(source))
        .collect::<Option<Vec<_>>>()
    else {
        return "Chỉ hỗ trợ đường dẫn tuyệt đối trên ổ đĩa Windows cho video nguồn.".into();
    };
    let manifest = match Manifest::create() {
        Ok(manifest) => manifest,
        Err(error) => return format!("Không tạo được danh sách video tạm: {error}"),
    };
    let path = manifest.path();
    let content = format!("ffconcat version 1.0\n{}", entries.concat());
    if let Err(error) = fs::write(&path, content) {
        return format!("Không ghi được danh sách video tạm: {error}");
    }
    invoke(
        "ffmpeg.exe",
        &[
            "-nostdin",
            "-n",
            "-protocol_whitelist",
            "file",
            "-f",
            "concat",
            "-safe",
            "0",
            "-i",
            &path.to_string_lossy(),
            "-map",
            "0",
            "-c",
            "copy",
            output,
        ],
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn escapes_apostrophes_and_limits_sources_to_local_paths() {
        assert_eq!(
            concat_path(r"C:\sample\clip's.mp4").as_deref(),
            Some("file 'C:/sample/clip'\\''s.mp4'\n")
        );
        for invalid in [
            r"\\server\share\source.mp4",
            r"C:relative.mp4",
            "https://example.com/a.mp4",
            "C:\\x\nfile a.mp4",
        ] {
            assert!(concat_path(invalid).is_none());
        }
    }
}
