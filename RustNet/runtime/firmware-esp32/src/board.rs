//! ESP32 board over ESP-IDF: GPIO/delay/RTC live via IDF calls, the rest
//! fail fast naming their esp-idf-hal integration points.

use core::ffi::c_void;
use esp_idf_svc::sys;
use rustnet_hal::gpio::{Edge, GpioPin, Level, PinMode};
use rustnet_hal::power::{BatteryStatus, PowerManager, SleepMode, WakeReason, WakeSource};
use rustnet_hal::rtc::{DateTime, Rtc};
use rustnet_hal::watchdog::Watchdog;
use rustnet_hal::{delay::Delay, Board, HalError, HalResult};
use std::sync::Once;

const PIN_COUNT: u32 = 40; // GPIO0..=39 (34..39 input-only)

/// The per-pin GPIO ISR service is process-global and must be installed
/// exactly once (flags=0 → handlers may live in flash, no IRAM constraint).
static GPIO_ISR_SERVICE: Once = Once::new();

/// Context handed to the GPIO edge ISR. Leaked for the app's lifetime so the
/// pointer stays valid; the ISR only touches the opaque FreeRTOS queue handle.
struct EdgeIsrCtx {
    pin: i32,
    queue: sys::QueueHandle_t,
}

/// GPIO edge ISR: sample the level and hand it to the dispatch thread. Heap
/// allocation and logging are illegal here, so we only do a register read and
/// a from-ISR queue send.
unsafe extern "C" fn edge_isr(arg: *mut c_void) {
    let ctx = &*(arg as *const EdgeIsrCtx);
    let level: u32 = sys::gpio_get_level(ctx.pin) as u32;
    let mut hp: sys::BaseType_t = 0;
    sys::xQueueGenericSendFromISR(
        ctx.queue,
        &level as *const u32 as *const c_void,
        &mut hp,
        0, // queueSEND_TO_BACK
    );
}

/// RMT RX "receive done" callback: runs in ISR context, forwards the captured
/// symbol count to the waiting `capture` call over a queue.
unsafe extern "C" fn rmt_rx_done(
    _chan: sys::rmt_channel_handle_t,
    edata: *const sys::rmt_rx_done_event_data_t,
    user: *mut c_void,
) -> bool {
    let queue = user as sys::QueueHandle_t;
    let count = (*edata).num_symbols as u32;
    let mut hp: sys::BaseType_t = 0;
    sys::xQueueGenericSendFromISR(queue, &count as *const u32 as *const c_void, &mut hp, 0);
    hp != 0
}

pub struct IdfPin {
    pin: i32,
}

impl GpioPin for IdfPin {
    fn set_mode(&mut self, mode: PinMode) -> HalResult<()> {
        let idf_mode = match mode {
            PinMode::Output => sys::gpio_mode_t_GPIO_MODE_INPUT_OUTPUT,
            PinMode::OutputOpenDrain => sys::gpio_mode_t_GPIO_MODE_INPUT_OUTPUT_OD,
            _ => sys::gpio_mode_t_GPIO_MODE_INPUT,
        };
        unsafe {
            sys::gpio_reset_pin(self.pin);
            sys::gpio_set_direction(self.pin, idf_mode);
            match mode {
                PinMode::InputPullUp => {
                    sys::gpio_set_pull_mode(self.pin, sys::gpio_pull_mode_t_GPIO_PULLUP_ONLY);
                }
                PinMode::InputPullDown => {
                    sys::gpio_set_pull_mode(self.pin, sys::gpio_pull_mode_t_GPIO_PULLDOWN_ONLY);
                }
                _ => {}
            }
        }
        Ok(())
    }

    fn write(&mut self, level: Level) -> HalResult<()> {
        unsafe { sys::gpio_set_level(self.pin, (level == Level::High) as u32) };
        Ok(())
    }

    fn read(&mut self) -> HalResult<Level> {
        let v = unsafe { sys::gpio_get_level(self.pin) };
        Ok(if v != 0 { Level::High } else { Level::Low })
    }

    fn toggle(&mut self) -> HalResult<()> {
        let level = self.read()?;
        self.write(if level == Level::High { Level::Low } else { Level::High })
    }

    fn on_edge(
        &mut self,
        edge: Edge,
        callback: Box<dyn FnMut(Level) + Send>,
    ) -> HalResult<()> {
        GPIO_ISR_SERVICE.call_once(|| unsafe {
            sys::gpio_install_isr_service(0);
        });
        let intr = match edge {
            Edge::Rising => sys::gpio_int_type_t_GPIO_INTR_POSEDGE,
            Edge::Falling => sys::gpio_int_type_t_GPIO_INTR_NEGEDGE,
            Edge::Both => sys::gpio_int_type_t_GPIO_INTR_ANYEDGE,
        };
        // Queue of sampled levels (u32) from the ISR to the dispatch thread.
        let queue = unsafe { sys::xQueueGenericCreate(8, 4, 0 /* queueQUEUE_TYPE_BASE */) };
        if queue.is_null() {
            return Err(HalError::Bus("gpio isr queue alloc failed"));
        }
        let ctx = Box::into_raw(Box::new(EdgeIsrCtx { pin: self.pin, queue }));
        unsafe {
            sys::gpio_set_intr_type(self.pin, intr);
            if sys::gpio_isr_handler_add(self.pin, Some(edge_isr), ctx as *mut c_void) != 0 {
                drop(Box::from_raw(ctx));
                sys::vQueueDelete(queue);
                return Err(HalError::Bus("gpio isr handler add failed"));
            }
            sys::gpio_intr_enable(self.pin);
        }
        // The boxed callback may allocate/log, so it runs on a normal task, not
        // in ISR context. This thread lives for the app's lifetime. The queue
        // handle is a raw pointer (!Send), so cross the thread boundary as a
        // usize and rebuild it inside.
        let queue_addr = queue as usize;
        std::thread::Builder::new()
            .stack_size(4096)
            .spawn(move || {
                let queue = queue_addr as sys::QueueHandle_t;
                let mut cb = callback;
                loop {
                    let mut level: u32 = 0;
                    let got = unsafe {
                        sys::xQueueReceive(
                            queue,
                            &mut level as *mut u32 as *mut c_void,
                            u32::MAX, // portMAX_DELAY (block forever)
                        )
                    };
                    if got != 0 {
                        cb(if level != 0 { Level::High } else { Level::Low });
                    }
                }
            })
            .map_err(|_| HalError::Bus("gpio isr thread spawn failed"))?;
        Ok(())
    }

    fn clear_interrupt(&mut self) -> HalResult<()> {
        Ok(())
    }
}

pub struct IdfDelay;

impl Delay for IdfDelay {
    fn delay_us(&mut self, us: u64) {
        if us >= 1000 {
            std::thread::sleep(std::time::Duration::from_micros(us));
        } else {
            unsafe { sys::esp_rom_delay_us(us as u32) };
        }
    }

    fn now_us(&self) -> u64 {
        unsafe { sys::esp_timer_get_time() as u64 }
    }
}

/// RTC over the IDF system clock (settable; SNTP later).
pub struct IdfRtc {
    offset: i64, // epoch seconds - boot seconds
    alarm: Option<u64>,
}

