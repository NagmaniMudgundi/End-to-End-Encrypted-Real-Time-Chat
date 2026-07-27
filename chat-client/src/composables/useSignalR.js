import { ref } from 'vue'
import * as signalR from '@microsoft/signalr'

export function useSignalR(userId) {
    const connection = ref(null)
    const connected = ref(false)
    const incoming = ref(null)
    const presenceMap = ref({})
    const typingMap = ref({})

    // Default withAutomaticReconnect() only retries 4 times (0s, 2s, 10s, 30s)
    // then gives up permanently — if the backend takes longer than ~42s to come
    // back up, the client would be stuck disconnected forever with no way to
    // recover short of a full page refresh (which would also wipe the AES keys).
    // This policy keeps retrying indefinitely, ramping up to a 30s interval.
    class IndefiniteRetryPolicy {
        nextRetryDelayInMilliseconds(retryContext) {
            const delays = [0, 2000, 5000, 10000, 15000, 30000]
            return delays[Math.min(retryContext.previousRetryCount, delays.length - 1)]
        }
    }

    async function connect() {
        connection.value = new signalR.HubConnectionBuilder()
            .withUrl(`http://localhost:5142/chatHub?userId=${userId}`)
            .withAutomaticReconnect(new IndefiniteRetryPolicy())
            .build()

        connection.value.on('Receive', (envelope) => {
            incoming.value = envelope

            if (envelope.type === 'typing') {
                typingMap.value = { ...typingMap.value, [envelope.senderId]: envelope.data === 'true' }
            }

            if (envelope.type === 'presence') {
                presenceMap.value = { ...presenceMap.value, [envelope.senderId]: envelope.data }
            }
        })

        // NEW: populate presence map with everyone who was already online when we joined
        connection.value.on('PresenceSnapshot', (onlineUsers) => {
            const snapshot = {}
            onlineUsers.forEach(u => { snapshot[u] = 'online' })
            presenceMap.value = { ...presenceMap.value, ...snapshot }
        })

        connection.value.on('Presence', (uid, status) => {
            presenceMap.value = { ...presenceMap.value, [uid]: status }
        })

        connection.value.on('Error', (message) => {
            console.warn('SERVER ERROR:', message)
        })

        // These three were missing entirely: without them, `connected` gets set
        // to true once on the initial handshake and then never updated again —
        // so after any network drop, the UI has no way to know the connection
        // is gone, and every send() attempt after that fails silently/repeatedly
        // instead of being visibly blocked.
        connection.value.onreconnecting(() => { connected.value = false })
        connection.value.onreconnected(() => { connected.value = true })
        connection.value.onclose(() => { connected.value = false })

        try {
            await connection.value.start()
            connected.value = true

            // Dev-only: exposes the raw connection so malformed payloads can be
            // sent directly from the browser console for manual protocol testing.
            // import.meta.env.DEV is false in a production build, so this never
            // ships — it's stripped out entirely.
            if (import.meta.env.DEV) {
                window.__signalRConnection = connection.value
            }
        } catch (err) {
            console.error('SignalR connection failed:', err)
        }
    }

    function disconnect() {
        connection.value?.stop()
    }

    // Returns true if the envelope was actually handed to the server, false if
    // it couldn't be (e.g. connection currently down) — callers can use this to
    // avoid showing a message as "sent" when it never actually went anywhere.
    async function send(envelope) {
        if (!connection.value || !connected.value) return false
        try {
            await connection.value.invoke('SendEnvelope', envelope)
            return true
        } catch (err) {
            console.error('Send invoke failed:', err)
            return false
        }
    }

    return { connected, incoming, presenceMap, typingMap, connect, disconnect, send }
}