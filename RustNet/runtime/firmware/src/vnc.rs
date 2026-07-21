//! Minimal VNC (RFB 3.8) server that streams the device framebuffer over
//! TCP so a desktop VNC client can watch the display remotely.
//!
//! Scope: security type `None`, a single true-colour 32bpp pixel format, and
//! `Raw`-encoded full-frame updates. Input events (pointer/keyboard) are
//! parsed and dropped. Enough for `RustNet.Media.Vnc.Start(port)` to expose
//! the panel; encodings like Tight/ZRLE are future work.

use rustnet_diag::Logger;
use rustnet_gfx::{Color, Framebuffer};
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

type Display = Arc<Mutex<Option<Framebuffer>>>;

/// Start the RFB server on `port`, serving `display`, until `running` is
/// cleared. Non-blocking accept loop on a background thread.
pub fn start(port: u16, display: Display, running: Arc<AtomicBool>, logger: Logger) {
    running.store(true, Ordering::SeqCst);
    std::thread::spawn(move || {
        let listener = match TcpListener::bind(("0.0.0.0", port)) {
            Ok(l) => l,
            Err(e) => {
                logger.warn("vnc", format!("bind :{port} failed: {e}"));
                running.store(false, Ordering::SeqCst);
                return;
            }
        };
        listener.set_nonblocking(true).ok();
        logger.info("vnc", format!("server listening on :{port}"));
        while running.load(Ordering::SeqCst) {
            match listener.accept() {
                Ok((stream, _)) => {
                    let d = display.clone();
                    let r = running.clone();
                    let lg = logger.clone();
                    std::thread::spawn(move || {
                        if let Err(e) = serve_client(stream, d, r) {
                            lg.info("vnc", format!("client closed: {e}"));
                        }
                    });
                }
                Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                    std::thread::sleep(Duration::from_millis(50));
                }
                Err(_) => break,
            }
        }
        logger.info("vnc", "server stopped");
    });
}

fn serve_client(mut s: TcpStream, display: Display, running: Arc<AtomicBool>) -> std::io::Result<()> {
    s.set_nonblocking(false)?;
    s.set_read_timeout(Some(Duration::from_secs(30)))?;

    // ---- handshake (RFB 3.8) ----
    s.write_all(b"RFB 003.008\n")?;
    let mut ver = [0u8; 12];
    s.read_exact(&mut ver)?;

    // Security: offer only "None" (1).
    s.write_all(&[1u8, 1u8])?;
    let mut sel = [0u8; 1];
    s.read_exact(&mut sel)?;
    s.write_all(&[0, 0, 0, 0])?; // SecurityResult = OK

    // ClientInit (shared-flag), then ServerInit.
    let mut shared = [0u8; 1];
    s.read_exact(&mut shared)?;

    let (w, h) = {
        let g = display.lock().unwrap();
        g.as_ref().map(|f| (f.width as u16, f.height as u16)).unwrap_or((0, 0))
    };
    s.write_all(&server_init(w, h))?;

    // ---- message loop ----
    loop {
        if !running.load(Ordering::SeqCst) {
            break;
        }
        let mut t = [0u8; 1];
        if s.read_exact(&mut t).is_err() {
            break;
        }
        match t[0] {
            0 => {
                // SetPixelFormat: 3 padding + 16-byte pixel format.
                let mut b = [0u8; 19];
                s.read_exact(&mut b)?;
            }
            2 => {
                // SetEncodings: 1 padding + u16 count + count*i32.
                let mut pad = [0u8; 1];
                s.read_exact(&mut pad)?;
                let mut cnt = [0u8; 2];
                s.read_exact(&mut cnt)?;
                let n = u16::from_be_bytes(cnt) as usize;
                let mut enc = vec![0u8; n * 4];
                s.read_exact(&mut enc)?;
            }
            3 => {
                // FramebufferUpdateRequest: incremental u8 + x,y,w,h u16.
                let mut b = [0u8; 9];
                s.read_exact(&mut b)?;
                s.write_all(&framebuffer_update(&display))?;
            }
            4 => {
                // KeyEvent: 7 bytes.
                let mut b = [0u8; 7];
                s.read_exact(&mut b)?;
            }
            5 => {
                // PointerEvent: 5 bytes.
                let mut b = [0u8; 5];
                s.read_exact(&mut b)?;
            }
            6 => {
                // ClientCutText: 3 padding + u32 length + text.
                let mut hdr = [0u8; 7];
                s.read_exact(&mut hdr)?;
                let len = u32::from_be_bytes([hdr[3], hdr[4], hdr[5], hdr[6]]) as usize;
                let mut text = vec![0u8; len];
                s.read_exact(&mut text)?;
            }
            _ => break,
        }
    }
    Ok(())
}

