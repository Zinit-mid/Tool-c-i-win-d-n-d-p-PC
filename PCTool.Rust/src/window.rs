use crate::diagnostics::{self, PAGES, Page, actions::Action, tools::Tool};
use std::cell::{Cell, RefCell};
use std::ffi::c_void;
use std::io;
use std::ptr::null_mut;
use std::sync::mpsc::{self, Receiver, TryRecvError};

type Handle = *mut c_void;
type WindowProc = unsafe extern "system" fn(Handle, u32, usize, isize) -> isize;

#[repr(C)]
struct WindowClass {
    style: u32,
    procedure: Option<WindowProc>,
    class_extra: i32,
    window_extra: i32,
    instance: Handle,
    icon: Handle,
    cursor: Handle,
    background: Handle,
    menu_name: *const u16,
    class_name: *const u16,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct Rect {
    left: i32,
    top: i32,
    right: i32,
    bottom: i32,
}

#[repr(C)]
struct Message {
    window: Handle,
    message: u32,
    wparam: usize,
    lparam: isize,
    time: u32,
    x: i32,
    y: i32,
    private: u32,
}

#[link(name = "user32")]
unsafe extern "system" {
    fn RegisterClassW(class: *const WindowClass) -> u16;
    fn CreateWindowExW(
        extended: u32,
        class: *const u16,
        text: *const u16,
        style: u32,
        x: i32,
        y: i32,
        width: i32,
        height: i32,
        parent: Handle,
        menu: Handle,
        instance: Handle,
        param: Handle,
    ) -> Handle;
    fn DefWindowProcW(window: Handle, message: u32, wparam: usize, lparam: isize) -> isize;
    fn GetMessageW(message: *mut Message, window: Handle, first: u32, last: u32) -> i32;
    fn TranslateMessage(message: *const Message) -> i32;
    fn DispatchMessageW(message: *const Message) -> isize;
    fn ShowWindow(window: Handle, command: i32) -> i32;
    fn SetWindowTextW(window: Handle, text: *const u16) -> i32;
    fn SendMessageW(window: Handle, message: u32, wparam: usize, lparam: isize) -> isize;
    fn EnableWindow(window: Handle, enabled: i32) -> i32;
    fn MoveWindow(window: Handle, x: i32, y: i32, width: i32, height: i32, repaint: i32) -> i32;
    fn DrawTextW(dc: Handle, text: *const u16, count: i32, rect: *mut Rect, format: u32) -> i32;
    fn DrawFocusRect(dc: Handle, rect: *const Rect) -> i32;
    fn FillRect(dc: Handle, rect: *const Rect, brush: Handle) -> i32;
    fn SetScrollInfo(window: Handle, bar: i32, info: *const ScrollInfo, redraw: i32) -> i32;
    fn GetScrollInfo(window: Handle, bar: i32, info: *mut ScrollInfo) -> i32;
    fn IsDialogMessageW(window: Handle, message: *mut Message) -> i32;
    fn GetDlgCtrlID(window: Handle) -> i32;
    fn SetFocus(window: Handle) -> Handle;
    fn GetClientRect(window: Handle, rect: *mut Rect) -> i32;
    fn GetDpiForWindow(window: Handle) -> u32;
    fn SetProcessDPIAware() -> i32;
    fn SetTimer(window: Handle, id: usize, interval: u32, callback: Handle) -> usize;
    fn KillTimer(window: Handle, id: usize) -> i32;
    fn PostQuitMessage(exit_code: i32);
    fn LoadCursorW(instance: Handle, cursor: *const u16) -> Handle;
    fn MessageBoxW(owner: Handle, message: *const u16, title: *const u16, kind: u32) -> i32;
}

#[link(name = "shell32")]
unsafe extern "system" {
    fn ShellExecuteW(
        window: Handle,
        operation: *const u16,
        file: *const u16,
        parameters: *const u16,
        directory: *const u16,
        show: i32,
    ) -> Handle;
}

#[link(name = "kernel32")]
unsafe extern "system" {
    fn GetModuleHandleW(name: *const u16) -> Handle;
}

#[link(name = "gdi32")]
unsafe extern "system" {
    fn SetDCBrushColor(dc: Handle, color: u32) -> u32;
    fn SetBkMode(dc: Handle, mode: i32) -> i32;
    fn SetBkColor(dc: Handle, color: u32) -> u32;
    fn SetTextColor(dc: Handle, color: u32) -> u32;
    fn SelectObject(dc: Handle, object: Handle) -> Handle;
    fn DeleteObject(object: Handle) -> i32;
    fn CreateFontW(
        height: i32,
        width: i32,
        escapement: i32,
        orientation: i32,
        weight: i32,
        italic: u32,
        underline: u32,
        strikeout: u32,
        charset: u32,
        output_precision: u32,
        clip_precision: u32,
        quality: u32,
        pitch: u32,
        face: *const u16,
    ) -> Handle;
    fn GetStockObject(index: i32) -> Handle;
}

const WM_CREATE: u32 = 0x0001;
const WM_DESTROY: u32 = 0x0002;
const WM_CLOSE: u32 = 0x0010;
const WM_SIZE: u32 = 0x0005;
const WM_SETFONT: u32 = 0x0030;
const WM_COMMAND: u32 = 0x0111;
const WM_TIMER: u32 = 0x0113;
const LIST_ADD: u32 = 0x0180;
const LIST_GET_SELECTION: u32 = 0x0188;
const LIST_SET_SELECTION: u32 = 0x0186;
const LIST_ID: usize = 100;
const RUN_ID: usize = 101;
const ACTION_IDS: [usize; 2] = [102, 103];
const JOB_TIMER: usize = 1;
const CHILD_VISIBLE: u32 = 0x5000_0000;
const TAB_STOP: u32 = 0x0001_0000;
const SYSTEM_COLOR_WINDOW: usize = 5;
const ARROW_CURSOR: usize = 32512;
const DEFAULT_GUI_FONT: i32 = 17;
const FIXED_FONT: i32 = 16;

const HOME_ID: usize = 105;
const HEADER_ID: usize = 106;
const UPDATE_ID: usize = 108;
const UPDATE_TIMER: usize = 2;
const GROUP_ID: usize = 120;
const CARD_ID: usize = 200;
const DC_BRUSH: i32 = 18;
const BLUE: u32 = 0x00eb6325;
const LIGHT_BLUE: u32 = 0x00fbf1ea;
const DARK_BLUE: u32 = 0x008a3a1e;

#[repr(C)]
#[derive(Default)]
struct ScrollInfo {
    size: u32,
    mask: u32,
    minimum: i32,
    maximum: i32,
    page: u32,
    position: i32,
    track: i32,
}

#[repr(C)]
struct DrawItem {
    control_type: u32,
    control_id: u32,
    item_id: u32,
    action: u32,
    state: u32,
    control: Handle,
    dc: Handle,
    rect: Rect,
    data: usize,
}

fn draw_card(item: &DrawItem, index: usize) {
    let dpi = unsafe { GetDpiForWindow(item.control) }.max(96) as i32;
    let scale = |value| value * dpi / 96;
    let dc = item.dc;
    let brush = unsafe { GetStockObject(DC_BRUSH) };
    let previous_font = unsafe { SelectObject(dc, UI_FONT.get()) };
    let fill = |rect: &Rect, color| unsafe {
        SetDCBrushColor(dc, color);
        FillRect(dc, rect, brush);
    };
    fill(&item.rect, 0x00e8d5ba);
    let mut inside = Rect {
        left: item.rect.left + 1,
        top: item.rect.top + 1,
        right: item.rect.right - 1,
        bottom: item.rect.bottom - 1,
    };
    fill(
        &inside,
        if item.state & 1 != 0 {
            0x00f6e5cd
        } else {
            0x00ffffff
        },
    );
    let icon = Rect {
        left: inside.left + scale(10),
        top: inside.top + scale(12),
        right: inside.left + scale(38),
        bottom: inside.top + scale(40),
    };
    fill(&icon, BLUE);
    let draw = |value: &str, mut area: Rect, color, flags| unsafe {
        SetBkMode(dc, 1);
        SetTextColor(dc, color);
        DrawTextW(dc, wide(value).as_ptr(), -1, &mut area, flags);
    };
    let icon_text = match index {
        15..=17 | 25..=26 => "▤",
        18 | 23 => "▶",
        13 | 14 | 19 | 22 | 24 => "↧",
        _ => "◆",
    };
    draw(icon_text, icon, 0x00ffffff, 0x25);
    let left = inside.left + scale(47);
    let right = inside.right - scale(6);
    draw(
        PAGES[index].name,
        Rect {
            left,
            right,
            top: inside.top + scale(9),
            bottom: inside.top + scale(31),
        },
        DARK_BLUE,
        0x8024,
    );
    draw(
        CARD_DETAILS[index],
        Rect {
            left,
            right,
            top: inside.top + scale(33),
            bottom: inside.bottom - scale(3),
        },
        0x00645340,
        0x0810,
    );
    if item.state & 0x10 != 0 {
        inside.left += 3;
        inside.top += 3;
        inside.right -= 3;
        inside.bottom -= 3;
        unsafe { DrawFocusRect(dc, &inside) };
    }
    if !previous_font.is_null() {
        unsafe { SelectObject(dc, previous_font) };
    }
}

const GROUPS: &[(&str, &[usize])] = &[
    ("Máy tính của bạn", &[0, 1, 11, 2, 3, 4, 20, 7, 8]),
    ("Cài lại Windows", &[14, 22, 13, 19, 24, 6]),
    ("Cài đặt & tiện ích", &[12, 18, 23]),
    ("Sửa lỗi & bảo vệ", &[5, 21, 9, 10]),
    ("PDF", &[15, 16, 17, 25, 26]),
];

const CARD_DETAILS: &[&str] = &[
    "Kiểm tra mạng, ổ đĩa và dịch vụ",
    "CPU, RAM, ổ đĩa và đồ họa",
    "Dung lượng đã dùng và còn trống",
    "Xem các mục tự mở cùng Windows",
    "Danh sách ứng dụng desktop",
    "Máy in, Spooler và hàng đợi",
    "Tìm thiết bị chưa có driver",
    "Thông tin Windows và kích hoạt",
    "Xem thông tin phân vùng",
    "Trạng thái và lịch sử cập nhật",
    "Xem trạng thái bảo vệ",
    "Xem trước và xóa tệp tạm cũ",
    "Cài ứng dụng bằng WinGet",
    "Xuất và cài lại từ danh sách",
    "Xuất và nạp gói driver",
    "Gộp hoặc tách tài liệu PDF",
    "Mở bằng trình xem mặc định",
    "Nhận dạng chữ Việt và Anh",
    "Trích một đoạn video",
    "Sao chép thư mục dữ liệu",
    "Mở trình gỡ Windows Installer",
    "Mở Word hoặc Excel ở chế độ an toàn",
    "Xuất và nhập hồ sơ mạng",
    "Nối các clip MP4 cùng cấu hình",
    "Sao chép thư mục tài khoản",
    "Xoay toàn bộ hoặc vài trang",
    "Xuất PNG và nhận dạng chữ",
];

struct Controls {
    title: Handle,
    back: Handle,
    update: Handle,
    update_job: Option<Receiver<(String, bool)>>,
    page_title: Handle,
    scroll: Handle,
    headings: Vec<Handle>,
    cards: Vec<Handle>,
    home: bool,
    home_scroll: i32,
    home_max: i32,
    pages: Handle,
    run: Handle,
    actions: [Handle; 2],
    instructions: Handle,
    catalog: Handle,
    input: Handle,
    output: Handle,
    status: Handle,
    job: Option<Receiver<String>>,
    changing_system: bool,
}

#[derive(Clone, Copy)]
enum Operation {
    Maintenance(Action),
    Tool(Tool),
}

impl Operation {
    fn label(self) -> &'static str {
        match self {
            Self::Maintenance(action) => action.label(),
            Self::Tool(tool) => tool.label(),
        }
    }

