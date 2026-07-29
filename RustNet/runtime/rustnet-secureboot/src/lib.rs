//! Secure boot: signed image container ("RNSB") used for firmware images
//! and application (.rnx) packages. The bootloader/firmware verifies the
//! RSA signature against a public key burned into the device before any
//! byte of the payload is executed.
//!
//! ```text
//! magic "RNSB" | u16 version | u8 kind | u8 chip | u32 payload_len
//! | u32 sig_len | payload | signature(PKCS#1 v1.5 SHA-256 over header+payload)
//! ```

//! `no_std + alloc` without the default `std` feature, so bare-metal firmware
//! verifies a container with exactly the code the host tools use.

#![cfg_attr(not(feature = "std"), no_std)]

extern crate alloc;

use alloc::string::{String, ToString};
use alloc::vec::Vec;

use rustnet_crypto::{rsa_sign, rsa_verify, CryptoError};

pub const MAGIC: &[u8; 4] = b"RNSB";
pub const VERSION: u16 = 1;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ImageKind {
    Firmware = 0,
    App = 1,
    Data = 2,
    BootImage = 3,
}

impl ImageKind {
    pub fn from_u8(v: u8) -> Option<Self> {
        Some(match v {
            0 => ImageKind::Firmware,
            1 => ImageKind::App,
            2 => ImageKind::Data,
            3 => ImageKind::BootImage,
            _ => return None,
        })
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChipFamily {
    Any = 0,
    Esp32 = 1,
    Stm32 = 2,
    Ti = 3,
    Nxp = 4,
    HostSim = 5,
    /// ESP32-C3 (RISC-V RV32IMC single core).
    Esp32C3 = 6,
    /// Kendryte K210 (RISC-V RV64GC dual core).
    K210 = 7,
}

impl ChipFamily {
    pub fn from_u8(v: u8) -> Option<Self> {
        Some(match v {
            0 => ChipFamily::Any,
            1 => ChipFamily::Esp32,
            2 => ChipFamily::Stm32,
            3 => ChipFamily::Ti,
            4 => ChipFamily::Nxp,
            5 => ChipFamily::HostSim,
            6 => ChipFamily::Esp32C3,
            7 => ChipFamily::K210,
            _ => return None,
        })
    }

    pub fn name(&self) -> &'static str {
        match self {
            ChipFamily::Any => "any",
            ChipFamily::Esp32 => "esp32",
            ChipFamily::Stm32 => "stm32",
            ChipFamily::Ti => "ti",
            ChipFamily::Nxp => "nxp",
            ChipFamily::HostSim => "host-sim",
            ChipFamily::Esp32C3 => "esp32c3",
            ChipFamily::K210 => "k210",
        }
    }

    pub fn parse(s: &str) -> Option<Self> {
        Some(match s.to_ascii_lowercase().as_str() {
            "any" => ChipFamily::Any,
            "esp32" => ChipFamily::Esp32,
            "stm32" => ChipFamily::Stm32,
            "ti" => ChipFamily::Ti,
            "nxp" => ChipFamily::Nxp,
            "host-sim" | "host" => ChipFamily::HostSim,
            "esp32c3" | "esp32-c3" => ChipFamily::Esp32C3,
            "k210" | "kendryte" => ChipFamily::K210,
            _ => return None,
        })
    }

    /// RISC-V instruction-set chips.
    pub fn is_riscv(&self) -> bool {
        matches!(self, ChipFamily::Esp32C3 | ChipFamily::K210)
    }
}

#[derive(Debug, PartialEq)]
pub enum BootError {
    NotAnImage,
    UnsupportedVersion(u16),
    Truncated,
    WrongChip { image: u8, device: ChipFamily },
    BadSignature,
    BadKey(String),
}

impl core::fmt::Display for BootError {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        match self {
            BootError::NotAnImage => write!(f, "not a signed RustNet image"),
            BootError::UnsupportedVersion(v) => write!(f, "unsupported image version {v}"),
            BootError::Truncated => write!(f, "image truncated"),
            BootError::WrongChip { image, device } => {
                write!(f, "image built for chip {image}, device is {}", device.name())
            }
            BootError::BadSignature => write!(f, "signature verification failed"),
            BootError::BadKey(m) => write!(f, "bad key: {m}"),
        }
    }
}

const HEADER_LEN: usize = 4 + 2 + 1 + 1 + 4 + 4;

/// Wrap and sign a payload. `priv_key_der` is PKCS#1 DER.
pub fn seal(
    kind: ImageKind,
    chip: ChipFamily,
    payload: &[u8],
    priv_key_der: &[u8],
) -> Result<Vec<u8>, CryptoError> {
    let mut unsigned = Vec::with_capacity(HEADER_LEN + payload.len());
    unsigned.extend_from_slice(MAGIC);
    unsigned.extend_from_slice(&VERSION.to_le_bytes());
    unsigned.push(kind as u8);
    unsigned.push(chip as u8);
    unsigned.extend_from_slice(&(payload.len() as u32).to_le_bytes());
    unsigned.extend_from_slice(&0u32.to_le_bytes()); // sig_len patched below
    unsigned.extend_from_slice(payload);
    let signature = rsa_sign(priv_key_der, &unsigned)?;
    let mut out = unsigned;
    let sig_len = signature.len() as u32;
    out[12..16].copy_from_slice(&sig_len.to_le_bytes());
    // Signature covers the header with sig_len zeroed + payload.
    out.extend_from_slice(&signature);
    Ok(out)
}

