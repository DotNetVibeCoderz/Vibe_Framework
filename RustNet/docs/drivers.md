# Community driver packages

Generated from `packages/*/rnpkg.json` by `packages/make_catalog.py` — do not edit by hand.

89 source packages, installed with `rustnet pkg install <name>` (see
[packages.md](packages.md)). Every driver talks to hardware through `rustnet-driver-bus`, and every
one is exercised against register-level simulators holding datasheet values, both on the desktop
CLR and on the virtual device's interpreter (`dotnet/tests/Drivers/`). Authoring rules and the
C# dialect drivers are written in: [packages/README.md](../packages/README.md).

## Environment

| Package | Covers |
|---|---|
| `rustnet-driver-aht` | Aosong AHT10, AHT20/AHT21 and the AHT10-compatible DHT10 temperature and humidity over I2C |
| `rustnet-driver-ags01db` | Aosong AGS01DB MEMS VOC gas sensor over I2C, with CRC-checked readings in ppm |
| `rustnet-driver-bh1745` | ROHM BH1745NUC RGB + clear colour sensor over I2C, with gain and measurement-time control |
| `rustnet-driver-bh1750` | ROHM BH1750FVI ambient light sensor over I2C: one-shot and continuous modes, measurement-time (MTreg) sensitivity adjustment, lux output |
| `rustnet-driver-bme280` | Bosch BME280 (temperature/humidity/pressure) and BMP280 (temperature/pressure) over I2C, with the datasheet's integer compensation |
| `rustnet-driver-bme680` | Bosch BME680 / BME688 gas, temperature, humidity and pressure sensor over I2C, with the datasheet compensation, heater profile set-up and gas resistance |
| `rustnet-driver-bmp180` | Bosch BMP180 (and BMP085) temperature and pressure over I2C, with the datasheet's integer compensation and oversampling |
| `rustnet-driver-ccs811` | ScioSense/ams CCS811 eCO2 and TVOC gas sensor over I2C, with application start, drive modes, environmental compensation and baseline |
| `rustnet-driver-dht12` | Aosong DHT12 temperature and humidity over I2C, with checksum verification |
| `rustnet-driver-ens160` | ScioSense ENS160 digital metal-oxide multi-gas sensor over I2C: AQI-UBA, TVOC and eCO2 with temperature/humidity compensation |
| `rustnet-driver-htu21d` | TE HTU21D(F) and Silicon Labs Si7006/Si7013/Si7020/Si7021 (Si70xx) temperature and humidity over I2C, with CRC checks, resolution, heater and electronic ID |
| `rustnet-driver-htu31d` | TE HTU31D temperature and humidity over I2C, with CRC checks, oversampling, heater and serial number |
| `rustnet-driver-ky038` | KY-038 / KY-037 microphone sound sensor module: analog sound level (peak-to-peak / RMS) and the comparator threshold output |
| `rustnet-driver-lm75` | LM75 / LM75A / LM75B digital temperature sensor and thermal watchdog over I2C (9- or 11-bit), with overtemperature and hysteresis set-points |
| `rustnet-driver-max44009` | Maxim MAX44009 ambient light sensor over I2C: 0.045..188000 lux from the exponent/mantissa reading |
| `rustnet-driver-max6675` | Maxim MAX6675 cold-junction-compensated K-type thermocouple converter over SPI, with open-thermocouple detection |
| `rustnet-driver-mcp9600` | Microchip MCP9600 / MCP9601 (MCP960x) thermocouple EMF-to-temperature converter over I2C: hot/cold junction, thermocouple type, filter and ADC resolution |
| `rustnet-driver-mcp9808` | Microchip MCP9808 precision temperature sensor over I2C: identification, resolution, shutdown and alert limits |
| `rustnet-driver-mpl3115a2` | NXP MPL3115A2 pressure / altitude / temperature sensor over I2C, one-shot or with sea-level calibration |
| `rustnet-driver-ms5611` | TE MS5611-01BA barometric pressure sensor over I2C, with PROM CRC-4 check and first/second order compensation |
| `rustnet-driver-pmsa003i` | Plantower PMSA003I particulate matter sensor over I2C: PM1.0/PM2.5/PM10 mass concentrations and particle counts with checksum check |
| `rustnet-driver-scd30` | Sensirion SCD30 NDIR CO2, temperature and humidity module over I2C, with CRC checks, IEEE-754 float decoding and calibration commands |
| `rustnet-driver-scd4x` | Sensirion SCD40 / SCD41 photoacoustic CO2, temperature and humidity sensor over I2C, periodic and single-shot measurement, calibration and compensation |
| `rustnet-driver-sgp40` | Sensirion SGP40 VOC sensor over I2C: CRC-checked raw SRAW_VOC signal with humidity/temperature compensation, self test and serial number |
| `rustnet-driver-sht3x` | Sensirion SHT30/SHT31/SHT35 (SHT3x-DIS) temperature and humidity over I2C, with CRC-checked single-shot measurements, heater and status |
| `rustnet-driver-sht4x` | Sensirion SHT40/SHT41/SHT45 (SHT4x) temperature and humidity over I2C, with CRC checks, precision modes, heater pulses and serial number |
| `rustnet-driver-si1145` | Silicon Labs SI1145 UV index, visible and IR light sensor over I2C, with the UV coefficient set-up and forced/autonomous measurement |
| `rustnet-driver-soilmoisture` | Capacitive (v1.2/v2.0) and resistive FC-28/YL-69 soil moisture sensors through an ADC, with dry/wet calibration to percent and the FC-28 comparator output |
| `rustnet-driver-tcs34725` | ams TCS34721/3/5/7 (TCS3472x) RGB colour sensor over I2C, with gain/integration control, lux and colour temperature (ams DN40) |
| `rustnet-driver-temt6000` | Vishay TEMT6000 ambient light phototransistor read through an ADC: lux from the load resistor voltage |
| `rustnet-driver-thermistor` | NTC thermistor temperature through an ADC voltage divider, with Beta-parameter and Steinhart-Hart models |
| `rustnet-driver-tmp102` | TI TMP102 digital temperature sensor over I2C: 12/13-bit readings, shutdown/one-shot, conversion rate and alert limits |
| `rustnet-driver-tsl2591` | ams TSL2591 high dynamic range light sensor over I2C: full-spectrum and IR channels, gain/integration control and lux |
| `rustnet-driver-veml7700` | Vishay VEML7700 ambient light sensor over I2C: gain/integration settings, lux with resolution scaling and high-lux correction |

