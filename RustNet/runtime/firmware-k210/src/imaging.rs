//! The camera, from the firmware's side: buffers, and one frame at a time.
//!
//! [`rustnet_hal_k210::camera`] knows how to configure the sensor and drive the
//! DVP block. What it deliberately does not do is own memory — the HAL crate
//! has no allocator and is host-testable precisely because it does not. So the
//! buffers live here, and with them the two things that make a DVP buffer
//! unlike an ordinary `Vec`.
//!
//! **It must be reachable past the cache.** The DVP is an AXI master writing
//! straight into SRAM and knows nothing about the CPU's data cache, so a frame
//! written through a cached address is invisible to a CPU reading the same
//! address. Every buffer here is handed to the block, and read back, through
//! the uncached alias — see [`rustnet_hal_k210::camera::uncached`].
//!
//! **The camera and the panel share their data pins, and that turns out not
//! to matter.** The eight DVP data lines are the eight the LCD is driven
//! through, and `sysctl.misc.spi_dvp_data_enable` is what routes SPI0's data
//! onto them — so the obvious design is to take the pins for a capture and
//! give them back for a blit. That design produces a frame of uniform
//! `0x0420`, which is exactly what the block's YUV-to-RGB conversion makes of
//! data lines reading zero. The DVP needs the bit *set*. MaixPy never touches
//! it — its LCD driver has both calls commented out — and captures fine, which
//! is the clue that settled it.

use alloc::alloc::{alloc_zeroed, dealloc, Layout};
use alloc::format;
use alloc::string::String;

use rustnet_hal::i2c::I2cBus;
use rustnet_hal_k210::camera::{self, Dvp};
use rustnet_hal_k210::CycleDelay;

/// What the block writes a burst with. Over-aligning costs a few bytes once
/// and removes a whole class of "the first row is fine and the rest is torn".
const DMA_ALIGN: usize = 64;

/// Frames to throw away after configuring the sensor, before anyone is handed
/// one.
///
/// The OV2640's automatic exposure and white balance converge over frames, not
/// over milliseconds, and there is no register that says "ready". Measured on
/// this board: frame one arrives with most of the buffer still untouched,
/// frame six is a complete image with a heavy green cast, and by frame forty
/// the cast is gone. Forty frames is a little over a second — the same order
/// as the reset delays the sensor already mandates — and it is the difference
/// between `Camera.Capture` returning a photograph and returning the sensor's
/// first guess at one.
const SETTLE_FRAMES: u32 = 40;

/// A buffer the DVP writes into.
///
/// Allocated raw rather than as a `Vec` because it is never read through the
/// pointer it was allocated at: [`Self::bytes`] reads the uncached alias, and
/// a `Vec` whose contents change behind the compiler's back is a worse lie
/// than a raw pointer that is documented to.
struct DmaBuffer {
    ptr: *mut u8,
    layout: Layout,
}

impl DmaBuffer {
    fn new(len: usize) -> Result<Self, String> {
        let layout = Layout::from_size_align(len, DMA_ALIGN)
            .map_err(|_| String::from("camera buffer size is not a valid layout"))?;
        // SAFETY: `len` is non-zero for every caller here (a frame has pixels),
        // and the layout was just validated.
        let ptr = unsafe { alloc_zeroed(layout) };
        if ptr.is_null() {
            return Err(format!("out of memory for a {len}-byte camera buffer"));
        }
        Ok(Self { ptr, layout })
    }

    /// The address to hand the DVP: this buffer, past the cache.
    fn dvp_addr(&self) -> u32 {
        camera::uncached(self.ptr as usize as u32)
    }

    /// The buffer's contents, read through the alias the DVP wrote them to.
    fn bytes(&self) -> &[u8] {
        // SAFETY: the uncached alias covers the same physical SRAM as `ptr`
        // for the same length, and this borrows for no longer than the buffer
        // itself lives.
        unsafe { core::slice::from_raw_parts(self.dvp_addr() as *const u8, self.layout.size()) }
    }
}