impl IdfRtc {
    fn boot_secs() -> i64 {
        // System clock: SNTP (started after WiFi) sets real UTC time here.
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_secs() as i64)
            .unwrap_or(0)
    }
}

impl Rtc for IdfRtc {
    fn now(&mut self) -> HalResult<DateTime> {
        Ok(DateTime::from_epoch((Self::boot_secs() + self.offset).max(0) as u64))
    }
    fn set(&mut self, dt: DateTime) -> HalResult<()> {
        self.offset = dt.to_epoch() as i64 - Self::boot_secs();
        Ok(())
    }
    fn set_alarm(&mut self, epoch: u64) -> HalResult<()> {
        self.alarm = Some(epoch);
        Ok(())
    }
    fn clear_alarm(&mut self) -> HalResult<()> {
        self.alarm = None;
        Ok(())
    }
    fn alarm(&self) -> Option<u64> {
        self.alarm
    }
}

pub struct IdfPower;

impl PowerManager for IdfPower {
    fn sleep(&mut self, _mode: SleepMode, duration_ms: Option<u64>) -> HalResult<()> {
        if let Some(ms) = duration_ms {
            // Light sleep with a timer wake — the practical default.
            unsafe {
                sys::esp_sleep_enable_timer_wakeup(ms * 1000);
                sys::esp_light_sleep_start();
            }
            return Ok(());
        }
        Err(HalError::InvalidArgument("sleep needs a duration on ESP32"))
    }
    fn battery(&mut self) -> HalResult<BatteryStatus> {
        Err(HalError::NotSupported)
    }
    fn cpu_frequency_hz(&self) -> u32 {
        240_000_000
    }
    fn set_cpu_frequency_hz(&mut self, _hz: u32) -> HalResult<()> {
        Err(HalError::NotSupported)
    }
    fn reset(&mut self) -> ! {
        unsafe { sys::esp_restart() };
        unreachable!()
    }
    fn shutdown(&mut self) -> ! {
        unsafe {
            sys::esp_deep_sleep_start();
        }
        unreachable!()
    }
    fn arm_wake(&mut self, source: WakeSource) -> HalResult<()> {
        match source {
            WakeSource::Rtc { seconds } => {
                unsafe { sys::esp_sleep_enable_timer_wakeup(seconds * 1_000_000) };
                Ok(())
            }
            WakeSource::Gpio { .. } => Err(HalError::NotSupported), // ext0/ext1 later
        }
    }
    fn clear_wake_sources(&mut self) {
        unsafe { sys::esp_sleep_disable_wakeup_source(sys::esp_sleep_source_t_ESP_SLEEP_WAKEUP_ALL) };
    }
    fn wake_reason(&self) -> WakeReason {
        match unsafe { sys::esp_sleep_get_wakeup_cause() } {
            sys::esp_sleep_source_t_ESP_SLEEP_WAKEUP_TIMER => WakeReason::RtcAlarm,
            sys::esp_sleep_source_t_ESP_SLEEP_WAKEUP_EXT0
            | sys::esp_sleep_source_t_ESP_SLEEP_WAKEUP_EXT1 => WakeReason::GpioPin(0),
            _ => WakeReason::PowerOn,
        }
    }
}

pub struct IdfWatchdog {
    running: bool,
    timeout_ms: u32,
}

impl Watchdog for IdfWatchdog {
    fn start(&mut self, timeout_ms: u32) -> HalResult<()> {
        let config = sys::esp_task_wdt_config_t {
            timeout_ms,
            idle_core_mask: 0,
            trigger_panic: true,
        };
        let err = unsafe { sys::esp_task_wdt_init(&config) };
        if err != 0 {
            unsafe { sys::esp_task_wdt_reconfigure(&config) };
        }
        unsafe { sys::esp_task_wdt_add(core::ptr::null_mut()) };
        self.running = true;
        self.timeout_ms = timeout_ms;
        Ok(())
    }
    fn feed(&mut self) -> HalResult<()> {
        if !self.running {
            return Err(HalError::InvalidArgument("watchdog not started"));
        }
        unsafe { sys::esp_task_wdt_reset() };
        Ok(())
    }
    fn stop(&mut self) -> HalResult<()> {
        unsafe {
            sys::esp_task_wdt_delete(core::ptr::null_mut());
            sys::esp_task_wdt_deinit();
        }
        self.running = false;
        Ok(())
    }
    fn is_running(&self) -> bool {
        self.running
    }
    fn timeout_ms(&self) -> u32 {
        self.timeout_ms
    }
}

/// UART driver ports: 1 → TX GPIO4 / RX GPIO5, 2 → TX GPIO17 / RX GPIO16.
pub struct IdfUart {
    port: u32,
    tx: i32,
    rx: i32,
    installed: bool,
}

impl IdfUart {
    fn ensure(&mut self) -> HalResult<()> {
        if self.installed {
            return Ok(());
        }
        unsafe {
            sys::uart_driver_install(self.port, 2048, 2048, 0, std::ptr::null_mut(), 0);
            sys::uart_set_pin(self.port, self.tx, self.rx, -1, -1);
        }
        self.installed = true;
        Ok(())
    }
}

impl rustnet_hal::uart::Uart for IdfUart {
    fn configure(&mut self, config: rustnet_hal::uart::UartConfig) -> HalResult<()> {
        self.ensure()?;
        let cfg = sys::uart_config_t {
            baud_rate: config.baud as i32,
            data_bits: sys::uart_word_length_t_UART_DATA_8_BITS,
            parity: match config.parity {
                rustnet_hal::uart::Parity::None => sys::uart_parity_t_UART_PARITY_DISABLE,
                rustnet_hal::uart::Parity::Even => sys::uart_parity_t_UART_PARITY_EVEN,
                rustnet_hal::uart::Parity::Odd => sys::uart_parity_t_UART_PARITY_ODD,
            },
            stop_bits: if config.stop_bits == 2 {
                sys::uart_stop_bits_t_UART_STOP_BITS_2
            } else {
                sys::uart_stop_bits_t_UART_STOP_BITS_1
            },
            flow_ctrl: sys::uart_hw_flowcontrol_t_UART_HW_FLOWCTRL_DISABLE,
            ..Default::default()
        };
        if unsafe { sys::uart_param_config(self.port, &cfg) } != 0 {
            return Err(HalError::Bus("uart param config failed"));
        }
        Ok(())
    }
    fn write(&mut self, data: &[u8]) -> HalResult<usize> {
        self.ensure()?;
        let n = unsafe {
            sys::uart_write_bytes(
                self.port,
                data.as_ptr() as *const core::ffi::c_void,
                data.len(),
            )
        };
        if n < 0 {
            return Err(HalError::Bus("uart write failed"));
        }
        Ok(n as usize)
    }
    fn read(&mut self, buf: &mut [u8]) -> HalResult<usize> {
        self.ensure()?;
        let n = unsafe {
            sys::uart_read_bytes(
                self.port,
                buf.as_mut_ptr() as *mut core::ffi::c_void,
                buf.len() as u32,
                1,
            )
        };
        Ok(n.max(0) as usize)
    }
    fn bytes_available(&mut self) -> HalResult<usize> {
        self.ensure()?;
        let mut len: usize = 0;
        unsafe { sys::uart_get_buffered_data_len(self.port, &mut len) };
        Ok(len)
    }
    fn flush(&mut self) -> HalResult<()> {
        unsafe { sys::uart_wait_tx_done(self.port, 100) };
        Ok(())
    }
}