## Motion, distance, power, input, GNSS and imaging

| Package | Covers |
|---|---|
| `rustnet-driver-adxl` | Analog Devices accelerometers: ADXL335/ADXL377 (analog, ratiometric), ADXL345 (I2C) and ADXL362 (SPI, with temperature) |
| `rustnet-driver-amg8833` | Panasonic AMG8833 (Grid-EYE) 8x8 thermopile array over I2C: pixel temperatures and thermistor |
| `rustnet-driver-arducam` | ArduCAM Mini (OV2640/OV5642) over SPI + I2C: ArduChip test register, capture start/done, FIFO length, burst FIFO read, sensor ID and register tables |
| `rustnet-driver-bg95` | Quectel BG95/BG96/BG77 GNSS over the AT port: AT+QGPS power, AT+QGPSLOC fix parsing, AT+QGPSGNMEA sentences. Needs RustNet.Sensors.Gnss from the RustNet NuGet |
| `rustnet-driver-bmi270` | Bosch BMI270 6-axis IMU over I2C: init with the user-supplied Bosch config file, accelerometer, gyroscope and temperature reads |
| `rustnet-driver-bno055` | Bosch BNO055 absolute orientation sensor: Euler angles, quaternion, linear acceleration, gravity, calibration status and offsets over I2C |
| `rustnet-driver-flowmeter` | Hall-effect liquid flow sensors (YF-S201, YF-S401, YF-B1/B5/B6/B7 and any K-factor sensor): pulse counting to L/min and total litres |
| `rustnet-driver-hx711` | Avia HX711 24-bit load-cell ADC, bit-banged over two GPIOs, with gain/channel selection, tare and scale |
| `rustnet-driver-ina` | TI power/current monitors over I2C: INA219 (external shunt, calibration), INA228 (20-bit, energy/charge), INA260 (integrated 2 mOhm shunt) |
| `rustnet-driver-input` | GPIO human input: scanned matrix keypads and analog two-axis joysticks with button and deadzone |
| `rustnet-driver-lsm303agr` | ST LSM303AGR eCompass: 3-axis accelerometer and 3-axis magnetometer over I2C, with tilt-compensated heading |
| `rustnet-driver-lsm6dsox` | ST LSM6DSOX 6-axis IMU (accelerometer + gyroscope + temperature) over I2C |
| `rustnet-driver-magnetometer` | 3-axis magnetometers over I2C: Honeywell/QST HMC5883L, ST LIS3MDL, NXP MAG3110, with compass heading |
| `rustnet-driver-max1704x` | Maxim MAX17043/MAX17044 ModelGauge LiPo fuel gauges over I2C: cell voltage, state of charge, quick-start, alert threshold |
| `rustnet-driver-mlx90640` | Melexis MLX90640 32x24 thermal camera over I2C: EEPROM parameter extraction and the full Melexis object temperature calculation |
| `rustnet-driver-mpr121` | NXP MPR121 12-electrode capacitive touch controller over I2C, with keypad mapping |
| `rustnet-driver-mpu6050` | InvenSense MPU-6050 6-axis accelerometer/gyroscope with die temperature, over I2C, with selectable full-scale ranges |
| `rustnet-driver-mt3339` | MediaTek MT3339 GPS modules (PA6H, Adafruit Ultimate GPS...) over UART: PMTK commands with checksum, update rate, NMEA sentence filter, ACK tracking. Needs RustNet.Sensors.Gnss from the RustNet NuGet |
| `rustnet-driver-nau7802` | Nuvoton NAU7802 24-bit load-cell ADC over I2C: power-up sequence, LDO, gain, sample rate, internal offset calibration, tare and scale |
| `rustnet-driver-touch` | Resistive touch-screen controllers: XPT2046/ADS7843 (SPI) and TSC2004 (I2C), with pressure and 3-point affine calibration |
| `rustnet-driver-ublox` | u-blox NEO-M8 (and other UBX-protocol receivers) over UART: UBX frames with Fletcher checksum, CFG-PRT/CFG-MSG/CFG-RATE, ACK, NAV-PVT, with NMEA passed through. Needs RustNet.Sensors.Gnss from the RustNet NuGet |
| `rustnet-driver-ultrasonic` | Ultrasonic rangefinders: HC-SR04 (trigger/echo pulse), MaxBotix (analog and serial Rxxxx), DFRobot A02YYUW and ME007YS (UART 0xFF frames) |
| `rustnet-driver-vc0706` | VC0706 serial JPEG camera (Adafruit TTL camera, LinkSprite) over UART: version, reset, image size, capture and chunked frame-buffer read |
| `rustnet-driver-vl53l0x` | ST VL53L0X time-of-flight distance sensor over I2C: the ST API init (SPAD setup, tuning, VHV/phase calibration, timing budget) as ported by Pololu, single-shot and continuous ranging |