    fn run(self, input: &str) -> String {
        match self {
            Self::Maintenance(action) => action.run(),
            Self::Tool(tool) => tool.run(input),
        }
    }
}

thread_local! {
    static UI_FONT: Cell<Handle> = const { Cell::new(null_mut()) };
    static MONO_FONT: Cell<Handle> = const { Cell::new(null_mut()) };
    static CONTROLS: RefCell<Option<Controls>> = const { RefCell::new(None) };
}

struct Fonts;

impl Fonts {
    fn load(window: Handle) {
        let dpi = unsafe { GetDpiForWindow(window) }.max(96) as i32;
        let create = |px: i32, name: &str| unsafe {
            CreateFontW(
                -px * dpi / 96,
                0,
                0,
                0,
                400,
                0,
                0,
                0,
                1,
                0,
                0,
                5,
                0,
                wide(name).as_ptr(),
            )
        };
        UI_FONT.set(create(19, "Segoe UI"));
        MONO_FONT.set(create(17, "Consolas"));
    }
}

impl Drop for Fonts {
    fn drop(&mut self) {
        for font in [UI_FONT.get(), MONO_FONT.get()] {
            if !font.is_null() {
                unsafe { DeleteObject(font) };
            }
        }
    }
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16()
        .map(|unit| if unit == 0 { 0xfffd } else { unit })
        .chain([0])
        .collect()
}

