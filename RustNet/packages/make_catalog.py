"""Regenerate docs/drivers.md from every packages/*/rnpkg.json.

Run from the repo root: python packages/make_catalog.py
"""
import glob
import json
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CATEGORIES = {
    "Environment": ["aht", "ags01db", "bh1745", "bh1750", "bme280", "bme680", "bmp180", "ccs811", "dht12", "ens160",
                    "htu21d", "htu31d", "ky038", "lm75", "max44009", "max6675", "mcp9600", "mcp9808", "mpl3115a2",
                    "ms5611", "pmsa003i", "scd30", "scd4x", "sgp40", "sht3x", "sht4x", "si1145", "soilmoisture",
                    "tcs34725", "temt6000", "thermistor", "tmp102", "tsl2591", "veml7700"],
    "Motion, distance, power, input, GNSS and imaging": [
        "adxl", "amg8833", "arducam", "bg95", "bmi270", "bno055", "flowmeter", "huskylens", "hx711", "ina", "input", "lsm303agr",
        "lsm6dsox", "magnetometer", "max1704x", "mlx90640", "mpr121", "mpu6050", "mt3339", "nau7802", "touch",
        "ublox", "ultrasonic", "vc0706", "vl53l0x"],
    "Displays": ["display-core", "segments", "tm1637", "charlcd", "mono-display", "tft", "epaper"],
    "Actuators and support ICs": [
        "dcmotor", "stepper", "servo", "bld510b", "cerusxdrive", "led", "ws2812", "apa102", "pca9633", "relay",
        "piezo", "ads1x15", "ads7128", "mcp3xxx", "mcp4725", "mcp49xx", "mcp23xxx", "pcf857x", "pca95xx",
        "ht16k33", "rtc", "digipot", "neopixel"],
    "Shared": ["bus"],
}


def main():
    manifests = {}
    for f in glob.glob(os.path.join(ROOT, "packages", "*", "rnpkg.json")):
        with open(f, encoding="utf-8") as fh:
            m = json.load(fh)
        manifests[m["name"].replace("rustnet-driver-", "")] = m
    listed = {n for names in CATEGORIES.values() for n in names}
    missing = sorted(set(manifests) - listed)
    if missing:
        raise SystemExit(f"uncategorised packages: {missing}")

    out = [
        "# Community driver packages",
        "",
        "Generated from `packages/*/rnpkg.json` by `packages/make_catalog.py` — do not edit by hand.",
        "",
        f"{len(manifests)} source packages, installed with `rustnet pkg install <name>` (see",
        "[packages.md](packages.md)). Every driver talks to hardware through `rustnet-driver-bus`, and every",
        "one is exercised against register-level simulators holding datasheet values, both on the desktop",
        "CLR and on the virtual device's interpreter (`dotnet/tests/Drivers/`). Authoring rules and the",
        "C# dialect drivers are written in: [packages/README.md](../packages/README.md).",
        "",
    ]
    for cat, names in CATEGORIES.items():
        out += [f"## {cat}", "", "| Package | Covers |", "|---|---|"]
        for n in names:
            m = manifests[n]
            out.append(f"| `{m['name']}` | {m['description']} |")
        out.append("")
    with open(os.path.join(ROOT, "docs", "drivers.md"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(out))
    print(f"docs/drivers.md: {len(manifests)} packages")


if __name__ == "__main__":
    main()