## Displays

| Package | Covers |
|---|---|
| `rustnet-driver-display-core` | Shared display plumbing for RustNet display drivers: command/data buses (SPI+DC, SSD1306-style I2C), reset and busy pins, 1bpp page/row canvases, an RGB565 canvas, drawing primitives and a 5x7 ASCII font |
| `rustnet-driver-segments` | Character to segment-mask encoders for 7-segment (gfedcba + dp), 14-segment (Adafruit alphanumeric layout) and 16-segment LED displays, ASCII 0x20..0x7F |
| `rustnet-driver-tm1637` | TM1637 4/6-digit 7-segment LED driver, bit-banged over two open-drain GPIOs (start/ack/stop, brightness, numbers, text, clock colon) |
| `rustnet-driver-charlcd` | HD44780-compatible character LCDs (16x2, 20x4...) in 4-bit mode, over six GPIOs or a PCF8574 I2C backpack |
| `rustnet-driver-mono-display` | Monochrome page-addressed OLED/LCD controllers: SSD1306 (I2C/SPI), SSD1309, SH1106, SH1107, CH1115, UC1609C and ST7565, drawing through a 1bpp page canvas |
| `rustnet-driver-tft` | SPI colour TFT/OLED controllers: ILI9341, ILI9163, ILI9225, ILI9481, ILI9486, ILI9488 (18-bit), ST7735 (tab variants), ST7789, ST7796S, GC9A01, HX8357B/D, SSD1331, SSD1351 - init, rotation, windowing, fills, RGB565 blits and canvas flush |
| `rustnet-driver-epaper` | E-paper controllers: SSD1608/IL3829, IL3897, SSD1680, SSD1681, IL0373, IL0376F, IL0398, IL91874, UC8151C, UC8179 and 7-colour ACeP, with Waveshare 1.54/2.13/2.7/2.9/4.2/5.65/7.5 inch presets; busy-pin waits with timeout, full refresh, deep sleep |

## Actuators and support ICs