fn set_text(window: Handle, text: &str) {
    unsafe { SetWindowTextW(window, wide(text).as_ptr()) };
}

fn set_missing_input(window: Handle, instructions: &str) {
    unsafe {
        MessageBoxW(
            window,
            wide(instructions).as_ptr(),
            wide("Nhập dữ liệu").as_ptr(),
            0x40,
        )
    };
}

pub fn show_error(error: &str) {
    unsafe {
        MessageBoxW(
            null_mut(),
            wide(error).as_ptr(),
            wide("PCTool Rust").as_ptr(),
            0x10,
        )
    };
}

pub fn run() -> io::Result<()> {
    let _fonts = Fonts;
    let class_name = wide("PCToolRustMain");
    let title = wide("PCTool Rust – Bảo trì Windows");
    unsafe {
        SetProcessDPIAware();
        let instance = GetModuleHandleW(std::ptr::null());
        let class = WindowClass {
            style: 0,
            procedure: Some(procedure),
            class_extra: 0,
            window_extra: 0,
            instance,
            icon: null_mut(),
            cursor: LoadCursorW(null_mut(), ARROW_CURSOR as *const u16),
            background: (SYSTEM_COLOR_WINDOW + 1) as Handle,
            menu_name: std::ptr::null(),
            class_name: class_name.as_ptr(),
        };
        if RegisterClassW(&class) == 0 {
            return Err(io::Error::last_os_error());
        }
        let window = CreateWindowExW(
            0,
            class_name.as_ptr(),
            title.as_ptr(),
            0x00cf_0000,
            i32::MIN,
            i32::MIN,
            1180,
            720,
            null_mut(),
            null_mut(),
            instance,
            null_mut(),
        );
        if window.is_null() {
            return Err(io::Error::last_os_error());
        }
        ShowWindow(window, 5);
        let mut message = std::mem::zeroed();
        loop {
            match GetMessageW(&mut message, null_mut(), 0, 0) {
                -1 => return Err(io::Error::last_os_error()),
                0 => return Ok(()),
                _ => {
                    if IsDialogMessageW(window, &mut message) == 0 {
                        TranslateMessage(&message);
                        DispatchMessageW(&message);
                    }
                }
            }
        }
    }
}

