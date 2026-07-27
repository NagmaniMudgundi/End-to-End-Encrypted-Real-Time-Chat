SecureChat — End-to-End Encrypted Real-Time Chat
A one-to-one chat app where two users can message each other in real time. The server relays messages but can never read them — all encryption and decryption happens in the browser.
Stack: ASP.NET Core + SignalR (backend), Vue 3 + Vite (frontend), Web Crypto API (ECDH + AES-GCM) for end-to-end encryption.

Running it locally
You need the .NET 8 SDK and Node.js installed.
1. Backend
cd ChatApp.Server
dotnet run
Starts the SignalR hub at http://localhost:5142/chatHub.
2. Frontend
In a second terminal:
cd chat-client
npm install
npm run dev
Starts the Vue app at http://localhost:5173.
3. Try it
Open http://localhost:5173 in two different browser sessions (e.g. a normal window and an incognito window, so they don't share local state). Join with two different usernames — you'll see each other appear in the sidebar on the left. Either type the other's name in "Chat with..." or just click their name in the sidebar, then click Start Chat — see Message format below for why only one side needs to.

Message format
Every message on the wire is a single envelope shape (ChatEnvelope), sent as JSON over the SignalR hub method SendEnvelope:
{
  "type": "chat",
  "senderId": "nagmani",
  "receiverId": "snehal",
  "data": "{\"iv\":\"...\",\"ciphertext\":\"...\"}",
  "timestamp": 1731000000000
}
Field	Meaning
type	One of connect, handshake, chat, typing, presence, error
senderId	Username of the sender
receiverId	Username of the intended recipient
data	Payload — a public key for handshake, an encrypted {iv, ciphertext} blob for chat, "true"/"false" for typing, "online"/"inactive" for presence
timestamp	Client-side send time (ms since epoch)
The server (ChatHub.SendEnvelope) validates every envelope before relaying it: type must be one of the known values, senderId must be present, chat/handshake must carry data, and data is capped at 100,000 characters. Invalid envelopes are rejected with an Error message back to the sender rather than being forwarded or crashing the hub. The server only ever sees type, senderId, receiverId, and timestamp in the clear — for chat messages, data is ciphertext it cannot read.

Encryption approach
1.	On joining, each browser generates an ECDH (P-256) keypair. The private key never leaves that browser tab — it's not sent anywhere and not persisted.
2.	When either user clicks Start Chat, they send their public key via a handshake envelope, relayed (unread) through the server. The receiving side automatically derives the shared key and sends their own public key back.
3.	Each side independently runs ECDH with their own private key and the other's public key, producing an identical 256-bit AES-GCM key on both ends — the key itself is never transmitted.
4.	Every chat message is encrypted client-side with that AES-GCM key and a fresh random 96-bit IV per message, before being sent. The envelope's data field is only {iv, ciphertext} — both base64.
5.	The receiving client decrypts with the same derived key. AES-GCM includes a built-in authentication tag, so if a message is altered in transit (or the ciphertext/IV is corrupted), decryption fails outright rather than producing garbled plaintext. The UI shows "⚠️ Unreadable — decryption failed" in that case, which is the tamper-detection requirement.

What I'd improve next
Persist messages and support offline delivery, so a refresh or a temporarily offline peer doesn't lose the conversation. Roughly:
	Add a small database table (e.g. Messages) storing each envelope's senderId, receiverId, data (still ciphertext — the server would store what it already can't read, so nothing new becomes readable), and timestamp.
	When SendEnvelope finds the receiver isn't currently connected, instead of just returning an error to the sender, save the envelope to that table as "undelivered."
	When a user connects (OnConnectedAsync), check the table for any undelivered messages addressed to them and push those through before resuming normal live delivery.
	On the frontend, load message history from the backend on join instead of starting with an empty messages array, so a page refresh doesn't wipe the visible conversation (the AES key itself would still need to be re-established via a new handshake, since private keys are never persisted).

Extra Features
•	Online users sidebar. A left-hand panel lists everyone currently connected, so you can click a name instead of typing it to start or resume a conversation with them. This is purely a navigation convenience on top of the same one-to-one flow — each conversation still has its own independent ECDH handshake and AES-GCM key; clicking between names in the sidebar doesn't create a shared room or expose one conversation's messages to another. Chat history is scoped per-peer, so switching to a different name only shows that specific conversation.