/// TWAI-backed CAN. Loopback config = NO_ACK mode with TX and RX on the
/// same pad (GPIO5) and self-reception frames — a true on-chip round
/// trip without a transceiver. Normal mode: TX GPIO5 / RX GPIO35 into a
/// CAN transceiver.
pub struct IdfCan {
    installed: bool,
    loopback: bool,
}

impl IdfCan {
    fn timing(bitrate: u32) -> sys::twai_timing_config_t {
        // 80 MHz APB, 20 time quanta per bit.
        let brp = match bitrate {
            1_000_000 => 4,
            250_000 => 16,
            125_000 => 32,
            _ => 8, // 500 kbit/s default
        };
        sys::twai_timing_config_t {
            brp,
            tseg_1: 15,
            tseg_2: 4,
            sjw: 3,
            triple_sampling: false,
            ..Default::default()
        }
    }
}

impl rustnet_hal::can::CanBus for IdfCan {
    fn configure(&mut self, config: rustnet_hal::can::CanConfig) -> HalResult<()> {
        unsafe {
            if self.installed {
                sys::twai_stop();
                sys::twai_driver_uninstall();
                self.installed = false;
            }
            let (tx, rx, mode) = if config.loopback {
                (5, 5, sys::twai_mode_t_TWAI_MODE_NO_ACK)
            } else if config.silent {
                (5, 35, sys::twai_mode_t_TWAI_MODE_LISTEN_ONLY)
            } else {
                (5, 35, sys::twai_mode_t_TWAI_MODE_NORMAL)
            };
            let g = sys::twai_general_config_t {
                mode,
                tx_io: tx,
                rx_io: rx,
                clkout_io: -1,
                bus_off_io: -1,
                tx_queue_len: 8,
                rx_queue_len: 8,
                alerts_enabled: 0,
                clkout_divider: 0,
                intr_flags: 0,
                ..Default::default()
            };
            let t = Self::timing(config.bitrate);
            let f = sys::twai_filter_config_t {
                acceptance_code: 0,
                acceptance_mask: 0xFFFF_FFFF,
                single_filter: true,
            };
            if sys::twai_driver_install(&g, &t, &f) != 0 {
                return Err(HalError::Bus("twai install failed"));
            }
            if sys::twai_start() != 0 {
                return Err(HalError::Bus("twai start failed"));
            }
        }
        self.installed = true;
        self.loopback = config.loopback;
        Ok(())
    }
    fn transmit(&mut self, frame: &rustnet_hal::can::CanFrame) -> HalResult<()> {
        if !self.installed {
            return Err(HalError::InvalidArgument("CAN not configured"));
        }
        let mut msg: sys::twai_message_t = unsafe { std::mem::zeroed() };
        msg.identifier = frame.id;
        msg.data_length_code = frame.data.len().min(8) as u8;
        msg.data[..frame.data.len().min(8)].copy_from_slice(&frame.data[..frame.data.len().min(8)]);
        let mut flags = 0u32;
        if frame.extended {
            flags |= sys::TWAI_MSG_FLAG_EXTD;
        }
        if frame.rtr {
            flags |= sys::TWAI_MSG_FLAG_RTR;
        }
        if self.loopback {
            flags |= sys::TWAI_MSG_FLAG_SELF; // self-reception request
        }
        msg.__bindgen_anon_1.flags = flags;
        let err = unsafe { sys::twai_transmit(&msg, 100) };
        if err != 0 {
            return Err(HalError::Bus("twai transmit failed"));
        }
        Ok(())
    }
    fn receive(&mut self) -> HalResult<Option<rustnet_hal::can::CanFrame>> {
        if !self.installed {
            return Err(HalError::InvalidArgument("CAN not configured"));
        }
        let mut msg: sys::twai_message_t = unsafe { std::mem::zeroed() };
        let err = unsafe { sys::twai_receive(&mut msg, 5) };
        if err != 0 {
            return Ok(None); // timeout: FIFO empty
        }
        let flags = unsafe { msg.__bindgen_anon_1.flags };
        Ok(Some(rustnet_hal::can::CanFrame {
            id: msg.identifier,
            extended: flags & sys::TWAI_MSG_FLAG_EXTD != 0,
            rtr: flags & sys::TWAI_MSG_FLAG_RTR != 0,
            data: msg.data[..msg.data_length_code.min(8) as usize].to_vec(),
        }))
    }
    fn rx_pending(&self) -> usize {
        let mut status: sys::twai_status_info_t = unsafe { std::mem::zeroed() };
        unsafe { sys::twai_get_status_info(&mut status) };
        status.msgs_to_rx as usize
    }
    fn set_filter(&mut self, _id: u32, _mask: u32) -> HalResult<()> {
        // TWAI acceptance filters need a driver reinstall; accept-all for now.
        Err(HalError::NotSupported)
    }
}

/// RMT-backed signal generation at 1 µs resolution (SignalGenerator).
pub struct IdfSignal {
    gpio: i32,
    channel: sys::rmt_channel_handle_t,
    encoder: sys::rmt_encoder_handle_t,
}

unsafe impl Send for IdfSignal {}

impl IdfSignal {
    fn new(gpio: i32) -> HalResult<Self> {
        let cfg = sys::rmt_tx_channel_config_t {
            gpio_num: gpio,
            clk_src: sys::soc_periph_rmt_clk_src_t_RMT_CLK_SRC_APB,
            resolution_hz: 1_000_000,
            mem_block_symbols: 64,
            trans_queue_depth: 4,
            ..Default::default()
        };
        let mut chan: sys::rmt_channel_handle_t = std::ptr::null_mut();
        if unsafe { sys::rmt_new_tx_channel(&cfg, &mut chan) } != 0 {
            return Err(HalError::Bus("rmt tx channel failed"));
        }
        let enc_cfg = sys::rmt_copy_encoder_config_t {};
        let mut enc: sys::rmt_encoder_handle_t = std::ptr::null_mut();
        if unsafe { sys::rmt_new_copy_encoder(&enc_cfg, &mut enc) } != 0 {
            return Err(HalError::Bus("rmt encoder failed"));
        }
        unsafe { sys::rmt_enable(chan) };
        Ok(IdfSignal { gpio, channel: chan, encoder: enc })
    }
}

