//! Chip variant selection. Each firmware image links exactly one chip
//! feature. The ESP32/STM32/TI/NXP variants are integration points for the
//! vendor PAC/SDK; until those land they run on the simulator board so the
//! whole stack stays exercisable per-variant.

use rustnet_hal::Board;
use rustnet_hal_host::HostBoard;
use rustnet_secureboot::ChipFamily;

pub fn chip_family() -> ChipFamily {
    #[cfg(feature = "chip-esp32")]
    return ChipFamily::Esp32;
    #[cfg(feature = "chip-stm32")]
    return ChipFamily::Stm32;
    #[cfg(feature = "chip-ti")]
    return ChipFamily::Ti;
    #[cfg(feature = "chip-nxp")]
    return ChipFamily::Nxp;
    #[cfg(feature = "chip-esp32c3")]
    return ChipFamily::Esp32C3;
    #[cfg(feature = "chip-k210")]
    return ChipFamily::K210;
    #[allow(unreachable_code)]
    ChipFamily::HostSim
}

pub fn board_name() -> &'static str {
    match chip_family() {
        ChipFamily::Esp32 => "RustNet ESP32 DevKit",
        ChipFamily::Stm32 => "RustNet STM32 Nucleo",
        ChipFamily::Ti => "RustNet TI LaunchPad",
        ChipFamily::Nxp => "RustNet NXP FRDM",
        ChipFamily::Esp32C3 => "RustNet ESP32-C3 DevKit (RISC-V)",
        ChipFamily::K210 => "RustNet Kendryte K210 (RISC-V)",
        _ => "RustNet Virtual Device",
    }
}

/// Whether this chip has WiFi on board.
pub fn has_wifi() -> bool {
    matches!(
        chip_family(),
        ChipFamily::Esp32 | ChipFamily::Esp32C3 | ChipFamily::HostSim
    )
}

pub fn make_board() -> Box<dyn Board> {
    // Vendor SDK boards plug in here per feature.
    let mut board = HostBoard::new();
    // The virtual device ships a DS18B20 on 1-Wire bus 0 at 25.5 C so
    // 1-Wire demos and drivers work out of the box.
    // ROM low byte = family code 0x28 (DS18B20).
    board.attach_onewire(0, Box::new(rustnet_hal_host::SimDs18b20::new(0x7A00_0000_0000_0128, 2550)));
    Box::new(board)
}
