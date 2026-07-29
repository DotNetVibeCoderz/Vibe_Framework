//! Crypto primitives for RustNet: AES-CTR, SHA-256/512, HMAC, RSA
//! sign/verify (PKCS#1 v1.5 + SHA-256, matching `RSA.SignData` defaults in
//! the .NET tooling). Chip variants can swap these for hardware
//! acceleration behind the same functions.
//!
//! `no_std + alloc` without the default `std` feature, so bare-metal firmware
//! can verify a signed image with the same code the host tools use.

#![cfg_attr(not(feature = "std"), no_std)]

extern crate alloc;

use alloc::string::{String, ToString};
use alloc::vec::Vec;

use aes::cipher::{KeyIvInit, StreamCipher};
use hmac::{Hmac, Mac};
use rsa::pkcs1::{DecodeRsaPrivateKey, DecodeRsaPublicKey};
use rsa::{Pkcs1v15Sign, RsaPrivateKey, RsaPublicKey};
use sha2::{Digest, Sha256, Sha512};

type Aes128Ctr = ctr::Ctr128BE<aes::Aes128>;
type Aes256Ctr = ctr::Ctr128BE<aes::Aes256>;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CryptoError {
    BadKeyLength,
    BadKeyFormat(String),
    SignatureInvalid,
}

impl core::fmt::Display for CryptoError {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        match self {
            CryptoError::BadKeyLength => write!(f, "bad key length"),
            CryptoError::BadKeyFormat(m) => write!(f, "bad key format: {m}"),
            CryptoError::SignatureInvalid => write!(f, "signature verification failed"),
        }
    }
}

#[cfg(feature = "std")]
impl std::error::Error for CryptoError {}

// ---- hashing ----

pub fn sha256(data: &[u8]) -> [u8; 32] {
    Sha256::digest(data).into()
}

pub fn sha512(data: &[u8]) -> [u8; 64] {
    Sha512::digest(data).into()
}

pub fn hmac_sha256(key: &[u8], data: &[u8]) -> [u8; 32] {
    let mut mac = <Hmac<Sha256> as Mac>::new_from_slice(key).expect("hmac accepts any key size");
    mac.update(data);
    mac.finalize().into_bytes().into()
}

// ---- AES-CTR (symmetric; encrypt == decrypt) ----

/// In-place AES-CTR. Key must be 16 or 32 bytes; nonce exactly 16.
pub fn aes_ctr_apply(key: &[u8], nonce: &[u8; 16], data: &mut [u8]) -> Result<(), CryptoError> {
    match key.len() {
        16 => {
            let mut c = Aes128Ctr::new(key.into(), nonce.into());
            c.apply_keystream(data);
            Ok(())
        }
        32 => {
            let mut c = Aes256Ctr::new(key.into(), nonce.into());
            c.apply_keystream(data);
            Ok(())
        }
        _ => Err(CryptoError::BadKeyLength),
    }
}

// ---- RSA (PKCS#1 v1.5 + SHA-256) ----

/// Verify `signature` over `data` with a PKCS#1 DER public key
/// (the byte format produced by .NET `RSA.ExportRSAPublicKey()`).
pub fn rsa_verify(pub_key_der: &[u8], data: &[u8], signature: &[u8]) -> Result<(), CryptoError> {
    let key = RsaPublicKey::from_pkcs1_der(pub_key_der)
        .map_err(|e| CryptoError::BadKeyFormat(e.to_string()))?;
    let hashed = sha256(data);
    key.verify(Pkcs1v15Sign::new::<Sha256>(), &hashed, signature)
        .map_err(|_| CryptoError::SignatureInvalid)
}

/// Sign with a PKCS#1 DER private key (used by tests and the host `sign`
/// tool; production signing happens in the .NET tooling).
pub fn rsa_sign(priv_key_der: &[u8], data: &[u8]) -> Result<Vec<u8>, CryptoError> {
    let key = RsaPrivateKey::from_pkcs1_der(priv_key_der)
        .map_err(|e| CryptoError::BadKeyFormat(e.to_string()))?;
    let hashed = sha256(data);
    key.sign(Pkcs1v15Sign::new::<Sha256>(), &hashed)
        .map_err(|e| CryptoError::BadKeyFormat(e.to_string()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use rsa::pkcs1::EncodeRsaPrivateKey;
    use rsa::pkcs1::EncodeRsaPublicKey;

    #[test]
    fn sha256_known_vector() {
        // SHA-256("abc")
        let h = sha256(b"abc");
        assert_eq!(
            hex(&h),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
    }

    #[test]
    fn hmac_sha256_known_vector() {
        // RFC 4231 test case 2
        let h = hmac_sha256(b"Jefe", b"what do ya want for nothing?");
        assert_eq!(
            hex(&h),
            "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843"
        );
    }

    #[test]
    fn aes_ctr_roundtrip() {
        let key = [7u8; 16];
        let nonce = [9u8; 16];
        let mut data = b"secret config data".to_vec();
        let original = data.clone();
        aes_ctr_apply(&key, &nonce, &mut data).unwrap();
        assert_ne!(data, original);
        aes_ctr_apply(&key, &nonce, &mut data).unwrap();
        assert_eq!(data, original);
    }

    #[test]
    fn aes_rejects_bad_key() {
        let mut data = vec![0u8; 4];
        assert_eq!(
            aes_ctr_apply(&[0u8; 10], &[0u8; 16], &mut data),
            Err(CryptoError::BadKeyLength)
        );
    }

    #[test]
    fn rsa_sign_verify_roundtrip() {
        let mut rng = rand::thread_rng();
        let key = RsaPrivateKey::new(&mut rng, 2048).unwrap();
        let priv_der = key.to_pkcs1_der().unwrap();
        let pub_der = key.to_public_key().to_pkcs1_der().unwrap();
        let data = b"firmware image bytes";
        let sig = rsa_sign(priv_der.as_bytes(), data).unwrap();
        rsa_verify(pub_der.as_bytes(), data, &sig).unwrap();
        // Tampered data must fail.
        assert_eq!(
            rsa_verify(pub_der.as_bytes(), b"firmware image bytez", &sig),
            Err(CryptoError::SignatureInvalid)
        );
    }

    fn hex(bytes: &[u8]) -> String {
        bytes.iter().map(|b| format!("{b:02x}")).collect()
    }
}
