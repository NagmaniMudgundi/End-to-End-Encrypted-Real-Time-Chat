export async function generateKeyPair() {
  return crypto.subtle.generateKey(
    { name: 'ECDH', namedCurve: 'P-256' },
    true,
    ['deriveKey']
  )
}

export async function exportPublicKey(keyPair) {
  const raw = await crypto.subtle.exportKey('raw', keyPair.publicKey)
  return btoa(String.fromCharCode(...new Uint8Array(raw)))
}

export async function importPublicKey(base64) {
  const raw = Uint8Array.from(atob(base64), c => c.charCodeAt(0))
  return crypto.subtle.importKey('raw', raw, { name: 'ECDH', namedCurve: 'P-256' }, true, [])
}

export async function deriveSharedKey(privateKey, theirPublicKey) {
  return crypto.subtle.deriveKey(
    { name: 'ECDH', public: theirPublicKey },
    privateKey,
    { name: 'AES-GCM', length: 256 },
    false,
    ['encrypt', 'decrypt']
  )
}

export async function encryptMessage(aesKey, plaintext) {
  const iv = crypto.getRandomValues(new Uint8Array(12))
  const ciphertext = await crypto.subtle.encrypt(
    { name: 'AES-GCM', iv },
    aesKey,
    new TextEncoder().encode(plaintext)
  )
  return {
    iv: btoa(String.fromCharCode(...iv)),
    ciphertext: btoa(String.fromCharCode(...new Uint8Array(ciphertext)))
  }
}

export async function decryptMessage(aesKey, { iv, ciphertext }) {
  const ivBytes = Uint8Array.from(atob(iv), c => c.charCodeAt(0))
  const cipherBytes = Uint8Array.from(atob(ciphertext), c => c.charCodeAt(0))
  const plainBuf = await crypto.subtle.decrypt(
    { name: 'AES-GCM', iv: ivBytes },
    aesKey,
    cipherBytes
  )
  return new TextDecoder().decode(plainBuf)
}