unsafe extern "system" fn procedure(
    window: Handle,
    message: u32,
    wparam: usize,
    lparam: isize,
) -> isize {
    unsafe {
        match message {
            WM_CREATE => match Controls::create(window) {
                Some(mut controls) => {
                    controls.layout(window);
                    CONTROLS.with_borrow_mut(|slot| *slot = Some(controls));
                    0
                }
                None => -1,
            },
            WM_SIZE => {
                CONTROLS.with_borrow_mut(|slot| {
                    if let Some(controls) = slot.as_mut() {
                        controls.layout(window);
                    }
                });
                0
            }
            WM_COMMAND
                if wparam & 0xffff == RUN_ID
                    || (wparam & 0xffff == LIST_ID && wparam >> 16 == 2) =>
            {
                CONTROLS.with_borrow_mut(|slot| {
                    if let Some(controls) = slot
                        && let Some(page) = controls.selected_page()
                        && controls.input_instructions().is_none()
                    {
                        controls.start(window, page.name, false, move || diagnostics::run(page));
                    }
                });
                0
            }
            WM_COMMAND
                if (CARD_ID..CARD_ID + PAGES.len()).contains(&(wparam & 0xffff))
                    || wparam & 0xffff == HOME_ID
                    || (wparam & 0xffff == LIST_ID && wparam >> 16 == 1) =>
            {
                CONTROLS.with_borrow_mut(|slot| {
                    let Some(controls) = slot.as_mut() else {
                        return;
                    };
                    if controls.job.is_some() {
                        return;
                    }
                    let id = wparam & 0xffff;
                    if id == HOME_ID {
                        controls.home = true;
                        SetFocus(controls.title);
                    } else {
                        if id >= CARD_ID {
                            SendMessageW(controls.pages, LIST_SET_SELECTION, id - CARD_ID, 0);
                        }
                        controls.home = false;
                        set_text(controls.input, "");
                        let introduction = if controls.input_instructions().is_some() {
                            "Kết quả sẽ xuất hiện ở đây sau khi bạn chạy công cụ."
                        } else {
                            "Đang mở công cụ…"
                        };
                        set_text(controls.output, introduction);
                        set_text(controls.status, "Sẵn sàng");
                        if let Some(page) = controls.selected_page() {
                            set_text(controls.page_title, page.name);
                        }
                    }
                    controls.layout(window);
                    controls.refresh_actions();
                    if !controls.home {
                        SetFocus(controls.back);
                        if id >= CARD_ID
                            && controls.input_instructions().is_none()
                            && let Some(page) = controls.selected_page()
                        {
                            controls
                                .start(window, page.name, false, move || diagnostics::run(page));
                        }
                    }
                });
                0
            }
            0x0115 | 0x020a => {
                CONTROLS.with_borrow_mut(|slot| {
                    let Some(controls) = slot.as_mut() else {
                        return;
                    };
                    if !controls.home {
                        return;
                    }
                    let step = 84 * GetDpiForWindow(window).max(96) as i32 / 96;
                    let position = if message == 0x020a {
                        controls.home_scroll - (wparam >> 16) as i16 as i32 * step / 120
                    } else {
                        match wparam & 0xffff {
                            0 => controls.home_scroll - step,
                            1 => controls.home_scroll + step,
                            2 => controls.home_scroll - 4 * step,
                            3 => controls.home_scroll + 4 * step,
                            4 | 5 => {
                                let mut info = ScrollInfo {
                                    size: size_of::<ScrollInfo>() as u32,
                                    mask: 16,
                                    ..Default::default()
                                };
                                GetScrollInfo(controls.scroll, 2, &mut info);
                                info.track
                            }
                            6 => 0,
                            7 => controls.home_max,
                            _ => return,
                        }
                    };
                    controls.home_scroll = position.clamp(0, controls.home_max);
                    controls.layout(window);
                });
                0
            }
            0x002b if (CARD_ID..CARD_ID + PAGES.len()).contains(&wparam) && lparam != 0 => {
                draw_card(&*(lparam as *const DrawItem), wparam - CARD_ID);
                1
            }
            0x0014 => {
                let mut rect = Rect::default();
                GetClientRect(window, &mut rect);
                let dc = wparam as Handle;
                SetDCBrushColor(dc, LIGHT_BLUE);
                FillRect(dc, &rect, GetStockObject(DC_BRUSH));
                1
            }
            0x0138 => {
                let dc = wparam as Handle;
                let id = GetDlgCtrlID(lparam as Handle) as usize;
                let (background, foreground) = if id == HEADER_ID {
                    (BLUE, 0x00ffffff)
                } else {
                    (LIGHT_BLUE, DARK_BLUE)
                };
                SetBkColor(dc, background);
                SetTextColor(dc, foreground);
                SetDCBrushColor(dc, background);
                GetStockObject(DC_BRUSH) as isize
            }
            WM_COMMAND if wparam & 0xffff == UPDATE_ID => {
                CONTROLS.with_borrow_mut(|slot| {
                    if let Some(controls) = slot {
                        controls.start_update(window);
                    }
                });
                0
            }
            WM_TIMER if wparam == UPDATE_TIMER => {
                let result = CONTROLS.with_borrow_mut(|slot| slot.as_mut()?.poll_update(window));
                if let Some((status, available)) = result {
                    let answer = MessageBoxW(
                        window,
                        wide(&status).as_ptr(),
                        wide("Cập nhật PCTool Rust").as_ptr(),
                        if available { 0x124 } else { 0x40 },
                    );
                    if available
                        && answer == 6
                        && ShellExecuteW(
                            window,
                            wide("open").as_ptr(),
                            wide(diagnostics::update::DOWNLOAD).as_ptr(),
                            std::ptr::null(),
                            std::ptr::null(),
                            1,
                        ) as isize
                            <= 32
                    {
                        show_error(diagnostics::update::DOWNLOAD);
                    }
                }
                0
            }
            WM_COMMAND if ACTION_IDS.contains(&(wparam & 0xffff)) => {
                let id = wparam & 0xffff;
                let action = CONTROLS.with_borrow(|slot| {
                    let controls = slot.as_ref()?;
                    if controls.job.is_some() {
                        return None;
                    }
                    let operation = controls.page_actions()[id - ACTION_IDS[0]]?;
                    let input = if matches!(operation, Operation::Tool(_)) {
                        let mut text = vec![0u16; 32768];
                        let len = {
                            SendMessageW(
                                controls.input,
                                0x000d,
                                text.len(),
                                text.as_mut_ptr() as isize,
                            )
                        };
                        String::from_utf16_lossy(&text[..len as usize])
                    } else {
                        String::new()
                    };
                    let (input, summary) = if matches!(operation, Operation::Tool(Tool::Install)) {
                        let (ids, names) = controls.selected_apps();
                        (format!("{ids}\n{input}"), format!("{names}\n{input}"))
                    } else {
                        (input.clone(), input)
                    };
                    Some((operation, input, summary))
                });
                if let Some((action, input, summary)) = action {
                    let prompt = match action {
                        Operation::Maintenance(operation) => operation.confirmation().to_owned(),
                        Operation::Tool(tool) if input.trim().is_empty() => {
                            set_missing_input(window, tool.instructions());
                            return 0;
                        }
                        Operation::Tool(_) => {
                            format!("Thực hiện {} với dữ liệu sau?\n\n{summary}", action.label())
                        }
                    };
                    let answer = {
                        MessageBoxW(
                            window,
                            wide(&prompt).as_ptr(),
                            wide(action.label()).as_ptr(),
                            0x134,
                        )
                    };
                    if answer == 6 {
                        CONTROLS.with_borrow_mut(|slot| {
                            if let Some(controls) = slot {
                                controls.start(window, action.label(), true, move || {
                                    action.run(&input)
                                });
                            }
                        });
                    }
                }
                0
            }
            WM_CLOSE
                if CONTROLS
                    .with_borrow(|slot| slot.as_ref().is_some_and(|c| c.changing_system)) =>
            {
                0
            }
            WM_TIMER if wparam == JOB_TIMER => {
                CONTROLS.with_borrow_mut(|slot| {
                    if let Some(controls) = slot {
                        controls.poll(window);
                    }
                });
                0
            }
            WM_DESTROY => {
                PostQuitMessage(0);
                0
            }
            _ => DefWindowProcW(window, message, wparam, lparam),
        }
    }
}

