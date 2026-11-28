use super::run_script;

#[derive(Clone, Copy)]
pub enum Action {
    CleanTemp,
    RestartSpooler,
    ClearPrintJobs,
    SafeWord,
    SafeExcel,
}

impl Action {
    pub fn label(self) -> &'static str {
        match self {
            Self::CleanTemp => "Xóa tệp tạm cũ",
            Self::RestartSpooler => "Khởi động lại Spooler",
            Self::ClearPrintJobs => "Xóa hàng đợi in",
            Self::SafeWord => "Mở Word /safe",
            Self::SafeExcel => "Mở Excel /safe",
        }
    }

    pub fn confirmation(self) -> &'static str {
        match self {
            Self::SafeWord => "Mở Microsoft Word ở chế độ an toàn?",
            Self::SafeExcel => "Mở Microsoft Excel ở chế độ an toàn?",
            Self::CleanTemp => {
                r"Xóa vĩnh viễn các tệp tạm cấp đầu tiên trong LocalAppData\Temp đã qua ít nhất 7 ngày kể từ lần sửa đổi?"
            }
            Self::RestartSpooler => {
                "Khởi động lại dịch vụ Print Spooler? Các tác vụ in đang chạy có thể bị gián đoạn. Thao tác cần quyền quản trị."
            }
            Self::ClearPrintJobs => {
                "Hủy các lệnh in trong hàng đợi máy in cục bộ? Thao tác cần quyền quản trị."
            }
        }
    }

    pub fn run(self) -> String {
        match self {
            Self::SafeWord => super::installed::office("WINWORD.EXE"),
            Self::SafeExcel => super::installed::office("EXCEL.EXE"),
            Self::CleanTemp => super::cleanup::report(true),
            Self::RestartSpooler => run_script(
                r#"
$service = Get-Service -Name Spooler
if ($service.Status -eq 'Stopped') { Start-Service -Name Spooler }
else { Restart-Service -Name Spooler }
$service.WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
'Print Spooler đang chạy.'
"#,
            ),
            Self::ClearPrintJobs => run_script(
                r#"
$printers = @(Get-Printer | Where-Object { $_.Type -eq 'Local' })
$removed = 0
$failures = 0
foreach ($printer in $printers) {
    try { $jobs = @(Get-PrintJob -PrinterObject $printer) }
    catch { $failures++; "Không đọc được hàng đợi $($printer.Name): $($_.Exception.Message)"; continue }
    foreach ($job in $jobs) {
        try { Remove-PrintJob -InputObject $job; $removed++ }
        catch { $failures++; "Không hủy được lệnh $($job.ID) ở $($printer.Name): $($_.Exception.Message)" }
    }
}
"Đã hủy $removed lệnh in. Số lỗi: $failures."
"#,
            ),
        }
    }
}