/// ServerInit: width, height, 16-byte pixel format (32bpp true colour,
/// big-endian, R<<16 G<<8 B<<0), then the desktop name.
fn server_init(w: u16, h: u16) -> Vec<u8> {
    let mut m = Vec::with_capacity(32);
    m.extend_from_slice(&w.to_be_bytes());
    m.extend_from_slice(&h.to_be_bytes());
    m.push(32); // bits-per-pixel
    m.push(24); // depth
    m.push(1); // big-endian flag
    m.push(1); // true-colour flag
    m.extend_from_slice(&255u16.to_be_bytes()); // red-max
    m.extend_from_slice(&255u16.to_be_bytes()); // green-max
    m.extend_from_slice(&255u16.to_be_bytes()); // blue-max
    m.push(16); // red-shift
    m.push(8); // green-shift
    m.push(0); // blue-shift
    m.extend_from_slice(&[0, 0, 0]); // padding
    let name = b"RustNet";
    m.extend_from_slice(&(name.len() as u32).to_be_bytes());
    m.extend_from_slice(name);
    m
}

/// A single Raw-encoded full-frame FramebufferUpdate.
fn framebuffer_update(display: &Display) -> Vec<u8> {
    let (w, h, pixels) = {
        let g = display.lock().unwrap();
        match g.as_ref() {
            Some(f) => (f.width as u16, f.height as u16, f.pixels.clone()),
            None => (0, 0, Vec::new()),
        }
    };
    let mut m = Vec::with_capacity(12 + pixels.len() * 4);
    m.push(0); // message-type = FramebufferUpdate
    m.push(0); // padding
    m.extend_from_slice(&1u16.to_be_bytes()); // one rectangle
    m.extend_from_slice(&0u16.to_be_bytes()); // x
    m.extend_from_slice(&0u16.to_be_bytes()); // y
    m.extend_from_slice(&w.to_be_bytes());
    m.extend_from_slice(&h.to_be_bytes());
    m.extend_from_slice(&0i32.to_be_bytes()); // encoding = Raw
    for px in pixels {
        let (r, g, b) = Color(px).to_rgb();
        // 32bpp big-endian value (R<<16 | G<<8 | B) -> bytes [0, R, G, B].
        m.extend_from_slice(&[0, r, g, b]);
    }
    m
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn server_init_advertises_true_colour_panel() {
        let m = server_init(32, 16);
        assert_eq!(&m[0..2], &32u16.to_be_bytes()); // width
        assert_eq!(&m[2..4], &16u16.to_be_bytes()); // height
        assert_eq!(m[4], 32); // bpp
        assert_eq!(m[5], 24); // depth
        assert_eq!(m[7], 1); // true-colour flag
        let namelen = u32::from_be_bytes([m[20], m[21], m[22], m[23]]) as usize;
        assert_eq!(&m[24..24 + namelen], b"RustNet");
    }

    #[test]
    fn framebuffer_update_encodes_raw_pixels() {
        let mut fb = Framebuffer::new(2, 1);
        fb.set_pixel(0, 0, Color::RED);
        fb.set_pixel(1, 0, Color::GREEN);
        let display: Display = Arc::new(Mutex::new(Some(fb)));
        let m = framebuffer_update(&display);
        assert_eq!(m[0], 0); // FramebufferUpdate
        assert_eq!(&m[2..4], &1u16.to_be_bytes()); // one rect
        assert_eq!(&m[8..10], &2u16.to_be_bytes()); // rect width
        assert_eq!(&m[10..12], &1u16.to_be_bytes()); // rect height
        assert_eq!(&m[12..16], &0i32.to_be_bytes()); // Raw encoding
        // Two pixels, [0,R,G,B] each: red then green.
        let px0 = &m[16..20];
        let px1 = &m[20..24];
        assert!(px0[1] > 200 && px0[2] < 40, "first pixel red: {px0:?}");
        assert!(px1[2] > 200 && px1[1] < 40, "second pixel green: {px1:?}");
    }
}