impl Controls {
    fn create(window: Handle) -> Option<Self> {
        Fonts::load(window);
        let child = |class: &str, text: &str, style, border, id, font| unsafe {
            let control = CreateWindowExW(
                border,
                wide(class).as_ptr(),
                wide(text).as_ptr(),
                CHILD_VISIBLE | style,
                0,
                0,
                0,
                0,
                window,
                id as Handle,
                GetModuleHandleW(std::ptr::null()),
                null_mut(),
            );
            if control.is_null() {
                None
            } else {
                let custom = if font == FIXED_FONT {
                    MONO_FONT.get()
                } else {
                    UI_FONT.get()
                };
                let actual = if custom.is_null() {
                    GetStockObject(font)
                } else {
                    custom
                };
                SendMessageW(control, WM_SETFONT, actual as usize, 1);
                Some(control)
            }
        };
        let title = child(
            "STATIC",
            "    PCTool  ·  Cài Win & Dọn dẹp PC",
            0x200,
            0,
            HEADER_ID,
            DEFAULT_GUI_FONT,
        )?;
        let update = child(
            "BUTTON",
            "Kiểm tra cập nhật",
            TAB_STOP,
            0,
            UPDATE_ID,
            DEFAULT_GUI_FONT,
        )?;
        let back = child(
            "BUTTON",
            "←  Trang chủ",
            TAB_STOP,
            0,
            HOME_ID,
            DEFAULT_GUI_FONT,
        )?;
        let page_title = child("STATIC", "", 0x200, 0, 0, DEFAULT_GUI_FONT)?;
        let scroll = child("SCROLLBAR", "", 1, 0, 0, DEFAULT_GUI_FONT)?;
        let pages = child("LISTBOX", "", 0, 0, LIST_ID, DEFAULT_GUI_FONT)?;
        let headings = GROUPS
            .iter()
            .enumerate()
            .map(|(index, (name, _))| {
                child("STATIC", name, 0x200, 0, GROUP_ID + index, DEFAULT_GUI_FONT)
            })
            .collect::<Option<Vec<_>>>()?;
        let cards = PAGES
            .iter()
            .enumerate()
            .map(|(index, page)| {
                let text = format!("{}\n{}", page.name, CARD_DETAILS[index]);
                child(
                    "BUTTON",
                    &text,
                    TAB_STOP | 0xb,
                    0,
                    CARD_ID + index,
                    DEFAULT_GUI_FONT,
                )
            })
            .collect::<Option<Vec<_>>>()?;
        let run = child(
            "BUTTON",
            "Chạy chẩn đoán",
            TAB_STOP,
            0,
            RUN_ID,
            DEFAULT_GUI_FONT,
        )?;
        let actions = [
            child("BUTTON", "", TAB_STOP, 0, ACTION_IDS[0], DEFAULT_GUI_FONT)?,
            child("BUTTON", "", TAB_STOP, 0, ACTION_IDS[1], DEFAULT_GUI_FONT)?,
        ];
        for button in actions {
            unsafe { ShowWindow(button, 0) };
        }
        let catalog = child(
            "LISTBOX",
            "",
            TAB_STOP | 0x0020_0108,
            0x200,
            107,
            DEFAULT_GUI_FONT,
        )?;
        for (name, _, group) in diagnostics::app_catalog::CATALOG {
            let label = format!("{name}  ·  {group}");
            unsafe { SendMessageW(catalog, LIST_ADD, 0, wide(&label).as_ptr() as isize) };
        }
        unsafe { ShowWindow(catalog, 0) };
        let instructions = child("STATIC", "", 0, 0, 0, DEFAULT_GUI_FONT)?;
        let input = child(
            "EDIT",
            "",
            TAB_STOP | 0x0020_1044,
            0x200,
            104,
            DEFAULT_GUI_FONT,
        )?;
        unsafe {
            ShowWindow(instructions, 0);
            ShowWindow(input, 0);
        }
        let output = child(
            "EDIT",
            "Chọn một mục bên trái để xem thông tin.",
            TAB_STOP | 0x0030_08c4,
            0x200,
            0,
            FIXED_FONT,
        )?;
        let status = child("STATIC", "Sẵn sàng", 0, 0, 0, DEFAULT_GUI_FONT)?;
        for page in PAGES {
            unsafe { SendMessageW(pages, LIST_ADD, 0, wide(page.name).as_ptr() as isize) };
        }
        unsafe { SendMessageW(pages, LIST_SET_SELECTION, 0, 0) };
        Some(Self {
            title,
            back,
            update,
            update_job: None,
            page_title,
            scroll,
            headings,
            cards,
            home: true,
            home_scroll: 0,
            home_max: 0,
            pages,
            run,
            actions,
            instructions,
            catalog,
            input,
            output,
            status,
            job: None,
            changing_system: false,
        })
    }