impl rustnet_hal::signal::SignalControl for IdfSignal {
    fn generate(&mut self, initial_high: bool, timings_us: &[u32]) -> HalResult<()> {
        let _ = self.gpio;
        // Pack timing pairs into RMT symbols (15-bit durations).
        let mut level = initial_high as u32;
        let mut halves: Vec<(u32, u32)> = Vec::new();
        for &t in timings_us {
            let mut left = t.max(1);
            while left > 0 {
                let d = left.min(0x7FFF);
                halves.push((d, level));
                left -= d;
            }
            level ^= 1;
        }
        if halves.len() % 2 != 0 {
            halves.push((1, level));
        }
        let symbols: Vec<u32> = halves
            .chunks(2)
            .map(|pair| {
                let (d0, l0) = pair[0];
                let (d1, l1) = pair[1];
                d0 | (l0 << 15) | (d1 << 16) | (l1 << 31)
            })
            .collect();
        let tx_cfg: sys::rmt_transmit_config_t = unsafe { std::mem::zeroed() };
        let err = unsafe {
            sys::rmt_transmit(
                self.channel,
                self.encoder,
                symbols.as_ptr() as *const core::ffi::c_void,
                symbols.len() * 4,
                &tx_cfg,
            )
        };
        if err != 0 {
            return Err(HalError::Bus("rmt transmit failed"));
        }
        unsafe { sys::rmt_tx_wait_all_done(self.channel, 1000) };
        Ok(())
    }
    fn capture(&mut self, max_edges: usize, timeout_us: u32) -> HalResult<Vec<u32>> {
        IdfSignal::rx_capture(self.gpio, max_edges, timeout_us)
    }
    fn pulse_feedback(&mut self, pulse_high: bool, pulse_us: u32, timeout_us: u32) -> HalResult<u32> {
        // Emit the trigger, then measure the first echo pulse on the same line.
        self.generate(pulse_high, &[pulse_us.max(1)])?;
        let edges = IdfSignal::rx_capture(self.gpio, 2, timeout_us)?;
        edges.first().copied().ok_or(HalError::Timeout)
    }
}

impl IdfSignal {
    /// Capture pulse widths (µs) on `gpio` with a transient RMT RX channel.
    /// The channel and its queue are torn down before returning so repeated
    /// captures never exhaust the shared RMT channel pool.
    fn rx_capture(gpio: i32, max_edges: usize, timeout_us: u32) -> HalResult<Vec<u32>> {
        // 1 MHz → 1 tick = 1 µs, matching the TX packing. Classic ESP32 has no
        // RMT DMA, so pin the hardware channel to one 64-symbol block; the
        // receive buffer below is sized independently to the requested capture.
        let buf_symbols = (((max_edges.max(2)) / 2) + 2).max(64).min(512);
        let mut cfg: sys::rmt_rx_channel_config_t = unsafe { std::mem::zeroed() };
        cfg.gpio_num = gpio;
        cfg.clk_src = sys::soc_periph_rmt_clk_src_t_RMT_CLK_SRC_APB;
        cfg.resolution_hz = 1_000_000;
        cfg.mem_block_symbols = 64;
        let mut chan: sys::rmt_channel_handle_t = std::ptr::null_mut();
        if unsafe { sys::rmt_new_rx_channel(&cfg, &mut chan) } != 0 {
            return Err(HalError::Bus("rmt rx channel failed"));
        }
        let queue = unsafe { sys::xQueueGenericCreate(4, 4, 0) };
        if queue.is_null() {
            unsafe { sys::rmt_del_channel(chan) };
            return Err(HalError::Bus("rmt rx queue alloc failed"));
        }
        let cbs = sys::rmt_rx_event_callbacks_t { on_recv_done: Some(rmt_rx_done) };
        let mut symbols: Vec<sys::rmt_symbol_word_t> =
            vec![unsafe { std::mem::zeroed() }; buf_symbols];
        // Idle threshold: a level held longer than this ends the frame. Clamp to
        // the 15-bit RMT counter range (≤32.767 ms at 1 µs resolution).
        let max_ns = (timeout_us.max(1) as u64 * 1000).min(32_000_000) as u32;
        let recv_cfg = sys::rmt_receive_config_t {
            signal_range_min_ns: 1_000, // treat <1 µs as glitch
            signal_range_max_ns: max_ns,
        };
        let ticks = (((timeout_us as u64) + 999) / 1000).max(1) as u32; // µs → ms ticks
        let result = unsafe {
            sys::rmt_rx_register_event_callbacks(chan, &cbs, queue as *mut c_void);
            sys::rmt_enable(chan);
            let started = sys::rmt_receive(
                chan,
                symbols.as_mut_ptr() as *mut c_void,
                symbols.len() * std::mem::size_of::<sys::rmt_symbol_word_t>(),
                &recv_cfg,
            );
            if started != 0 {
                Err(HalError::Bus("rmt receive failed"))
            } else {
                let mut count: u32 = 0;
                if sys::xQueueReceive(queue, &mut count as *mut u32 as *mut c_void, ticks) != 0 {
                    let mut out = Vec::with_capacity(count as usize * 2);
                    for s in symbols.iter().take(count as usize) {
                        let v = s.val; // union: packed durations (see TX path)
                        let d0 = v & 0x7FFF;
                        if d0 == 0 {
                            break;
                        }
                        out.push(d0);
                        if out.len() >= max_edges {
                            break;
                        }
                        let d1 = (v >> 16) & 0x7FFF;
                        if d1 == 0 {
                            break;
                        }
                        out.push(d1);
                        if out.len() >= max_edges {
                            break;
                        }
                    }
                    Ok(out)
                } else {
                    Ok(Vec::new()) // timed out with no signal on the line
                }
            }
        };
        unsafe {
            sys::rmt_disable(chan);
            sys::rmt_del_channel(chan);
            sys::vQueueDelete(queue);
        }
        result
    }
}

/// ADC1 oneshot channel (channels 0..=7 map GPIO 36/37/38/39/32/33/34/35).
pub struct IdfAdcChan {
    // SAFETY: the oneshot unit handle is only used behind the board mutex.

    unit: sys::adc_oneshot_unit_handle_t,
    channel: sys::adc_channel_t,
    configured: bool,
}

impl IdfAdcChan {
    fn ensure(&mut self) -> HalResult<()> {
        if self.configured {
            return Ok(());
        }
        let cfg = sys::adc_oneshot_chan_cfg_t {
            atten: sys::adc_atten_t_ADC_ATTEN_DB_11,
            bitwidth: sys::adc_bitwidth_t_ADC_BITWIDTH_12,
        };
        let err = unsafe { sys::adc_oneshot_config_channel(self.unit, self.channel, &cfg) };
        if err != 0 {
            return Err(HalError::Bus("adc channel config failed"));
        }
        self.configured = true;
        Ok(())
    }
}

unsafe impl Send for IdfAdcChan {}

impl rustnet_hal::adc::AdcChannel for IdfAdcChan {
    fn read_raw(&mut self) -> HalResult<u16> {
        self.ensure()?;
        let mut raw: i32 = 0;
        let err = unsafe { sys::adc_oneshot_read(self.unit, self.channel, &mut raw) };
        if err != 0 {
            return Err(HalError::Bus("adc read failed"));
        }
        Ok(raw as u16)
    }
    fn resolution_bits(&self) -> u8 {
        12
    }
}

/// LEDC-backed PWM: the RustNet "channel" number is the GPIO pin; the
/// LEDC channel is pin % 8 and all share timer 0 (one frequency).
pub struct IdfPwm {
    gpio: i32,
    duty_permyriad: u16,
    hz: u32,
    enabled: bool,
}

