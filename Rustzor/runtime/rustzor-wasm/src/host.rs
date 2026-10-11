//! The [`Host`] the interpreter sees in a browser.

use rustclr_core::Host;

use crate::bridge::imports;

/// Console output goes to the page's `console` (and to whatever `rustzor.js`
/// has hooked to it, such as the dev overlay). Clocks come from
/// `performance.now()` and `Date.now()`.
///
/// There is no standard input: a browser has none, and `Console.ReadLine`
/// returning `null` is what .NET does at end of input.
pub struct BrowserHost {
    /// `Console.Write` without a newline is buffered until one arrives, so a
    /// line built from several writes reaches `console.log` whole.
    out_line: String,
    err_line: String,
}

impl BrowserHost {
    pub fn new() -> Self {
        Self { out_line: String::new(), err_line: String::new() }
    }

    fn flush_lines(buffer: &mut String, stream: i32) {
        while let Some(end) = buffer.find('\n') {
            let line: String = buffer.drain(..=end).collect();
            imports::write(stream, line.trim_end_matches(['\r', '\n']));
        }
    }
}

impl Default for BrowserHost {
    fn default() -> Self {
        Self::new()
    }
}

impl Host for BrowserHost {
    fn write_out(&mut self, text: &str) {
        self.out_line.push_str(text);
        Self::flush_lines(&mut self.out_line, 1);
    }

    fn write_err(&mut self, text: &str) {
        self.err_line.push_str(text);
        Self::flush_lines(&mut self.err_line, 2);
    }

    fn read_line(&mut self) -> Option<String> {
        None
    }

    fn monotonic_millis(&mut self) -> u64 {
        imports::now() as u64
    }

    fn monotonic_nanos(&mut self) -> u64 {
        (imports::now() * 1_000_000.0) as u64
    }

    fn wall_clock_millis(&mut self) -> i64 {
        imports::wall_clock() as i64
    }

    fn utc_offset_minutes(&mut self) -> i32 {
        imports::tz_offset()
    }
}
