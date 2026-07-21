//! The `--stdio` transport serves RNDP over a raw byte pipe — the same
//! shape a USB-CDC/UART transport has on real silicon. This drives the
//! compiled firmware binary through process pipes.

use rustnet_firmware::proto::{Frame, CMD_INFO, CMD_PING, PROTOCOL_VERSION};
use std::io::{Read, Write};
use std::process::{Command, Stdio};

fn read_frame(pipe: &mut impl Read, buf: &mut Vec<u8>) -> Frame {
    let mut chunk = [0u8; 4096];
    loop {
        if let Ok(Some((frame, used))) = Frame::decode(buf) {
            buf.drain(..used);
            return frame;
        }
        let n = pipe.read(&mut chunk).expect("firmware closed the pipe");
        assert!(n > 0, "firmware closed the pipe before responding");
        buf.extend_from_slice(&chunk[..n]);
    }
}

#[test]
fn rndp_over_stdio_pipe() {
    let mut child = Command::new(env!("CARGO_BIN_EXE_rustnet-firmware"))
        .args(["--stdio", "--ephemeral"])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .expect("spawn firmware");
    let mut stdin = child.stdin.take().unwrap();
    let mut stdout = child.stdout.take().unwrap();
    let mut buf = Vec::new();

    // PING → protocol version.
    stdin.write_all(&Frame::new(CMD_PING, vec![]).encode()).unwrap();
    stdin.flush().unwrap();
    let pong = read_frame(&mut stdout, &mut buf);
    assert_eq!(pong.payload, vec![PROTOCOL_VERSION]);

    // INFO → device JSON with the chip name.
    stdin.write_all(&Frame::new(CMD_INFO, vec![]).encode()).unwrap();
    stdin.flush().unwrap();
    let info = read_frame(&mut stdout, &mut buf);
    let text = String::from_utf8_lossy(&info.payload).to_string();
    assert!(text.contains("host-sim"), "unexpected info: {text}");

    // Closing stdin ends the session; the process exits cleanly.
    drop(stdin);
    let status = child.wait().expect("firmware wait");
    assert!(status.success(), "firmware exited with {status:?}");
}