impl IdfPwm {
    fn apply(&mut self) -> HalResult<()> {
        let timer = sys::ledc_timer_config_t {
            speed_mode: sys::ledc_mode_t_LEDC_LOW_SPEED_MODE,
            duty_resolution: sys::ledc_timer_bit_t_LEDC_TIMER_10_BIT,
            timer_num: sys::ledc_timer_t_LEDC_TIMER_0,
            freq_hz: self.hz.max(1),
            clk_cfg: sys::soc_periph_ledc_clk_src_legacy_t_LEDC_AUTO_CLK,
            deconfigure: false,
        };
        if unsafe { sys::ledc_timer_config(&timer) } != 0 {
            return Err(HalError::Bus("ledc timer config failed"));
        }
        let duty = (self.duty_permyriad.min(10_000) as u32 * 1023) / 10_000;
        let channel = sys::ledc_channel_config_t {
            gpio_num: self.gpio,
            speed_mode: sys::ledc_mode_t_LEDC_LOW_SPEED_MODE,
            channel: (self.gpio % 8) as u32,
            intr_type: sys::ledc_intr_type_t_LEDC_INTR_DISABLE,
            timer_sel: sys::ledc_timer_t_LEDC_TIMER_0,
            duty,
            hpoint: 0,
            ..Default::default()
        };
        if unsafe { sys::ledc_channel_config(&channel) } != 0 {
            return Err(HalError::Bus("ledc channel config failed"));
        }
        Ok(())
    }
}

impl rustnet_hal::pwm::PwmChannel for IdfPwm {
    fn set_frequency(&mut self, hz: u32) -> HalResult<()> {
        self.hz = hz;
        if self.enabled {
            self.apply()?;
        }
        Ok(())
    }
    fn set_duty(&mut self, duty_permyriad: u16) -> HalResult<()> {
        self.duty_permyriad = duty_permyriad;
        if self.enabled {
            self.apply()?;
        }
        Ok(())
    }
    fn enable(&mut self) -> HalResult<()> {
        self.enabled = true;
        self.apply()
    }
    fn disable(&mut self) -> HalResult<()> {
        self.enabled = false;
        unsafe {
            sys::ledc_stop(
                sys::ledc_mode_t_LEDC_LOW_SPEED_MODE,
                (self.gpio % 8) as u32,
                0,
            );
        }
        Ok(())
    }
}

/// I2C master on the new (5.x) bus/device driver. Bus 0 = SDA 21 / SCL 22
/// (the devkit convention).
pub struct IdfI2c {
    bus: sys::i2c_master_bus_handle_t,
    hz: u32,
    devices: std::collections::HashMap<u8, sys::i2c_master_dev_handle_t>,
}

unsafe impl Send for IdfI2c {}

impl IdfI2c {
    fn new(sda: i32, scl: i32) -> HalResult<Self> {
        let mut cfg = sys::i2c_master_bus_config_t {
            i2c_port: -1,
            sda_io_num: sda,
            scl_io_num: scl,
            clk_source: sys::soc_periph_i2c_clk_src_t_I2C_CLK_SRC_DEFAULT,
            glitch_ignore_cnt: 7,
            intr_priority: 0,
            trans_queue_depth: 0,
            ..Default::default()
        };
        cfg.flags.set_enable_internal_pullup(1);
        let mut handle: sys::i2c_master_bus_handle_t = std::ptr::null_mut();
        let err = unsafe { sys::i2c_new_master_bus(&cfg, &mut handle) };
        if err != 0 {
            return Err(HalError::Bus("i2c bus init failed"));
        }
        Ok(IdfI2c { bus: handle, hz: 100_000, devices: std::collections::HashMap::new() })
    }

    fn device(&mut self, addr: u8) -> HalResult<sys::i2c_master_dev_handle_t> {
        if let Some(h) = self.devices.get(&addr) {
            return Ok(*h);
        }
        let cfg = sys::i2c_device_config_t {
            dev_addr_length: sys::i2c_addr_bit_len_t_I2C_ADDR_BIT_LEN_7,
            device_address: addr as u16,
            scl_speed_hz: self.hz,
            ..Default::default()
        };
        let mut dev: sys::i2c_master_dev_handle_t = std::ptr::null_mut();
        let err = unsafe { sys::i2c_master_bus_add_device(self.bus, &cfg, &mut dev) };
        if err != 0 {
            return Err(HalError::Bus("i2c add device failed"));
        }
        self.devices.insert(addr, dev);
        Ok(dev)
    }

    /// Write-then-read with a repeated start (no intervening STOP) — the form
    /// register reads on PMICs like the AXP192 require.
    #[cfg(feature = "board-m5tough")]
    fn write_read(&mut self, addr: u8, wr: &[u8], rd: &mut [u8]) -> HalResult<()> {
        let dev = self.device(addr)?;
        let err = unsafe {
            sys::i2c_master_transmit_receive(
                dev,
                wr.as_ptr(),
                wr.len(),
                rd.as_mut_ptr(),
                rd.len(),
                100,
            )
        };
        if err != 0 {
            return Err(HalError::Bus("i2c wr/rd NACK/timeout"));
        }
        Ok(())
    }
}

impl rustnet_hal::i2c::I2cBus for IdfI2c {
    fn set_frequency(&mut self, hz: u32) -> HalResult<()> {
        self.hz = hz;
        // Applies to devices added after this call.
        self.devices.clear();
        Ok(())
    }
    fn write(&mut self, addr: u8, data: &[u8]) -> HalResult<()> {
        let dev = self.device(addr)?;
        let err = unsafe { sys::i2c_master_transmit(dev, data.as_ptr(), data.len(), 100) };
        if err != 0 {
            return Err(HalError::Bus("i2c write NACK/timeout"));
        }
        Ok(())
    }
    fn read(&mut self, addr: u8, buf: &mut [u8]) -> HalResult<()> {
        let dev = self.device(addr)?;
        let err = unsafe { sys::i2c_master_receive(dev, buf.as_mut_ptr(), buf.len(), 100) };
        if err != 0 {
            return Err(HalError::Bus("i2c read NACK/timeout"));
        }
        Ok(())
    }
}

/// SPI master on VSPI (SPI3): SCLK 18 / MOSI 23 / MISO 19 / CS 5.
pub struct IdfSpi {
    host: sys::spi_host_device_t,
    dev: sys::spi_device_handle_t,
    hz: u32,
    mode: u8,
}

// The device handle is a raw pointer; the board serialises all HAL access.
unsafe impl Send for IdfSpi {}

impl IdfSpi {
    fn new() -> HalResult<Self> {
        let host = sys::spi_host_device_t_SPI3_HOST;
        // `spi_bus_config_t` overlaps the mosi/miso/quad pins with the octal
        // data pins in anonymous unions, so build it zeroed and set by union.
        let mut buscfg: sys::spi_bus_config_t = unsafe { std::mem::zeroed() };
        buscfg.sclk_io_num = 18;
        buscfg.max_transfer_sz = 4096;
        unsafe {
            buscfg.__bindgen_anon_1.mosi_io_num = 23;
            buscfg.__bindgen_anon_2.miso_io_num = 19;
            buscfg.__bindgen_anon_3.quadwp_io_num = -1;
            buscfg.__bindgen_anon_4.quadhd_io_num = -1;
        }
        let err =
            unsafe { sys::spi_bus_initialize(host, &buscfg, sys::spi_common_dma_t_SPI_DMA_CH_AUTO) };
        if err != 0 {
            return Err(HalError::Bus("spi bus init failed"));
        }
        let mut spi = IdfSpi { host, dev: std::ptr::null_mut(), hz: 1_000_000, mode: 0 };
        spi.add_device()?;
        Ok(spi)
    }