impl Drop for DmaBuffer {
    fn drop(&mut self) {
        // SAFETY: allocated by this type with exactly this layout, and freed
        // once.
        unsafe { dealloc(self.ptr, self.layout) }
    }
}

/// A configured sensor and the memory one frame lands in.
pub(crate) struct Sensor {
    dvp: Dvp,
    width: u16,
    height: u16,
    /// RGB565, ready to blit.
    rgb: DmaBuffer,
    /// The same frame as three planes of 8-bit R, G and B — what the KPU
    /// wants. Allocated even though nothing here reads it yet, because the
    /// block is configured the way MaixPy configures it, and MaixPy enables
    /// both outputs. A block with an output enabled and no buffer behind it
    /// writes to address zero.
    _ai: DmaBuffer,
}

impl Sensor {
    /// Bring up the sensor at `width` x `height` and arm the capture path.
    ///
    /// The DVP block has to be clocked and out of reset *before* the sensor
    /// will answer at all — `XCLK` comes from the block, and the sensor's own
    /// logic, including the part that acknowledges an I²C address, runs off
    /// it.
    pub(crate) fn open(
        bus: &mut dyn I2cBus,
        cpu_hz: u32,
        width: u16,
        height: u16,
    ) -> Result<Self, String> {
        Self::open_with_settle(bus, cpu_hz, width, height, SETTLE_FRAMES)
    }

    /// As [`Self::open`], but with the settling budget named.
    ///
    /// The boot self-test wants to know the path works, not to take a good
    /// photograph, and a full settle would put a second and a half into every
    /// boot for a frame nobody looks at.
    pub(crate) fn open_with_settle(
        bus: &mut dyn I2cBus,
        cpu_hz: u32,
        width: u16,
        height: u16,
        settle: u32,
    ) -> Result<Self, String> {
        if width == 0 || height == 0 || width % 8 != 0 {
            return Err(format!(
                "camera frame must be a positive size with a width in whole bursts of 8, got {width}x{height}"
            ));
        }

        let mut dvp = Dvp::new(camera::MAIX_CAMERA, cpu_hz);
        dvp.init();

        let _ = bus.set_frequency(100_000);
        let mut delay = CycleDelay::new(cpu_hz);
        camera::configure(bus, camera::OV2640_ADDRESS, width, height, &mut delay)
            .map_err(|e| format!("camera would not configure: {e}"))?;

        let pixels = width as usize * height as usize;
        let rgb = DmaBuffer::new(pixels * 2)?;
        let ai = DmaBuffer::new(pixels * 3)?;
        let plane = pixels as u32;
        let base = ai.dvp_addr();
        dvp.configure(
            width,
            height,
            rgb.dvp_addr(),
            [base, base + plane, base + plane * 2],
        );

        let mut sensor = Self { dvp, width, height, rgb, _ai: ai };
        for _ in 0..settle {
            // Errors here are not fatal: a sensor that cannot produce a
            // settling frame will fail the same way at the first real capture,
            // and reporting it there gives the caller something to act on.
            let _ = sensor.capture();
        }
        Ok(sensor)
    }

    pub(crate) fn width(&self) -> u16 {
        self.width
    }

    pub(crate) fn height(&self) -> u16 {
        self.height
    }

    /// Capture one frame, borrowing the shared data pins for the duration.
    ///
    /// The pins go back to the panel even if the capture times out. Leaving
    /// them with the camera would mean a failed photograph also blanks the
    /// screen, which reads as a crash rather than as one bad frame.
    pub(crate) fn capture(&mut self) -> Result<&[u8], String> {
        // Counter-intuitively, the routing to capture through is the panel's.
        // `spi_dvp_data_enable` reads like "SPI0 owns these pads", and turning
        // it off for a capture gives a frame of uniform 0x0420 — which is what
        // the block's YUV-to-RGB conversion produces from data lines that are
        // all zero. MaixPy never touches the bit at all: its LCD driver has
        // both calls commented out, and it captures with the bit set. So does
        // this. The camera and the panel do not have to alternate after all.
        camera::return_pins_to_panel();
        let outcome = self.dvp.capture();
        outcome.map_err(|e| format!("camera capture failed: {e}"))?;
        Ok(self.rgb.bytes())
    }
}

