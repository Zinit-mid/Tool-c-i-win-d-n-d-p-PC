use std::ffi::c_void;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use super::backup::{copy_tree, ordinary_directory};

#[repr(C)]
struct ProcessEntry {
    size: u32,
    usage: u32,
    process_id: u32,
    heap: usize,
    module: u32,
    threads: u32,
    parent: u32,
    priority: i32,
    flags: u32,
    executable: [u16; 260],
}

#[link(name = "kernel32")]
unsafe extern "system" {
    fn CreateToolhelp32Snapshot(flags: u32, process_id: u32) -> *mut c_void;
    fn Process32FirstW(snapshot: *mut c_void, entry: *mut ProcessEntry) -> i32;
    fn Process32NextW(snapshot: *mut c_void, entry: *mut ProcessEntry) -> i32;
    fn CloseHandle(handle: *mut c_void) -> i32;
}

fn running() -> io::Result<bool> {
    let snapshot = unsafe { CreateToolhelp32Snapshot(2, 0) };
    if snapshot == (-1isize) as *mut c_void {
        return Err(io::Error::last_os_error());
    }
    let mut entry: ProcessEntry = unsafe { std::mem::zeroed() };
    entry.size = size_of::<ProcessEntry>() as u32;
    let mut available = unsafe { Process32FirstW(snapshot, &mut entry) };
    let result = loop {
        if available == 0 {
            let error = io::Error::last_os_error();
            break if error.raw_os_error() == Some(18) {
                Ok(false)
            } else {
                Err(error)
            };
        }
        let end = entry
            .executable
            .iter()
            .position(|&unit| unit == 0)
            .unwrap_or(entry.executable.len());
        let name = String::from_utf16_lossy(&entry.executable[..end]);
        if ["Zalo.exe", "ZaloPC.exe"]
            .iter()
            .any(|candidate| name.eq_ignore_ascii_case(candidate))
        {
            break Ok(true);
        }
        available = unsafe { Process32NextW(snapshot, &mut entry) };
    };
    unsafe { CloseHandle(snapshot) };
    result
}

fn profile_root() -> io::Result<PathBuf> {
    let local = std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .filter(|path| path.is_absolute())
        .ok_or_else(|| io::Error::other("Không tìm thấy LocalAppData."))?;
    let roots: Vec<_> = ["ZaloPC", "Zalo PC"]
        .iter()
        .map(|name| local.join(name))
        .filter(|path| path.ancestors().all(ordinary_directory))
        .collect();
    match roots.as_slice() {
        [root] => Ok(root.clone()),
        [] => Err(io::Error::other(
            "Không tìm thấy thư mục Zalo PC. Hãy cài Zalo, đăng nhập một lần rồi thoát hẳn.",
        )),
        _ => Err(io::Error::other(
            "Có hai thư mục dữ liệu Zalo; hãy dùng mục sao lưu thư mục và chọn nguồn cụ thể.",
        )),
    }
}

fn transfer(source: &Path, target: &Path) -> io::Result<String> {
    if ![source, target]
        .iter()
        .all(|path| path.is_absolute() && path.ancestors().all(ordinary_directory))
    {
        return Err(io::Error::other(
            "Hai thư mục phải tồn tại và dùng đường dẫn tuyệt đối không qua liên kết.",
        ));
    }
    let source = fs::canonicalize(source)?;
    let target = fs::canonicalize(target)?;
    let src_key = source.to_string_lossy().to_lowercase();
    let dst_key = target.to_string_lossy().to_lowercase();
    if Path::new(&dst_key).starts_with(Path::new(&src_key))
        || Path::new(&src_key).starts_with(Path::new(&dst_key))
    {
        return Err(io::Error::other("Thư mục nguồn và đích phải tách biệt."));
    }
    let (mut found, mut copied, mut existing, mut skipped) = (0, 0, 0, 0);
    let mut errors = Vec::new();
    for entry in fs::read_dir(&source)? {
        let entry = entry?;
        let name = entry.file_name();
        let text = name.to_string_lossy();
        if text.is_empty()
            || !text.bytes().all(|byte| byte.is_ascii_digit())
            || !ordinary_directory(&entry.path())
        {
            continue;
        }
        found += 1;
        match copy_tree(&entry.path(), &target.join(&name)) {
            Ok((files, links)) => {
                copied += files;
                skipped += links;
            }
            Err(error) if error.kind() == io::ErrorKind::AlreadyExists => existing += 1,
            Err(error) => errors.push(format!("{text}: {error}")),
        }
    }
    if found == 0 {
        return Err(io::Error::other(
            "Không tìm thấy thư mục tài khoản Zalo có tên dạng số.",
        ));
    }
    let mut result = format!(
        "Đã chép {copied} tệp, bỏ qua {existing} thư mục tài khoản đã có và {skipped} liên kết."
    );
    if !errors.is_empty() {
        result.push_str(&format!(
            "\nCó lỗi khi sao chép, bản chép có thể chưa đầy đủ:\n{}",
            errors.join("\n")
        ));
    }
    Ok(result)
}

pub fn run(folder: &str, restore: bool) -> String {
    let result = (|| {
        if running()? {
            return Err(io::Error::other(
                "Hãy thoát hẳn Zalo, kể cả biểu tượng ở khay hệ thống.",
            ));
        }
        let profile = profile_root()?;
        let folder = Path::new(folder);
        if restore {
            transfer(folder, &profile)
        } else {
            if fs::read_dir(folder)?.next().is_some() {
                return Err(io::Error::other(
                    "Chọn một thư mục đích trống dành riêng để sao lưu Zalo.",
                ));
            }
            transfer(&profile, folder)
        }
    })();
    result.unwrap_or_else(|error| format!("Không xử lý được dữ liệu Zalo: {error}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn copies_only_numeric_accounts_and_leaves_existing_accounts_intact() -> io::Result<()> {
        let root = std::env::temp_dir().join(format!("pctool-zalo-test-{}", std::process::id()));
        fs::create_dir(&root)?;
        let result = (|| {
            let source = root.join("source");
            let target = root.join("target");
            for path in [
                source.join("123"),
                source.join("456"),
                source.join("cache"),
                target.join("456"),
            ] {
                fs::create_dir_all(path)?;
            }
            fs::write(source.join("123/data"), b"sample")?;
            fs::write(source.join("456/data"), b"new")?;
            fs::write(target.join("456/data"), b"existing")?;
            assert!(transfer(&source, &target)?.contains("Đã chép 1 tệp"));
            assert_eq!(fs::read(target.join("123/data"))?, b"sample");
            assert_eq!(fs::read(target.join("456/data"))?, b"existing");
            assert!(!target.join("cache").exists());
            assert!(transfer(&source, &source).is_err());
            Ok(())
        })();
        fs::remove_dir_all(root)?;
        result
    }
}