    fn add_device(&mut self) -> HalResult<()> {
        if !self.dev.is_null() {
            unsafe { sys::spi_bus_remove_device(self.dev) };
            self.dev = std::ptr::null_mut();
        }
        let devcfg = sys::spi_device_interface_config_t {
            clock_speed_hz: self.hz as i32,
            mode: self.mode,
            spics_io_num: 5,
            queue_size: 1,
            ..Default::default()
        };
        let err = unsafe { sys::spi_bus_add_device(self.host, &devcfg, &mut self.dev) };
        if err != 0 {
            return Err(HalError::Bus("spi add device failed"));
        }
        Ok(())
    }
}

impl rustnet_hal::spi::SpiBus for IdfSpi {
    fn configure(&mut self, hz: u32, mode: rustnet_hal::spi::SpiMode) -> HalResult<()> {
        use rustnet_hal::spi::SpiMode;
        self.hz = hz.max(1);
        self.mode = match mode {
            SpiMode::Mode0 => 0,
            SpiMode::Mode1 => 1,
            SpiMode::Mode2 => 2,
            SpiMode::Mode3 => 3,
        };
        self.add_device()
    }

    fn transfer(&mut self, tx: &[u8], rx: &mut [u8]) -> HalResult<()> {
        let n = tx.len().max(rx.len());
        if n == 0 {
            return Ok(());
        }
        let mut trans: sys::spi_transaction_t = unsafe { std::mem::zeroed() };
        trans.length = n * 8; // bits shifted out
        trans.rxlength = rx.len() * 8;
        unsafe {
            trans.__bindgen_anon_1.tx_buffer =
                if tx.is_empty() { std::ptr::null() } else { tx.as_ptr() as *const std::ffi::c_void };
            trans.__bindgen_anon_2.rx_buffer =
                if rx.is_empty() { std::ptr::null_mut() } else { rx.as_mut_ptr() as *mut std::ffi::c_void };
        }
        let err = unsafe { sys::spi_device_transmit(self.dev, &mut trans) };
        if err != 0 {
            return Err(HalError::Bus("spi transfer failed"));
        }
        Ok(())
    }
}

/// The WROOM-32 board on ESP-IDF. Live: GPIO, delay, RTC, light-sleep
/// power, task watchdog, reset/shutdown, ADC1 oneshot, LEDC PWM
/// (channel = GPIO), I2C master (bus 0: SDA 21 / SCL 22), SPI master (VSPI).
/// Named integration points: CAN via TWAI, WiFi netif via
/// esp-idf-svc, signal via RMT.
// ===========================================================================
// M5Stack Tough panel bring-up (feature `board-m5tough`)
//
// Display : ILI9342C 320x240 on SPI2 (SCLK 18 / MOSI 23 / CS 5 / DC 15),
//           manual CS+DC so a full frame streams under one CS assertion.
// Power   : AXP192 PMIC (I2C 0x34 on SDA 21 / SCL 22) gates LCD power, the
//           backlight rail and the panel reset — the screen stays dark until
//           it is programmed, so this runs before the first flush.
// ===========================================================================

#[cfg(feature = "board-m5tough")]
const AXP192_ADDR: u8 = 0x34;

/// Bring the AXP192 up for the M5 Tough LCD. Follows the battle-tested
/// M5Core2 sequence (Tough is the same PMIC family) and additionally powers
/// LDO2 so either candidate backlight rail is live. Register 0x12 is
/// read-modify-written so DCDC1 (the ESP32's own 3.3 V) is never cleared.
#[cfg(feature = "board-m5tough")]
fn m5_axp192_init(i2c: &mut IdfI2c) -> HalResult<()> {
    use rustnet_hal::i2c::I2cBus as _;
    use std::thread::sleep;
    use std::time::Duration;

    let rd = |i2c: &mut IdfI2c, reg: u8| -> HalResult<u8> {
        let mut b = [0u8; 1];
        i2c.write_read(AXP192_ADDR, &[reg], &mut b)?;
        Ok(b[0])
    };
    let wr = |i2c: &mut IdfI2c, reg: u8, val: u8| -> HalResult<()> {
        i2c.write(AXP192_ADDR, &[reg, val])
    };

    // VBUS current-limit path: keep bit2, set bit1.
    let v = rd(i2c, 0x30)?;
    wr(i2c, 0x30, (v & 0x04) | 0x02)?;
    // Battery-charge control (M5 default) + enable all ADC channels.
    let v = rd(i2c, 0x35)?;
    wr(i2c, 0x35, (v & 0x1C) | 0xA2)?;
    wr(i2c, 0x82, 0xFF)?;
    // Reg 0x28: LDO2 = 3.3 V (upper nibble — the LCD BACKLIGHT rail, what
    // ScreenBreath drives), LDO3 = 3.0 V (lower nibble). 0xF<<4 | 0xC = 0xFC.
    wr(i2c, 0x28, 0xFC)?;
    // DCDC3 = 3.0 V as well, in case this unit routes the backlight there.
    let v = rd(i2c, 0x27)?;
    wr(i2c, 0x27, (v & 0x80) | 0x5C)?;
    // GPIO1/GPIO2 → open-drain output (low 3 bits = 0).
    let v = rd(i2c, 0x92)?;
    wr(i2c, 0x92, v & 0xF8)?;
    let v = rd(i2c, 0x93)?;
    wr(i2c, 0x93, v & 0xF8)?;
    // GPIO4 → output (M5 magic value; drives the LCD reset line).
    let v = rd(i2c, 0x95)?;
    wr(i2c, 0x95, (v & 0x72) | 0x84)?;
    // Power-output enable (reg 0x12): OR in DCDC3(bit1), LDO2(bit2 — backlight),
    // LDO3(bit3), EXTEN(bit6); preserve DCDC1(bit0) so the ESP32 keeps power.
    let v = rd(i2c, 0x12)?;
    wr(i2c, 0x12, v | 0x4E)?;
    // LCD reset pulse on GPIO3/GPIO4 (reg 0x96): low → high. Driving both bits
    // high covers whichever line gates the panel reset / backlight enable.
    wr(i2c, 0x96, 0x00)?;
    sleep(Duration::from_millis(120));
    wr(i2c, 0x96, 0x03)?;
    sleep(Duration::from_millis(120));
    Ok(())
}

/// One DMA transfer covers this many rows (320*40*2 = 25 600 B).
#[cfg(feature = "board-m5tough")]
const M5_BAND_ROWS: usize = 40;
#[cfg(feature = "board-m5tough")]
const M5_BAND_BYTES: usize = 320 * M5_BAND_ROWS * 2;

/// ILI9342C over SPI2 with manually driven CS(5) and DC(15). Frames stream by
/// DMA out of a DMA-capable internal bounce buffer (the framebuffer is in
/// PSRAM, which SPI DMA cannot read directly).
#[cfg(feature = "board-m5tough")]
pub struct M5Display {
    spi: sys::spi_device_handle_t,
    dc: i32,
    cs: i32,
    bounce: *mut u8,
}

#[cfg(feature = "board-m5tough")]
unsafe impl Send for M5Display {}

#[cfg(feature = "board-m5tough")]
impl M5Display {
    const DC: i32 = 15;
    const CS: i32 = 5;

