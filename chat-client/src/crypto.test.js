
import { describe, it, expect } from 'vitest'
import {
    generateKeyPair,
    exportPublicKey,
    importPublicKey,
    deriveSharedKey,
    encryptMessage,
    decryptMessage
} from './crypto'

// Helper: simulate two people (Alice and Bob) doing the ECDH handshake,
// exactly like startSecureSession() + the 'handshake' watcher do in App.vue.
async function deriveSharedKeyPairForTwoUsers() {
    const alice = await generateKeyPair()
    const bob = await generateKeyPair()

    const aliceExportedPub = await exportPublicKey(alice)
    const bobExportedPub = await exportPublicKey(bob)

    const aliceImportedBobPub = await importPublicKey(bobExportedPub)
    const bobImportedAlicePub = await importPublicKey(aliceExportedPub)

    const aliceAesKey = await deriveSharedKey(alice.privateKey, aliceImportedBobPub)
    const bobAesKey = await deriveSharedKey(bob.privateKey, bobImportedAlicePub)

    return { aliceAesKey, bobAesKey }
}

describe('crypto.js — encrypt/decrypt round trip', () => {
    it('encrypt -> decrypt returns the original plaintext', async () => {
        const { aliceAesKey, bobAesKey } = await deriveSharedKeyPairForTwoUsers()

        const original = 'hello snehal, this is a secret message'
        const encrypted = await encryptMessage(aliceAesKey, original)
        const decrypted = await decryptMessage(bobAesKey, encrypted)

        expect(decrypted).toBe(original)
    })

    it('produces a different ciphertext each time (random IV), even for the same plaintext', async () => {
        const { aliceAesKey } = await deriveSharedKeyPairForTwoUsers()

        const first = await encryptMessage(aliceAesKey, 'same message')
        const second = await encryptMessage(aliceAesKey, 'same message')

        expect(first.iv).not.toBe(second.iv)
        expect(first.ciphertext).not.toBe(second.ciphertext)
    })

    it('handles empty string and long messages', async () => {
        const { aliceAesKey, bobAesKey } = await deriveSharedKeyPairForTwoUsers()

        const long = 'x'.repeat(5000)
        for (const original of ['', long, '😀 emoji + ünïcödé']) {
            const encrypted = await encryptMessage(aliceAesKey, original)
            const decrypted = await decryptMessage(bobAesKey, encrypted)
            expect(decrypted).toBe(original)
        }
    })
})

describe('crypto.js — tamper detection', () => {
    it('fails to decrypt when the ciphertext is modified', async () => {
        const { aliceAesKey, bobAesKey } = await deriveSharedKeyPairForTwoUsers()

        const encrypted = await encryptMessage(aliceAesKey, 'do not tamper with me')

        // flip one character in the base64 ciphertext to simulate an attacker
        // (or network corruption) altering the message in transit
        const tamperedChar = encrypted.ciphertext[0] === 'A' ? 'B' : 'A'
        const tampered = {
            iv: encrypted.iv,
            ciphertext: tamperedChar + encrypted.ciphertext.slice(1)
        }

        await expect(decryptMessage(bobAesKey, tampered)).rejects.toThrow()
    })

    it('fails to decrypt when the IV is modified', async () => {
        const { aliceAesKey, bobAesKey } = await deriveSharedKeyPairForTwoUsers()

        const encrypted = await encryptMessage(aliceAesKey, 'iv integrity matters too')
        const tamperedChar = encrypted.iv[0] === 'A' ? 'B' : 'A'
        const tampered = {
            iv: tamperedChar + encrypted.iv.slice(1),
            ciphertext: encrypted.ciphertext
        }

        await expect(decryptMessage(bobAesKey, tampered)).rejects.toThrow()
    })

    it('fails to decrypt with the wrong key (e.g. message meant for someone else)', async () => {
        const { aliceAesKey } = await deriveSharedKeyPairForTwoUsers()
        const wrongPersonKeyPair = await deriveSharedKeyPairForTwoUsers() // an unrelated key

        const encrypted = await encryptMessage(aliceAesKey, 'this is only for bob')

        await expect(decryptMessage(wrongPersonKeyPair.bobAesKey, encrypted)).rejects.toThrow()
    })
})





