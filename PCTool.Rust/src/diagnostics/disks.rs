use std::fmt::Write;

#[link(name = "kernel32")]
unsafe extern "system" {
    fn GetLogicalDrives() -> u32;
    fn GetDriveTypeW(root_path: *const u16) -> u32;
    fn GetDiskFreeSpaceExW(
        root_path: *const u16,
        available: *mut u64,
        total: *mut u64,
        free: *mut u64,
    ) -> i32;
}

pub fn report() -> String {
    const DRIVE_FIXED: u32 = 3;
    const GIB: f64 = 1_073_741_824.0;
    let drives = unsafe { GetLogicalDrives() };
    if drives == 0 {
        return format!(
            "Không đọc được danh sách ổ: {}",
            std::io::Error::last_os_error()
        );
    }
    let mut output = String::from("Ổ đĩa     Trống (GiB)     Tổng (GiB)\n");
    for offset in 0..26 {
        if drives & (1 << offset) == 0 {
            continue;
        }
        let letter = b'A' + offset as u8;
        let root = [u16::from(letter), u16::from(b':'), u16::from(b'\\'), 0];
        if unsafe { GetDriveTypeW(root.as_ptr()) } != DRIVE_FIXED {
            continue;
        }
        let (mut total, mut free) = (0u64, 0u64);
        if unsafe {
            GetDiskFreeSpaceExW(root.as_ptr(), std::ptr::null_mut(), &mut total, &mut free)
        } != 0
        {
            let _ = writeln!(
                output,
                "{}:      {:>10.1}      {:>10.1}",
                char::from(letter),
                free as f64 / GIB,
                total as f64 / GIB
            );
        } else {
            let _ = writeln!(
                output,
                "{}:      Không đọc được dung lượng",
                char::from(letter)
            );
        }
    }
    output
}
