//! Print the 8x8 font the framebuffer draws text with, as one hex string
//! (128 glyphs x 8 rows, bit 0 = leftmost pixel). Desktop renderers that must
//! match the device pixel for pixel (RustNet.UI.Frontends) embed this.
fn main() {
    let mut s = String::new();
    for glyph in font8x8::legacy::BASIC_LEGACY.iter() {
        for row in glyph {
            s.push_str(&format!("{row:02X}"));
        }
    }
    println!("{s}");
}