    fn layout(&mut self, window: Handle) {
        let mut rect = Rect::default();
        unsafe { GetClientRect(window, &mut rect) };
        let dpi = unsafe { GetDpiForWindow(window) }.max(96) as i32;
        let scale = |value| value * dpi / 96;
        let margin = scale(16);
        let header = scale(54);
        let row = scale(36);
        let place = |control, x, y, width: i32, height: i32| unsafe {
            MoveWindow(control, x, y, width.max(0), height.max(0), 1);
        };
        let show = |control, visible| unsafe {
            ShowWindow(control, if visible { 8 } else { 0 });
        };
        place(self.title, 0, 0, rect.right, header);
        let update_width = scale(204).min((rect.right / 2).max(0));
        place(
            self.update,
            rect.right - update_width - margin,
            scale(8),
            update_width,
            row,
        );
        show(self.pages, false);
        for control in [self.back, self.page_title, self.output, self.status] {
            show(control, !self.home);
        }
        if self.home {
            for control in [
                self.run,
                self.instructions,
                self.catalog,
                self.input,
                self.actions[0],
                self.actions[1],
            ] {
                show(control, false);
            }
            let viewport = (rect.bottom - header - margin).max(0);
            let columns: usize = if rect.right >= scale(880) {
                4
            } else if rect.right >= scale(600) {
                3
            } else {
                2
            };
            let heading_height = scale(34);
            let card_height = scale(94);
            let gap = scale(10);
            let content_total: i32 = GROUPS
                .iter()
                .map(|(_, cards)| {
                    heading_height + cards.len().div_ceil(columns) as i32 * (card_height + gap)
                })
                .sum::<i32>()
                + margin;
            self.home_max = (content_total - viewport).max(0);
            self.home_scroll = self.home_scroll.clamp(0, self.home_max);
            let scroll_width = scale(18);
            place(
                self.scroll,
                rect.right - scroll_width - scale(4),
                header + margin,
                scroll_width,
                viewport,
            );
            let info = ScrollInfo {
                size: size_of::<ScrollInfo>() as u32,
                mask: 7,
                maximum: (content_total - 1).max(0),
                page: viewport as u32,
                position: self.home_scroll,
                ..Default::default()
            };
            unsafe { SetScrollInfo(self.scroll, 2, &info, 1) };
            show(self.scroll, self.home_max > 0);
            let width = (rect.right - 2 * margin - scroll_width - (columns as i32 - 1) * gap)
                / columns as i32;
            let mut y = header + margin - self.home_scroll;
            for (group, (_, cards)) in GROUPS.iter().enumerate() {
                let visible = |top, height| top >= header && top + height <= rect.bottom;
                place(
                    self.headings[group],
                    margin,
                    y,
                    rect.right - 2 * margin,
                    heading_height,
                );
                show(self.headings[group], visible(y, heading_height));
                y += heading_height;
                for (slot, &index) in cards.iter().enumerate() {
                    let card_top = y + (slot / columns) as i32 * (card_height + gap);
                    let x = margin + (slot % columns) as i32 * (width + gap);
                    place(self.cards[index], x, card_top, width, card_height);
                    show(self.cards[index], visible(card_top, card_height));
                }
                y += cards.len().div_ceil(columns) as i32 * (card_height + gap);
            }
            return;
        }
        for &control in self
            .headings
            .iter()
            .chain(&self.cards)
            .chain([&self.scroll])
        {
            show(control, false);
        }
        place(self.back, margin, header + scale(8), scale(116), row);
        place(
            self.page_title,
            margin + scale(132),
            header + scale(8),
            rect.right - margin - scale(132),
            row,
        );
        let top = header + row + scale(18);
        let width = rect.right - 2 * margin;
        let input_height = if self.input_instructions().is_some() {
            let instruction_height = scale(54);
            let catalog_height = if self.is_installer() {
                (rect.bottom / 3).min(scale(230))
            } else {
                0
            };
            let input_height = if self.is_installer() {
                scale(52)
            } else {
                scale(92)
            };
            place(
                self.catalog,
                margin,
                top + instruction_height,
                width,
                catalog_height,
            );
            place(self.instructions, margin, top, width, instruction_height);
            place(
                self.input,
                margin,
                top + instruction_height + catalog_height,
                width,
                input_height,
            );
            instruction_height + catalog_height + input_height + scale(10)
        } else {
            0
        };
        let action_y = top + input_height;
        let available = width.max(0);
        let has_input = input_height > 0;
        let action_width = (available / if has_input { 2 } else { 3 }).min(scale(206));
        place(
            self.run,
            margin,
            action_y,
            (action_width - scale(8)).max(0),
            row,
        );
        for (index, button) in self.actions.iter().enumerate() {
            let slot = index as i32 + i32::from(!has_input);
            place(
                *button,
                margin + slot * action_width,
                action_y,
                (action_width - scale(8)).max(0),
                row,
            );
        }
        let status_y = action_y + row + scale(6);
        place(self.status, margin, status_y, width, scale(24));
        let result_y = status_y + scale(27);
        place(
            self.output,
            margin,
            result_y,
            width,
            (rect.bottom - result_y - margin).max(0),
        );
    }

