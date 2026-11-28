use std::os::windows::process::CommandExt;
use std::process::Command;

pub mod actions;
pub mod app_catalog;
mod backup;
mod cleanup;
mod disks;
mod installed;
mod pdf_images;
pub mod tools;
pub mod update;
mod video;
mod zalo;

pub struct Page {
    pub name: &'static str,
    pub script: &'static str,
}

pub const PAGES: &[Page] = &[
    Page {
        name: "Quét lỗi máy",
        script: r#"
'=== Dịch vụ hệ thống ==='
Get-Service Spooler,wuauserv,BITS | Format-Table Name,Status,StartType -AutoSize | Out-String
'=== Ổ đĩa ==='
Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Select-Object DeviceID,@{n='Trống (GB)';e={[math]::Round($_.FreeSpace/1GB,1)}},@{n='Tổng (GB)';e={[math]::Round($_.Size/1GB,1)}} | Format-Table -AutoSize | Out-String
'=== Cấu hình mạng đang hoạt động ==='
Get-NetIPConfiguration | Select-Object InterfaceAlias,IPv4Address,IPv4DefaultGateway | Format-Table -AutoSize | Out-String
"#,
    },
    Page {
        name: "Thông số phần cứng",
        script: r#"
'=== CPU ==='
Get-CimInstance Win32_Processor | Format-List Name,NumberOfCores,NumberOfLogicalProcessors | Out-String
'=== RAM ==='
Get-CimInstance Win32_PhysicalMemory | Select-Object BankLabel,Manufacturer,Speed,@{n='Dung lượng (GB)';e={$_.Capacity/1GB}} | Format-Table -AutoSize | Out-String
'=== GPU ==='
Get-CimInstance Win32_VideoController | Format-List Name,DriverVersion | Out-String
'=== Ổ đĩa vật lý ==='
Get-CimInstance Win32_DiskDrive | Select-Object Model,InterfaceType,@{n='Dung lượng (GB)';e={[math]::Round($_.Size/1GB,1)}} | Format-Table -AutoSize | Out-String
"#,
    },
    Page {
        name: "Dung lượng ổ",
        script: "",
    },
    Page {
        name: "Chương trình khởi động",
        script: r#"
Get-CimInstance Win32_StartupCommand | Sort-Object Name | Format-List Name,Command,Location | Out-String
"#,
    },
    Page {
        name: "Ứng dụng đã cài",
        script: "",
    },
    Page {
        name: "Chẩn đoán máy in",
        script: r#"
'=== Print Spooler ==='
Get-Service Spooler | Format-Table Name,Status,StartType | Out-String
'=== Máy in ==='
Get-CimInstance Win32_Printer | Format-Table Name,DriverName,PortName,Default,WorkOffline -AutoSize | Out-String -Width 180
'=== Hàng đợi in ==='
Get-CimInstance Win32_PrintJob | Format-Table JobId,Name,Status,TotalPages -AutoSize | Out-String
"#,
    },
    Page {
        name: "Quét driver thiếu",
        script: r#"
$devices = @(Get-CimInstance Win32_PnPEntity -Filter 'ConfigManagerErrorCode=28')
if ($devices.Count -eq 0) { 'Không tìm thấy thiết bị có mã lỗi 28 (chưa cài driver).' }
else { $devices | Format-List Name,PNPDeviceID,ConfigManagerErrorCode | Out-String }
"#,
    },
    Page {
        name: "Báo cáo & bản quyền",
        script: r#"
Get-CimInstance Win32_OperatingSystem | Format-List Caption,Version,BuildNumber,LastBootUpTime | Out-String
Get-CimInstance SoftwareLicensingProduct -Filter "ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL" |
    Select-Object Name,Description,@{n='Trạng thái';e={if($_.LicenseStatus -eq 1){'Đã kích hoạt'}else{"Mã trạng thái: $($_.LicenseStatus)"}}} | Format-List | Out-String
"#,
    },
    Page {
        name: "Phân vùng ổ cứng",
        script: r#"
Get-Partition | Sort-Object DiskNumber,PartitionNumber | Select-Object DiskNumber,PartitionNumber,DriveLetter,Type,@{n='Dung lượng (GB)';e={[math]::Round($_.Size/1GB,2)}} | Format-Table -AutoSize | Out-String
"#,
    },
    Page {
        name: "Windows Update",
        script: r#"
Get-Service wuauserv,BITS,UsoSvc | Format-Table Name,Status,StartType | Out-String
Get-HotFix | Sort-Object InstalledOn -Descending | Select-Object -First 15 HotFixID,Description,InstalledOn | Format-Table -AutoSize | Out-String
"#,
    },
    Page {
        name: "Windows Defender",
        script: r#"
Get-MpComputerStatus | Format-List AntivirusEnabled,RealTimeProtectionEnabled,AntivirusSignatureLastUpdated,QuickScanEndTime,FullScanEndTime | Out-String
"#,
    },
    Page {
        name: "Dọn dẹp rác",
        script: "",
    },
    Page {
        name: "Cài nhanh ứng dụng",
        script: "",
    },
    Page {
        name: "Sao lưu / Khôi phục app",
        script: "",
    },
    Page {
        name: "Sao lưu / Khôi phục driver",
        script: "",
    },
    Page {
        name: "Công cụ PDF",
        script: "",
    },
    Page {
        name: "Xem PDF",
        script: "",
    },
    Page {
        name: "Đọc chữ từ ảnh",
        script: "",
    },
    Page {
        name: "Cắt video",
        script: "",
    },
    Page {
        name: "Sao lưu / Khôi phục dữ liệu",
        script: "",
    },
    Page {
        name: "Gỡ ứng dụng MSI",
        script: "",
    },
    Page {
        name: "Sửa lỗi Word / Excel",
        script: "",
    },
    Page {
        name: "Sao lưu / Khôi phục Wi-Fi",
        script: "",
    },
    Page {
        name: "Ghép video",
        script: "",
    },
    Page {
        name: "Sao lưu / Khôi phục Zalo",
        script: "",
    },
    Page {
        name: "Xoay trang PDF",
        script: "",
    },
    Page {
        name: "Xuất ảnh / OCR PDF",
        script: "",
    },
];

