use std::ffi::c_void;
use std::fmt::Write;
use std::ptr::{null, null_mut};

type Key = *mut c_void;
const MACHINE: Key = 0x80000002u32 as i32 as isize as Key;
const USER: Key = 0x80000001u32 as i32 as isize as Key;
const UNINSTALL: &str = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
const ENUMERATE: u32 = 0x0008;
const QUERY: u32 = 0x0001;
const NO_MORE_ITEMS: i32 = 259;

#[link(name = "advapi32")]
unsafe extern "system" {
    fn RegOpenKeyExW(root: Key, name: *const u16, options: u32, access: u32, key: *mut Key) -> i32;
    fn RegCloseKey(key: Key) -> i32;
    fn RegEnumKeyExW(
        key: Key,
        index: u32,
        name: *mut u16,
        length: *mut u32,
        reserved: *mut u32,
        class: *mut u16,
        class_length: *mut u32,
        timestamp: *mut c_void,
    ) -> i32;
    fn RegGetValueW(
        key: Key,
        subkey: *const u16,
        name: *const u16,
        flags: u32,
        kind: *mut u32,
        data: *mut c_void,
        bytes: *mut u32,
    ) -> i32;
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain([0]).collect()
}

struct RegistryKey(Key);

impl Drop for RegistryKey {
    fn drop(&mut self) {
        unsafe { RegCloseKey(self.0) };
    }
}

impl RegistryKey {
    fn open(root: Key, subkey: &str, access: u32) -> Result<Self, i32> {
        let mut key = null_mut();
        let code = unsafe { RegOpenKeyExW(root, wide(subkey).as_ptr(), 0, access, &mut key) };
        if code == 0 { Ok(Self(key)) } else { Err(code) }
    }

    fn value(&self, name: &str, flag: u32, data: *mut c_void, bytes: &mut u32) -> bool {
        (unsafe {
            RegGetValueW(
                self.0,
                null(),
                wide(name).as_ptr(),
                flag,
                null_mut(),
                data,
                bytes,
            )
        }) == 0
    }

    fn text(&self, name: &str) -> String {
        let mut buffer = [0u16; 2048];
        let mut bytes = (buffer.len() * 2) as u32;
        if !self.value(name, 2, buffer.as_mut_ptr().cast(), &mut bytes) {
            return String::new();
        }
        let end = buffer
            .iter()
            .position(|unit| *unit == 0)
            .unwrap_or(buffer.len());
        String::from_utf16_lossy(&buffer[..end])
    }

    fn integer(&self, name: &str) -> u32 {
        let mut value = 0u32;
        let mut bytes = 4;
        self.value(name, 0x10, (&mut value as *mut u32).cast(), &mut bytes);
        value
    }
}

struct App {
    name: String,
    version: String,
    publisher: String,
    msi: Option<String>,
}

fn valid_product_code(code: &str) -> bool {
    let bytes = code.as_bytes();
    bytes.len() == 38
        && bytes[0] == b'{'
        && bytes[37] == b'}'
        && bytes[1..37].iter().enumerate().all(|(index, byte)| {
            if [8, 13, 18, 23].contains(&index) {
                *byte == b'-'
            } else {
                byte.is_ascii_hexdigit()
            }
        })
}

fn installed() -> (Vec<App>, usize) {
    let mut apps = Vec::new();
    let mut failures = 0;
    for (root, view) in [(MACHINE, 0x100), (MACHINE, 0x200), (USER, 0)] {
        let parent = match RegistryKey::open(root, UNINSTALL, ENUMERATE | view) {
            Ok(key) => key,
            Err(2) => continue,
            Err(_) => {
                failures += 1;
                continue;
            }
        };
        for index in 0.. {
            let mut name = [0u16; 256];
            let mut length = name.len() as u32;
            let code = unsafe {
                RegEnumKeyExW(
                    parent.0,
                    index,
                    name.as_mut_ptr(),
                    &mut length,
                    null_mut(),
                    null_mut(),
                    null_mut(),
                    null_mut(),
                )
            };
            if code == NO_MORE_ITEMS {
                break;
            }
            if code != 0 {
                failures += 1;
                break;
            }
            let id = String::from_utf16_lossy(&name[..length as usize]);
            let Ok(key) = RegistryKey::open(root, &format!(r"{UNINSTALL}\{id}"), QUERY | view)
            else {
                failures += 1;
                continue;
            };
            let name = key.text("DisplayName");
            if !name.trim().is_empty() && key.integer("SystemComponent") != 1 {
                apps.push(App {
                    name,
                    version: key.text("DisplayVersion"),
                    publisher: key.text("Publisher"),
                    msi: (key.integer("WindowsInstaller") == 1 && valid_product_code(&id))
                        .then(|| id.to_ascii_uppercase()),
                });
            }
        }
    }
    apps.sort_by_key(|app| {
        (
            app.name.to_lowercase(),
            app.version.clone(),
            app.msi.clone(),
            app.publisher.clone(),
        )
    });
    apps.dedup_by(|left, right| {
        left.name.eq_ignore_ascii_case(&right.name)
            && left.version == right.version
            && left.publisher == right.publisher
            && left.msi == right.msi
    });
    (apps, failures)
}

pub fn report() -> String {
    let (apps, failures) = installed();
    let mut output = format!(
        "Tìm thấy {} ứng dụng desktop. Mục không đọc được: {failures}.\n",
        apps.len()
    );
    for app in apps {
        let _ = writeln!(output, "\n{}  {}  {}", app.name, app.version, app.publisher);
        if let Some(code) = app.msi {
            let _ = writeln!(output, "Mã MSI: {code}");
        }
    }
    output
}

pub fn uninstall(code: &str) -> String {
    if !valid_product_code(code)
        || !installed().0.iter().any(|app| {
            app.msi
                .as_ref()
                .is_some_and(|id| id.eq_ignore_ascii_case(code))
        })
    {
        return "Mã MSI không thuộc danh sách ứng dụng desktop đang cài.".into();
    }
    super::tools::native_tool("msiexec.exe", &["/x", code, "/norestart"])
}

pub fn office(program: &str) -> String {
    use std::path::Path;
    let subkey = format!(r"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{program}");
    for (root, view) in [(USER, 0), (MACHINE, 0x100), (MACHINE, 0x200)] {
        let Ok(key) = RegistryKey::open(root, &subkey, QUERY | view) else {
            continue;
        };
        let file = key.text("");
        let path = Path::new(file.trim_matches('"'));
        if path.is_absolute() && path.is_file() {
            return match std::process::Command::new(path).arg("/safe").spawn() {
                Ok(_) => format!("Đã gửi yêu cầu mở {program} ở chế độ an toàn."),
                Err(error) => format!("Không mở được {program}: {error}"),
            };
        }
    }
    format!("Không tìm thấy đường dẫn {program} đã đăng ký trong Windows.")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_only_braced_product_codes() {
        assert!(valid_product_code("{01234567-abcd-1234-5678-901234567890}"));
        assert!(!valid_product_code(
            "{01234567-abcd-1234-5678-901234567890} /quiet"
        ));
        assert!(!valid_product_code(
            "{01234567-abcd-1234-5678-90123456789g}"
        ));
        assert!(uninstall("invalid").contains("không thuộc"));
    }
}
