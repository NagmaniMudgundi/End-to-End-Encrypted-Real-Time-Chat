# SecureChat

**One-to-one real-time chat with end-to-end encryption: the server relays messages but can never read them.**


> Two browser sessions chatting in real time: handshake, encrypted messages, typing indicator and the online-users sidebar.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Backend | ASP.NET Core (.NET 8), SignalR |
| Frontend | Vue 3, Vite |
| Encryption | Web Crypto API: ECDH (P-256) key exchange + AES-GCM (256-bit) |
| Transport | WebSockets via SignalR, JSON envelopes |

---

## Features

- **End-to-end encryption.** All encryption and decryption happens in the browser. The server only ever sees ciphertext.
- **Real-time messaging** over SignalR, with typing indicators and online/inactive presence.
- **Tamper detection.** AES-GCM authentication tags mean any altered message fails to decrypt and is flagged in the UI instead of showing garbled text.
- **Online users sidebar.** Click a name to start or resume a conversation. Each conversation has its own independent key and its own history.
- **Server-side envelope validation.** Malformed or oversized messages are rejected with an error, never forwarded.

---

## Running It Locally

**Prerequisites:** [.NET 8 SDK](https://dotnet.microsoft.com/download) and [Node.js](https://nodejs.org/) (LTS).

### 1. Start the backend

```bash
cd ChatApp.Server
dotnet run
```

The SignalR hub runs at `http://localhost:5142/chatHub`.

### 2. Start the frontend

In a second terminal:

```bash
cd chat-client
npm install
npm run dev
```

The app runs at `http://localhost:5173`.

### 3. Try it

1. Open `http://localhost:5173` in **two separate browser sessions**, such as a normal window and an incognito window, so they don't share local state.
2. Join with two different usernames. Each user appears in the other's sidebar.
3. Click the other user's name (or type it in **Chat with...**) and press **Start Chat**. Only one side needs to do this, because the other side answers the handshake automatically.
4. Send messages. They are encrypted before they leave the browser.

---

## How the Encryption Works

```
 Alice's browser                  Server (relay only)                 Bob's browser
 ───────────────                  ───────────────────                 ─────────────
 generate ECDH keypair                                                generate ECDH keypair
 send public key  ───── handshake ─────►  relays, unread  ─────►      receive Alice's key
 receive Bob's key ◄──── handshake ─────  relays, unread  ◄─────      send public key back
 derive AES-GCM key                                                   derive the same AES-GCM key
 encrypt(msg, fresh IV) ── chat ──────►   sees ciphertext only ──►    decrypt(msg)
```

1. On joining, each browser generates an **ECDH P-256 keypair**. The private key never leaves the tab and is never persisted.
2. Clicking **Start Chat** sends the user's public key in a `handshake` envelope. The receiver derives the shared key and replies with their own public key.
3. Both sides run ECDH independently and arrive at the **same 256-bit AES-GCM key**. The key itself is never transmitted.
4. Every message is encrypted with that key and a **fresh random 96-bit IV**. The payload on the wire is just `{ iv, ciphertext }`, base64-encoded.
5. If a message is altered in transit, the AES-GCM auth tag check fails and the UI shows **"⚠️ Unreadable — decryption failed"**.

---

## Message Format

Every message is one envelope shape (`ChatEnvelope`), sent as JSON to the hub method `SendEnvelope`:

```json
{
  "type": "chat",
  "senderId": "alice",
  "receiverId": "bob",
  "data": "{\"iv\":\"...\",\"ciphertext\":\"...\"}",
  "timestamp": 1731000000000
}
```

| Field | Meaning |
|---|---|
| `type` | One of `connect`, `handshake`, `chat`, `typing`, `presence`, `error` |
| `senderId` | Username of the sender |
| `receiverId` | Username of the intended recipient |
| `data` | A public key for `handshake`, an encrypted `{iv, ciphertext}` blob for `chat`, `"true"`/`"false"` for `typing`, `"online"`/`"inactive"` for `presence` |
| `timestamp` | Client-side send time (ms since epoch) |

**Server-side validation:** `type` must be a known value, `senderId` must be present, `chat` and `handshake` must carry `data`, and `data` is capped at 100,000 characters. Invalid envelopes get an `error` envelope back to the sender instead of being relayed.

---

## Roadmap

**Message persistence and offline delivery**, so a page refresh or an offline peer doesn't lose the conversation:

- [ ] Add a `Messages` table storing `senderId`, `receiverId`, `data` (still ciphertext, so nothing new becomes readable) and `timestamp`.
- [ ] When the receiver isn't connected, store the envelope as *undelivered* instead of returning an error.
- [ ] On `OnConnectedAsync`, push any undelivered messages to the user before resuming live delivery.
- [ ] Load history from the backend on join. A fresh handshake would still be needed to decrypt it, since private keys are never persisted.
