#![windows_subsystem = "windows"]

mod diagnostics;
mod window;

fn main() {
    if let Err(error) = window::run() {
        window::show_error(&error.to_string());
    }
}