pub fn run(page: &Page) -> String {
    match page.name {
        "Dung lượng ổ" => disks::report(),
        "Ứng dụng đã cài" => installed::report(),
        "Sửa lỗi Word / Excel" => "Chọn nút mở Word hoặc Excel ở chế độ an toàn để kiểm tra lỗi do phần bổ trợ hoặc cấu hình.".into(),
        "Dọn dẹp rác" => cleanup::report(false),
        _ => run_script(page.script),
    }
}

fn run_script(body: &str) -> String {
    run_script_with_input(body, "")
}

fn run_script_with_input(body: &str, input: &str) -> String {
    let script = format!(
        "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; \
         [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); \
         try {{ {} }} catch {{ [Console]::Error.WriteLine($_.Exception.Message); exit 1 }}",
        body
    );
    let powershell = std::env::var_os("SystemRoot")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|| std::path::PathBuf::from(r"C:\Windows"))
        .join(r"System32\WindowsPowerShell\v1.0\powershell.exe");
    match Command::new(powershell)
        .args([
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            &script,
        ])
        .env("PCTOOL_DOCUMENT_PATH", input)
        .creation_flags(0x08000000)
        .output()
    {
        Ok(output) => {
            let result = String::from_utf8_lossy(&output.stdout);
            let error = String::from_utf8_lossy(&output.stderr);
            if !output.status.success() {
                format!(
                    "{result}\nKhông hoàn thành tác vụ (mã thoát: {}).\n{error}",
                    output.status
                )
            } else if result.trim().is_empty() {
                "Không có dữ liệu để hiển thị.".into()
            } else {
                result.into_owned()
            }
        }
        Err(error) => format!("Không chạy được tác vụ: {error}"),
    }
}