    fn selected_page(&self) -> Option<&'static Page> {
        let index = unsafe { SendMessageW(self.pages, LIST_GET_SELECTION, 0, 0) };
        PAGES.get(index as usize)
    }

    fn page_actions(&self) -> [Option<Operation>; 2] {
        use Operation::{Maintenance, Tool as External};
        match self.selected_page().map(|page| page.name) {
            Some("Dọn dẹp rác") => [Some(Maintenance(Action::CleanTemp)), None],
            Some("Chẩn đoán máy in") => [
                Some(Maintenance(Action::RestartSpooler)),
                Some(Maintenance(Action::ClearPrintJobs)),
            ],
            Some("Cài nhanh ứng dụng") => [Some(External(Tool::Install)), None],
            Some("Sao lưu / Khôi phục app") => [
                Some(External(Tool::ExportApps)),
                Some(External(Tool::ImportApps)),
            ],
            Some("Sao lưu / Khôi phục driver") => [
                Some(External(Tool::ExportDrivers)),
                Some(External(Tool::RestoreDrivers)),
            ],
            Some("Công cụ PDF") => [
                Some(External(Tool::MergePdf)),
                Some(External(Tool::SplitPdf)),
            ],
            Some("Xem PDF") => [Some(External(Tool::OpenPdf)), None],
            Some("Đọc chữ từ ảnh") => [Some(External(Tool::ReadImage)), None],
            Some("Cắt video") => [Some(External(Tool::CutVideo)), None],
            Some("Sao lưu / Khôi phục dữ liệu") => [
                Some(External(Tool::BackupData)),
                Some(External(Tool::RestoreData)),
            ],
            Some("Gỡ ứng dụng MSI") => [Some(External(Tool::UninstallMsi)), None],
            Some("Sửa lỗi Word / Excel") => [
                Some(Maintenance(Action::SafeWord)),
                Some(Maintenance(Action::SafeExcel)),
            ],
            Some("Sao lưu / Khôi phục Wi-Fi") => [
                Some(External(Tool::ExportWifi)),
                Some(External(Tool::ImportWifi)),
            ],
            Some("Ghép video") => [Some(External(Tool::MergeVideo)), None],
            Some("Sao lưu / Khôi phục Zalo") => [
                Some(External(Tool::BackupZalo)),
                Some(External(Tool::RestoreZalo)),
            ],
            Some("Xoay trang PDF") => [Some(External(Tool::RotatePdf)), None],
            Some("Xuất ảnh / OCR PDF") => [
                Some(External(Tool::ExportPdfImages)),
                Some(External(Tool::RecognizePdf)),
            ],
            _ => [None, None],
        }
    }

    fn is_installer(&self) -> bool {
        self.selected_page()
            .is_some_and(|page| page.name == "Cài nhanh ứng dụng")
    }

    fn selected_apps(&self) -> (String, String) {
        let mut ids = Vec::new();
        let mut names = Vec::new();
        for (index, (name, id, _)) in diagnostics::app_catalog::CATALOG.iter().enumerate() {
            if unsafe { SendMessageW(self.catalog, 0x0187, index, 0) } > 0 {
                ids.push(*id);
                names.push(*name);
            }
        }
        (ids.join("\n"), names.join("\n"))
    }

    fn input_instructions(&self) -> Option<&'static str> {
        match self.page_actions()[0] {
            Some(Operation::Tool(tool)) => Some(tool.instructions()),
            _ => None,
        }
    }

    fn refresh_actions(&self) {
        if self.home {
            return;
        }
        let actions = self.page_actions();
        for (button, action) in self.actions.into_iter().zip(actions) {
            if let Some(action) = action {
                set_text(button, action.label());
                unsafe {
                    EnableWindow(button, i32::from(self.job.is_none()));
                    ShowWindow(button, 5);
                }
            } else {
                unsafe {
                    ShowWindow(button, 0);
                }
            }
        }
        let label = match self.selected_page().map(|page| page.name) {
            Some("Dọn dẹp rác") => "Xem trước",
            Some("Phân vùng ổ cứng") => "Tải lại phân vùng",
            _ => "Tải lại dữ liệu",
        };
        set_text(self.run, label);
        let instructions = self.input_instructions();
        if let Some(text) = instructions {
            set_text(self.instructions, text);
        }
        unsafe {
            let show = if instructions.is_some() { 5 } else { 0 };
            ShowWindow(self.instructions, show);
            ShowWindow(self.input, show);
            ShowWindow(self.catalog, if self.is_installer() { 8 } else { 0 });
            EnableWindow(self.catalog, i32::from(self.job.is_none()));
            EnableWindow(self.input, i32::from(self.job.is_none()));
            EnableWindow(
                self.run,
                i32::from(self.job.is_none() && instructions.is_none()),
            );
        }
    }

    fn start_update(&mut self, window: Handle) {
        if self.update_job.is_some() {
            return;
        }
        if unsafe { SetTimer(window, UPDATE_TIMER, 100, null_mut()) } == 0 {
            set_text(self.update, "Kiểm tra lại");
            return;
        }
        let (sender, receiver) = mpsc::channel();
        if std::thread::Builder::new()
            .spawn(move || {
                let _ = sender.send(diagnostics::update::check());
            })
            .is_err()
        {
            unsafe { KillTimer(window, UPDATE_TIMER) };
            set_text(self.update, "Kiểm tra lại");
            return;
        }
        self.update_job = Some(receiver);
        set_text(self.update, "Đang kiểm tra…");
        unsafe { EnableWindow(self.update, 0) };
    }

    fn poll_update(&mut self, window: Handle) -> Option<(String, bool)> {
        let result = match self.update_job.as_ref()?.try_recv() {
            Ok(result) => result,
            Err(TryRecvError::Empty) => return None,
            Err(TryRecvError::Disconnected) => ("Kiểm tra cập nhật bị gián đoạn.".into(), false),
        };
        self.update_job = None;
        unsafe {
            KillTimer(window, UPDATE_TIMER);
            EnableWindow(self.update, 1);
        }
        set_text(self.update, "Kiểm tra cập nhật");
        Some(result)
    }

    fn start(
        &mut self,
        window: Handle,
        name: &str,
        changing_system: bool,
        task: impl FnOnce() -> String + Send + 'static,
    ) {
        if self.job.is_some() {
            return;
        }
        let (sender, receiver) = mpsc::channel();
        if unsafe { SetTimer(window, JOB_TIMER, 80, null_mut()) } == 0 {
            set_text(self.status, "Không khởi tạo được bộ theo dõi tác vụ.");
            return;
        }
        if let Err(error) = std::thread::Builder::new().spawn(move || {
            let _ = sender.send(task());
        }) {
            unsafe { KillTimer(window, JOB_TIMER) };
            set_text(self.status, &format!("Không mở được tác vụ: {error}"));
            return;
        }
        self.job = Some(receiver);
        self.changing_system = changing_system;
        unsafe {
            EnableWindow(self.run, 0);
            EnableWindow(self.pages, 0);
            EnableWindow(self.back, 0);
        }
        self.refresh_actions();
        set_text(self.output, "Đang xử lý…");
        set_text(self.status, name);
    }

    fn poll(&mut self, window: Handle) {
        let Some(job) = &self.job else {
            return;
        };
        let result = match job.try_recv() {
            Ok(result) => result,
            Err(TryRecvError::Empty) => return,
            Err(TryRecvError::Disconnected) => "Tác vụ kết thúc đột ngột.".into(),
        };
        self.job = None;
        self.changing_system = false;
        unsafe {
            KillTimer(window, JOB_TIMER);
            EnableWindow(self.run, 1);
            EnableWindow(self.pages, 1);
            EnableWindow(self.back, 1);
        }
        self.refresh_actions();
        set_text(
            self.output,
            &result.replace("\r\n", "\n").replace('\n', "\r\n"),
        );
        set_text(self.status, "Sẵn sàng");
    }
}