#[derive(Debug, PartialEq)]
pub struct VerifiedImage<'a> {
    pub kind: ImageKind,
    pub chip: u8,
    pub payload: &'a [u8],
}

/// Verify a sealed image; returns the payload only on success.
pub fn verify<'a>(
    image: &'a [u8],
    pub_key_der: &[u8],
    device_chip: ChipFamily,
) -> Result<VerifiedImage<'a>, BootError> {
    if image.len() < HEADER_LEN {
        return Err(BootError::Truncated);
    }
    if &image[0..4] != MAGIC {
        return Err(BootError::NotAnImage);
    }
    let version = u16::from_le_bytes(image[4..6].try_into().unwrap());
    if version != VERSION {
        return Err(BootError::UnsupportedVersion(version));
    }
    let kind = ImageKind::from_u8(image[6]).ok_or(BootError::NotAnImage)?;
    let chip = image[7];
    let payload_len = u32::from_le_bytes(image[8..12].try_into().unwrap()) as usize;
    let sig_len = u32::from_le_bytes(image[12..16].try_into().unwrap()) as usize;
    let payload_end = HEADER_LEN + payload_len;
    if image.len() < payload_end + sig_len {
        return Err(BootError::Truncated);
    }
    if chip != ChipFamily::Any as u8 && chip != device_chip as u8 {
        return Err(BootError::WrongChip { image: chip, device: device_chip });
    }
    // Reconstruct the signed view (sig_len zeroed).
    let mut signed = image[..payload_end].to_vec();
    signed[12..16].copy_from_slice(&0u32.to_le_bytes());
    let signature = &image[payload_end..payload_end + sig_len];
    match rsa_verify(pub_key_der, &signed, signature) {
        Ok(()) => Ok(VerifiedImage { kind, chip, payload: &image[HEADER_LEN..payload_end] }),
        Err(CryptoError::SignatureInvalid) => Err(BootError::BadSignature),
        Err(e) => Err(BootError::BadKey(e.to_string())),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use rsa::pkcs1::{EncodeRsaPrivateKey, EncodeRsaPublicKey};
    use rsa::RsaPrivateKey;

    fn keypair() -> (Vec<u8>, Vec<u8>) {
        let key = RsaPrivateKey::new(&mut rand::thread_rng(), 2048).unwrap();
        (
            key.to_pkcs1_der().unwrap().as_bytes().to_vec(),
            key.to_public_key().to_pkcs1_der().unwrap().as_bytes().to_vec(),
        )
    }

    #[test]
    fn seal_and_verify_roundtrip() {
        let (priv_der, pub_der) = keypair();
        let payload = b"application rnx bytes";
        let image = seal(ImageKind::App, ChipFamily::Esp32, payload, &priv_der).unwrap();
        let v = verify(&image, &pub_der, ChipFamily::Esp32).unwrap();
        assert_eq!(v.kind, ImageKind::App);
        assert_eq!(v.payload, payload);
    }

    #[test]
    fn tampered_payload_rejected() {
        let (priv_der, pub_der) = keypair();
        let mut image = seal(ImageKind::Firmware, ChipFamily::Any, b"boot code", &priv_der).unwrap();
        let mid = HEADER_LEN + 2;
        image[mid] ^= 0xFF;
        assert_eq!(verify(&image, &pub_der, ChipFamily::Esp32), Err(BootError::BadSignature));
    }

    #[test]
    fn wrong_chip_rejected() {
        let (priv_der, pub_der) = keypair();
        let image = seal(ImageKind::Firmware, ChipFamily::Stm32, b"code", &priv_der).unwrap();
        assert!(matches!(
            verify(&image, &pub_der, ChipFamily::Esp32),
            Err(BootError::WrongChip { .. })
        ));
    }

    #[test]
    fn wrong_key_rejected() {
        let (priv_der, _) = keypair();
        let (_, other_pub) = keypair();
        let image = seal(ImageKind::App, ChipFamily::Any, b"app", &priv_der).unwrap();
        assert_eq!(verify(&image, &other_pub, ChipFamily::Esp32), Err(BootError::BadSignature));
    }

    #[test]
    fn garbage_rejected() {
        let (_, pub_der) = keypair();
        assert_eq!(verify(b"nope", &pub_der, ChipFamily::Esp32), Err(BootError::Truncated));
        assert_eq!(
            verify(&[0u8; 64], &pub_der, ChipFamily::Esp32),
            Err(BootError::NotAnImage)
        );
    }
}