    fn new() -> HalResult<Self> {
        unsafe {
            for pin in [Self::DC, Self::CS] {
                sys::gpio_reset_pin(pin);
                sys::gpio_set_direction(pin, sys::gpio_mode_t_GPIO_MODE_OUTPUT);
            }
            sys::gpio_set_level(Self::CS, 1); // idle high
        }
        let mut buscfg: sys::spi_bus_config_t = unsafe { std::mem::zeroed() };
        buscfg.sclk_io_num = 18;
        buscfg.__bindgen_anon_1.mosi_io_num = 23;
        buscfg.__bindgen_anon_2.miso_io_num = -1;
        buscfg.__bindgen_anon_3.quadwp_io_num = -1;
        buscfg.__bindgen_anon_4.quadhd_io_num = -1;
        buscfg.max_transfer_sz = M5_BAND_BYTES as i32;
        let host = sys::spi_host_device_t_SPI2_HOST;
        // DMA on: a whole band streams in one transfer. Command/param bytes go
        // through the transaction's inline tx_data (SPI_TRANS_USE_TXDATA), so no
        // small, unaligned buffer is ever hard-handed to the DMA engine.
        if unsafe {
            sys::spi_bus_initialize(host, &buscfg, sys::spi_common_dma_t_SPI_DMA_CH_AUTO)
        } != 0
        {
            return Err(HalError::Bus("m5 spi bus init failed"));
        }
        let mut devcfg: sys::spi_device_interface_config_t = unsafe { std::mem::zeroed() };
        // SPI2 routes pins 18/23 through the GPIO matrix, which caps at APB/3 =
        // 26.67 MHz (40 MHz needs native IO_MUX pins → ESP_ERR_NOT_SUPPORTED).
        devcfg.clock_speed_hz = 26_670_000;
        devcfg.mode = 0;
        devcfg.duty_cycle_pos = 128; // 50% duty
        devcfg.spics_io_num = -1; // CS driven manually
        devcfg.queue_size = 1;
        let mut dev: sys::spi_device_handle_t = std::ptr::null_mut();
        let e = unsafe { sys::spi_bus_add_device(host, &devcfg, &mut dev) };
        if e != 0 {
            let msg: &'static str =
                Box::leak(format!("m5 spi add device failed (err {e:#x})").into_boxed_str());
            return Err(HalError::Bus(msg));
        }
        // DMA-capable internal scratch: bands are copied here from the PSRAM
        // framebuffer before each DMA transfer.
        let bounce = unsafe {
            sys::heap_caps_malloc(
                M5_BAND_BYTES,
                sys::MALLOC_CAP_DMA | sys::MALLOC_CAP_8BIT,
            )
        } as *mut u8;
        if bounce.is_null() {
            return Err(HalError::Bus("m5 dma bounce alloc failed"));
        }
        let mut d = M5Display { spi: dev, dc: Self::DC, cs: Self::CS, bounce };
        d.init_panel()?;
        Ok(d)
    }

    /// Small write (≤4 B) via the transaction's inline tx_data — needs no
    /// DMA-capable buffer, so it is safe for command/param bytes.
    fn tx_inline(&mut self, data: &[u8]) -> HalResult<()> {
        if data.is_empty() {
            return Ok(());
        }
        let mut t: sys::spi_transaction_t = unsafe { std::mem::zeroed() };
        t.flags = sys::SPI_TRANS_USE_TXDATA;
        t.length = data.len() * 8;
        for (i, b) in data.iter().enumerate().take(4) {
            unsafe { t.__bindgen_anon_1.tx_data[i] = *b };
        }
        if unsafe { sys::spi_device_polling_transmit(self.spi, &mut t) } != 0 {
            return Err(HalError::Bus("m5 spi cmd tx failed"));
        }
        Ok(())
    }

    /// DMA out the first `n` bytes already staged in the bounce buffer.
    fn tx_dma(&mut self, n: usize) -> HalResult<()> {
        let mut t: sys::spi_transaction_t = unsafe { std::mem::zeroed() };
        t.length = n * 8;
        t.__bindgen_anon_1.tx_buffer = self.bounce as *const c_void;
        if unsafe { sys::spi_device_polling_transmit(self.spi, &mut t) } != 0 {
            return Err(HalError::Bus("m5 spi dma tx failed"));
        }
        Ok(())
    }

    fn cmd(&mut self, c: u8) -> HalResult<()> {
        unsafe { sys::gpio_set_level(self.dc, 0) };
        self.tx_inline(&[c])
    }

    fn data(&mut self, d: &[u8]) -> HalResult<()> {
        unsafe { sys::gpio_set_level(self.dc, 1) };
        self.tx_inline(d)
    }

    fn init_panel(&mut self) -> HalResult<()> {
        use std::thread::sleep;
        use std::time::Duration;
        unsafe { sys::gpio_set_level(self.cs, 0) };
        self.cmd(0x01)?; // SWRESET
        sleep(Duration::from_millis(150));
        self.cmd(0x11)?; // SLPOUT
        sleep(Duration::from_millis(120));
        self.cmd(0x3A)?;
        self.data(&[0x55])?; // COLMOD: 16bpp RGB565
        self.cmd(0x36)?;
        self.data(&[0x08])?; // MADCTL: BGR, native 320x240 landscape
        self.cmd(0x21)?; // INVON (M5 panels invert)
        self.cmd(0x13)?; // NORON
        self.cmd(0x29)?; // DISPON
        sleep(Duration::from_millis(50));
        unsafe { sys::gpio_set_level(self.cs, 1) };
        Ok(())
    }

    fn flush(&mut self, pixels: &[u16], w: u32, h: u32) -> HalResult<()> {
        let w = w.min(320) as usize;
        let h = h.min(240) as usize;
        unsafe { sys::gpio_set_level(self.cs, 0) };
        self.cmd(0x2A)?; // CASET
        self.data(&[0, 0, (((w - 1) >> 8) as u8), ((w - 1) as u8)])?;
        self.cmd(0x2B)?; // PASET
        self.data(&[0, 0, (((h - 1) >> 8) as u8), ((h - 1) as u8)])?;
        self.cmd(0x2C)?; // RAMWR
        unsafe { sys::gpio_set_level(self.dc, 1) };
        // Copy each band PSRAM→bounce (big-endian per RAMWR), then DMA it out.
        // polling_transmit is synchronous, so the bounce buffer is free to
        // refill as soon as it returns.
        let mut y = 0usize;
        while y < h {
            let rows = M5_BAND_ROWS.min(h - y);
            let n = rows * w; // pixels in this band
            let dst = unsafe { std::slice::from_raw_parts_mut(self.bounce, n * 2) };
            let base = y * w;
            for k in 0..n {
                let px = pixels.get(base + k).copied().unwrap_or(0);
                dst[k * 2] = (px >> 8) as u8;
                dst[k * 2 + 1] = (px & 0xFF) as u8;
            }
            self.tx_dma(n * 2)?;
            y += rows;
        }
        unsafe { sys::gpio_set_level(self.cs, 1) };
        Ok(())
    }
}

