fn main() {
    embuild::espidf::sysenv::output();
    check_ble_config();
}

/// `--features ble` needs NimBLE switched on in ESP-IDF's sdkconfig, which a
/// cargo feature cannot do (esp-idf-sys reads the sdkconfig files named in
/// `ESP_IDF_SDKCONFIG_DEFAULTS`, never our features). Without it the build
/// would die deep inside esp32-nimble with hundreds of "cannot find
/// `ble_gatt_*`" errors, so fail here instead and say what to set.
///
/// This runs after esp-idf-sys has configured ESP-IDF (we read its relayed
/// cfgs), but before esp32-nimble is compiled against the bindings.
fn check_ble_config() {
    println!("cargo:rerun-if-env-changed=ESP_IDF_SDKCONFIG_DEFAULTS");
    if std::env::var_os("CARGO_FEATURE_BLE").is_none() {
        return;
    }
    let nimble = embuild::espidf::sysenv::cfg_args()
        .map(|cfg| cfg.args.iter().any(|a| a == "esp_idf_bt_nimble_enabled"))
        .unwrap_or(false);
    if !nimble {
        let dir = std::env::var("CARGO_MANIFEST_DIR").unwrap_or_default();
        panic!(
            "\n\nfeature `ble` needs NimBLE enabled in ESP-IDF, and this build's sdkconfig \
             does not have it.\nSet ESP_IDF_SDKCONFIG_DEFAULTS to both defaults files, with \
             ABSOLUTE paths (esp-idf-sys does not resolve relative ones against this crate):\n\n  \
             PowerShell: $env:ESP_IDF_SDKCONFIG_DEFAULTS = \"{dir}\\sdkconfig.defaults;{dir}\\sdkconfig.defaults.ble\"\n  \
             bash:       export ESP_IDF_SDKCONFIG_DEFAULTS=\"{dir}/sdkconfig.defaults;{dir}/sdkconfig.defaults.ble\"\n\n\
             See sdkconfig.defaults.ble and docs/ble.md.\n"
        );
    }
}