| Package | Covers |
|---|---|
| `rustnet-driver-dcmotor` | Brushed DC motor drivers: L298N-style H-bridge (two direction pins + PWM enable), two-PWM-input bridges (DRV8833/DRV8871 style, fast/slow decay, brake/coast) and the Toshiba TB67H420FTG dual H-bridge (incl. full-step bipolar stepping) |
| `rustnet-driver-stepper` | Stepper motor drivers with step-count positioning and a trapezoidal speed/acceleration profile: A4988 (STEP/DIR/ENABLE, MS1-MS3 microstepping), ULN2003 + 28BYJ-48 (half/full/wave sequences) and generic 4-wire GPIO steppers |
| `rustnet-driver-servo` | Hobby servos (angle to 50 Hz pulse width) and brushless/drone ESCs (1000-2000 us throttle, arming and calibration sequences) on a PWM channel |
| `rustnet-driver-bld510b` | StepperOnline BLD-510B brushless DC motor driver: PWM speed (SV), direction (F/R), enable (EN), brake (BRK) and SPEED pulse feedback converted to RPM |
| `rustnet-driver-cerusxdrive` | Franklin Electric Cerus X-Drive variable frequency drive over Modbus RTU: run/stop/direction, frequency set-point, fault reset and status/telemetry registers |
| `rustnet-driver-led` | Single LEDs, PWM LEDs with gamma-corrected brightness, digital and PWM RGB LEDs (common anode/cathode, HSV colours) and LED bar graphs |
| `rustnet-driver-ws2812` | WS2812/WS2812B (NeoPixel) addressable LEDs driven from an SPI peripheral at 2.4 MHz (3 SPI bits per data bit, GRB order, reset latch) |
| `rustnet-driver-apa102` | APA102/APA102C (DotStar) addressable LEDs over SPI: start frame, LED frames with 5-bit global brightness, end frame |
| `rustnet-driver-pca9633` | NXP PCA9633 4-channel I2C LED driver: individual 8-bit PWM, group dimming and group blinking |
| `rustnet-driver-relay` | Relays (active-high or active-low coil drive) with on/off/toggle/pulse |
| `rustnet-driver-piezo` | Piezo speaker on a PWM channel: tones, equal-temperament note frequencies (A4 = 440 Hz) and a simple text melody player |
| `rustnet-driver-ads1x15` | TI ADS1015 (12-bit) and ADS1115 (16-bit) I2C ADCs: mux, PGA, data rate, single-shot conversions to millivolts, comparator thresholds |
| `rustnet-driver-ads7128` | TI ADS7128 8-channel 12-bit I2C ADC/GPIO: register opcodes, manual-mode conversions to millivolts, GPIO outputs/inputs |
| `rustnet-driver-mcp3xxx` | Microchip MCP3004/MCP3008 (10-bit) and MCP3204/MCP3208 (12-bit) SPI ADCs, single-ended and pseudo-differential |
| `rustnet-driver-mcp4725` | Microchip MCP4725 12-bit I2C DAC: fast-mode, DAC-register and DAC+EEPROM writes, power-down modes, status read-back |
| `rustnet-driver-mcp49xx` | Microchip MCP4921 (single) and MCP4922 (dual) 12-bit SPI DACs: A/B select, BUF, GA and SHDN bits, optional LDAC |
| `rustnet-driver-mcp23xxx` | Microchip MCP23008/MCP23017 (I2C) and MCP23S08/MCP23S17 (SPI) GPIO expanders: direction, pull-ups, polarity, GPIO/OLAT (IOCON.BANK=0 map) |
| `rustnet-driver-pcf857x` | NXP/TI PCF8574 (8-bit) and PCF8575 (16-bit) quasi-bidirectional I2C GPIO expanders |
| `rustnet-driver-pca95xx` | NXP/TI PCA9554, PCA9555, TCA9535 and TCA9555 I2C GPIO expanders: input/output/polarity/configuration registers |
| `rustnet-driver-ht16k33` | Holtek HT16K33 LED matrix/7-segment driver with key scan: display RAM, dimming, blinking, key data |
| `rustnet-driver-rtc` | Real-time clocks: Maxim DS1307 (CH bit, SQW, RAM), DS3231/DS3232 (alarms, temperature, OSF flag, DS3232 SRAM) and NXP PCF8523 (battery switch-over), with BCD time |
| `rustnet-driver-digipot` | Microchip digital potentiometers: MCP4131/MCP4151/MCP4231/MCP4251 (SPI, 7/8-bit), MCP4017/MCP4018/MCP4019 (I2C single byte) and MCP4451/MCP4461 (I2C quad, 9-bit commands) |
| `rustnet-driver-neopixel` | DEPRECATED - use rustnet-driver-ws2812. The original WS2812 driver, from before RustNet.Hal had an SPI API; it pushes the encoded bits through the I2C call and does not drive a real strip. |

## Shared

| Package | Covers |
|---|---|
| `rustnet-driver-bus` | Shared device wrappers for RustNet drivers: I2C/SPI/GPIO/PWM/ADC/UART devices with register helpers, and register-level simulators for testing drivers without hardware |
