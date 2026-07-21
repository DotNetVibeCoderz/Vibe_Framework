Nama: RustNet

deskripsi:
framework embedded system baru berbasis .NET yang bisa berjalan di microcontroller, dengan runtime ditulis dalam Rust (untuk safety & performance). Framework ini akan terinspirasi dari kombinasi nanoFramework, TinyCLR, Meadow, dan .NET Microframework, tetapi lebih modern dan modular. Gunakan best practice, optimalkan code performance sehingga aplikasi ringan dan cepat. Fitur:

---

 🚀 Core Runtime
- Rust-based runtime: memory safety, concurrency, dan low-level control.
- .NET IL interpreter/JIT: eksekusi C#/.NET code di atas microcontroller.
- Cross-chip support: ESP32, STM32, TI, NXP, dengan firmware varian.
- Secure boot: verifikasi signature sebelum eksekusi firmware.
- OTA update: update firmware dan aplikasi via WiFi/USB.

---

 🛠️ Tools & Utilities
- Tool dibuat dengan .NET 10 + Avalonia UI sehingga crossplatform dengan tampilan modern dan mudah digunakan
- Firmware flasher: flash firmware ke MCU.
- Erase app: hapus aplikasi dari flash.
- Check apps: list aplikasi yang terpasang.
- Flash data: upload data (config, assets).
- Secure config: TLS cert, API keys, encrypted storage.
- Change boot image: ganti logo/boot splash.
- Multi-chip firmware manager: pilih firmware sesuai varian chip.
- Konfigurasi WiFi untuk chip ber-wifi
---

 📦 Developer Experience
- Template apps: IoT sensor logger, weather check, calculator, game (XoX), Display Testing, Test File System, Wifi + MQTT
- IDE extensions: VSCode plugin untuk flasher, debugger, log viewer.
- Debugger integration: step-through C# code, inspect variables.
- CLI tools: command-line untuk flashing, erasing, checking.
- Profiler: monitor CPU/memory usage.  
- Template Generator: buat project baru dengan driver preloaded.  

---

 🔌 Hardware Abstraction
- Driver library: sensor suhu, kelembaban, accelerometer, GPS, motor driver, relay, LED matrix, Display: OLED, TFT, Touch Screen (Capacitive, Resistive), dan module-module lainnya.
- Unified HAL: API konsisten untuk GPIO, I2C, SPI, UART, I2S.
- Async I/O: berbasis async/await untuk operasi non-blocking.
- Power management: sleep mode, battery monitor.

---

 🔒 Security & Networking
- TLS/SSL stack: komunikasi aman.
- MQTT/HTTP client: integrasi IoT cloud.
- Encrypted storage: simpan data sensitif.
- Secure OTA: update dengan signature verification.

---

 🌐 Ekosistem & Extensibility
- NuGet-like package manager: distribusi library sensor/actuator.
- Community drivers: kontribusi open-source untuk hardware baru.
- Cross-platform SDK: Windows, Linux, macOS.
- Interop dengan native C/Rust: untuk operasi real-time.

---

 📚 Supporting Libraries

- Graphics Library  
  - API untuk menggambar shape, text, bitmap. Seperti System.Drawing atau SkiaSharp  
  - Dukungan display driver (SPI/I2C LCD, OLED, e-paper, HDMI).  
  - Font rendering dan double-buffering untuk animasi halus.  

- USB Stack  
  - USB Device (CDC, HID, Mass Storage).  
  - USB Host (keyboard, mouse, storage).  
  - Plug-and-play driver extensibility.  

- FileSystem  
  - FAT32/ExFAT untuk SD card/flash.  
  - API mirip .NET `System.IO`.  
  - Encryption opsional untuk file sensitif.  

- Firmware Update  
  - OTA update via WiFi/USB.  
  - Secure signature verification.  
  - Rollback mechanism jika update gagal.  

- Networking  
  - TCP/UDP stack, HTTP client/server.  
  - MQTT untuk IoT cloud.  
  - TLS/SSL untuk komunikasi aman.  
  - Micro Web Server

- Crypto & Security  
  - AES, RSA, SHA, HMAC.  
  - Secure bootloader.  
  - Hardware crypto acceleration jika tersedia.  

- Diagnostics & Logging  
  - Serial log viewer.  
  - Remote logging via MQTT/HTTP.  
  - Performance counters (CPU, memory, uptime).  

- Threading & Async  
  - Task scheduler berbasis async/await.  
  - Cooperative multitasking.  
  - Event-driven programming model.  

---

Lainnya:
- dokumentasi lengkap di folder docs
- tambahkan readme.md (English, Bahasa Indonesia)
- Tambahkan informasi dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil di dokumentasi dan aplikasi.

hasil akhir: framework akan punya DNA TinyCLR (tools & config) + DNA nanoFramework (IoT & open-source) + DNA Meadow (.NET Standard modern), tapi lebih kuat karena runtime Rust memberi safety + performance.