pub struct Esp32IdfBoard {
    pins: Vec<IdfPin>,
    delay: IdfDelay,
    rtc: IdfRtc,
    power: IdfPower,
    watchdog: IdfWatchdog,
    adc: Vec<IdfAdcChan>,
    pwm: std::collections::HashMap<u8, IdfPwm>,
    i2c: Option<IdfI2c>,
    spi: Option<IdfSpi>,
    uarts: std::collections::HashMap<u8, IdfUart>,
    can: IdfCan,
    signals: std::collections::HashMap<u32, IdfSignal>,
    #[cfg(feature = "board-m5tough")]
    display: Option<M5Display>,
}

unsafe impl Send for Esp32IdfBoard {}

impl Esp32IdfBoard {
    pub fn new() -> Self {
        // One ADC1 oneshot unit shared by all channels.
        let mut adc_unit: sys::adc_oneshot_unit_handle_t = std::ptr::null_mut();
        let unit_cfg = sys::adc_oneshot_unit_init_cfg_t {
            unit_id: sys::adc_unit_t_ADC_UNIT_1,
            ..Default::default()
        };
        unsafe { sys::adc_oneshot_new_unit(&unit_cfg, &mut adc_unit) };
        Esp32IdfBoard {
            pins: (0..PIN_COUNT).map(|i| IdfPin { pin: i as i32 }).collect(),
            delay: IdfDelay,
            rtc: IdfRtc { offset: 0, alarm: None },
            power: IdfPower,
            watchdog: IdfWatchdog { running: false, timeout_ms: 0 },
            adc: (0..8)
                .map(|ch| IdfAdcChan { unit: adc_unit, channel: ch, configured: false })
                .collect(),
            pwm: std::collections::HashMap::new(),
            i2c: None,
            spi: None,
            uarts: std::collections::HashMap::new(),
            can: IdfCan { installed: false, loopback: false },
            signals: std::collections::HashMap::new(),
            #[cfg(feature = "board-m5tough")]
            display: None,
        }
    }
}

impl Board for Esp32IdfBoard {
    fn name(&self) -> &str {
        "esp32-wroom-32 (esp-idf)"
    }
    fn gpio(&mut self, pin: u32) -> HalResult<&mut dyn GpioPin> {
        self.pins
            .get_mut(pin as usize)
            .map(|p| p as &mut dyn GpioPin)
            .ok_or(HalError::InvalidArgument("ESP32 has GPIO0..=39"))
    }
    fn i2c(&mut self, bus: u8) -> HalResult<&mut dyn rustnet_hal::i2c::I2cBus> {
        if bus != 0 {
            return Err(HalError::InvalidArgument("only I2C bus 0 (SDA 21 / SCL 22)"));
        }
        if self.i2c.is_none() {
            self.i2c = Some(IdfI2c::new(21, 22)?);
        }
        Ok(self.i2c.as_mut().unwrap() as &mut dyn rustnet_hal::i2c::I2cBus)
    }
    fn spi(&mut self, bus: u8) -> HalResult<&mut dyn rustnet_hal::spi::SpiBus> {
        if bus != 0 {
            return Err(HalError::InvalidArgument("only SPI bus 0 (VSPI: SCLK18/MOSI23/MISO19/CS5)"));
        }
        if self.spi.is_none() {
            self.spi = Some(IdfSpi::new()?);
        }
        Ok(self.spi.as_mut().unwrap() as &mut dyn rustnet_hal::spi::SpiBus)
    }
    fn uart(&mut self, port: u8) -> HalResult<&mut dyn rustnet_hal::uart::Uart> {
        let (tx, rx) = match port {
            1 => (4, 5),
            2 => (17, 16),
            _ => return Err(HalError::InvalidArgument("UART1 (TX4/RX5) or UART2 (TX17/RX16)")),
        };
        Ok(self
            .uarts
            .entry(port)
            .or_insert_with(|| IdfUart { port: port as u32, tx, rx, installed: false })
            as &mut dyn rustnet_hal::uart::Uart)
    }
    fn i2s(&mut self, _port: u8) -> HalResult<&mut dyn rustnet_hal::i2s::I2sBus> {
        Err(HalError::NotSupported)
    }
    fn pwm(&mut self, channel: u8) -> HalResult<&mut dyn rustnet_hal::pwm::PwmChannel> {
        if channel as u32 >= PIN_COUNT {
            return Err(HalError::InvalidArgument("PWM channel is the GPIO number"));
        }
        Ok(self
            .pwm
            .entry(channel)
            .or_insert_with(|| IdfPwm {
                gpio: channel as i32,
                duty_permyriad: 0,
                hz: 1000,
                enabled: false,
            }) as &mut dyn rustnet_hal::pwm::PwmChannel)
    }
    fn adc(&mut self, channel: u8) -> HalResult<&mut dyn rustnet_hal::adc::AdcChannel> {
        self.adc
            .get_mut(channel as usize)
            .map(|c| c as &mut dyn rustnet_hal::adc::AdcChannel)
            .ok_or(HalError::InvalidArgument("ADC1 channels 0..=7"))
    }
    fn power(&mut self) -> &mut dyn PowerManager {
        &mut self.power
    }
    fn delay(&mut self) -> &mut dyn Delay {
        &mut self.delay
    }
    fn can(&mut self, bus: u8) -> HalResult<&mut dyn rustnet_hal::can::CanBus> {
        if bus != 0 {
            return Err(HalError::InvalidArgument("one TWAI controller (bus 0)"));
        }
        Ok(&mut self.can as &mut dyn rustnet_hal::can::CanBus)
    }
    fn onewire(&mut self, _bus: u8) -> HalResult<&mut dyn rustnet_hal::onewire::OneWireBus> {
        Err(HalError::NotSupported) // integration point: RMT-timed bit banging
    }
    fn rtc(&mut self) -> &mut dyn Rtc {
        &mut self.rtc
    }
    fn watchdog(&mut self) -> &mut dyn Watchdog {
        &mut self.watchdog
    }
    fn extmem(&mut self, _index: u8) -> HalResult<&mut dyn rustnet_hal::extmem::ExtMemory> {
        Err(HalError::NotSupported) // integration point: esp_partition / SPIRAM
    }
    fn netif(
        &mut self,
        _kind: rustnet_hal::netif::NetIfKind,
    ) -> HalResult<&mut dyn rustnet_hal::netif::NetInterface> {
        Err(HalError::NotSupported) // integration point: esp-idf-svc EspWifi
    }
    fn signal(&mut self, pin: u32) -> HalResult<&mut dyn rustnet_hal::signal::SignalControl> {
        if pin >= PIN_COUNT {
            return Err(HalError::InvalidArgument("ESP32 has GPIO0..=39"));
        }
        if !self.signals.contains_key(&pin) {
            let sig = IdfSignal::new(pin as i32)?;
            self.signals.insert(pin, sig);
        }
        Ok(self.signals.get_mut(&pin).unwrap() as &mut dyn rustnet_hal::signal::SignalControl)
    }

    #[cfg(feature = "board-m5tough")]
    fn present_frame(&mut self, rgb565: &[u16], w: u32, h: u32) -> HalResult<()> {
        if self.display.is_none() {
            // Power the LCD rails before the first flush, then bring up the panel.
            if self.i2c.is_none() {
                self.i2c = Some(IdfI2c::new(21, 22)?);
            }
            m5_axp192_init(self.i2c.as_mut().unwrap())?;
            self.display = Some(M5Display::new()?);
        }
        self.display.as_mut().unwrap().flush(rgb565, w, h)
    }
}