/// What a frame looks like, in one line, for a check that does not need eyes.
///
/// A camera that is wired but not capturing hands back a buffer that is
/// entirely one value; a photograph of anything at all does not. But "not
/// uniform" is a low bar, and this port has already been fooled once by a
/// frame that varied and was still wrong. So the summary also says *where*
/// the content is: a frame whose non-zero rows stop a third of the way down
/// is a size or burst-count mistake, not a dark room, and no amount of
/// looking at a screen distinguishes those as quickly as two numbers do.
pub(crate) fn describe(frame: &[u8], width: u16) -> String {
    let stride = width as usize * 2;
    let mut low = u16::MAX;
    let mut high = 0u16;
    let mut total = 0u64;
    let mut nonzero = 0u32;
    let mut first_row = None;
    let mut last_row = 0usize;

    for (row, line) in frame.chunks(stride.max(1)).enumerate() {
        let mut row_has_content = false;
        for pair in line.chunks_exact(2) {
            let pixel = u16::from_le_bytes([pair[0], pair[1]]);
            low = low.min(pixel);
            high = high.max(pixel);
            total += pixel as u64;
            if pixel != 0 {
                nonzero += 1;
                row_has_content = true;
            }
        }
        if row_has_content {
            first_row.get_or_insert(row);
            last_row = row;
        }
    }

    let pixels = (frame.len() / 2).max(1) as u64;
    let rows = match first_row {
        Some(first) => format!("rows {first}..={last_row}"),
        None => String::from("no non-zero row"),
    };
    format!(
        "min {low:#06x} max {high:#06x} mean {:#06x}, {}% lit, {rows}",
        (total / pixels) as u16,
        nonzero as u64 * 100 / pixels
    )
}

/// How much two frames of the same scene differ.
///
/// The point is noise. A live sensor never delivers the same frame twice —
/// even pointed at a blank wall, the bottom bits move. A data bus that is
/// stuck, or a buffer nothing is writing to, delivers a byte-identical repeat.
/// That distinction is what separates "capturing a dark room" from "not
/// capturing", and it is the one a still image cannot answer.
pub(crate) fn difference(a: &[u8], b: &[u8]) -> u32 {
    a.iter().zip(b).filter(|(x, y)| x != y).count() as u32
}

/// Every `step`th pixel of every `step`th row, as hex, for a look at the frame
/// over a serial line.
///
/// Kept for bring-up rather than called at boot: sixty lines of hex on every
/// start would drown the rest of the banner. Wire it into [`probe_camera`] when
/// the image path is in question — `scratchpad/thumb.py` in this port's notes
/// turns the output back into a PNG.
///
/// [`probe_camera`]: crate::probe_camera
///
/// Statistics say whether a frame is plausible; they do not say whether it is
/// a picture of the room. A thumbnail small enough to fit down the console —
/// an eightieth of the pixels — settles that without needing anyone to watch a
/// screen and describe what they see, which is a slow and unreliable way to
/// debug an image path.
#[allow(dead_code)]
pub(crate) fn thumbnail(frame: &[u8], width: u16, height: u16, step: usize) -> String {
    let stride = width as usize * 2;
    let mut out = String::new();
    let mut row = 0usize;
    while row < height as usize {
        let line = &frame[(row * stride).min(frame.len())..][..stride.min(frame.len())];
        let mut col = 0usize;
        while col < width as usize {
            let at = col * 2;
            let pixel = u16::from_le_bytes([line[at], line[at + 1]]);
            let _ = core::fmt::Write::write_fmt(&mut out, format_args!("{pixel:04x}"));
            col += step;
        }
        out.push('\n');
        row += step;
    }
    out
}
