<script setup>
    import { ref, watch, computed } from 'vue'
    import { useSignalR } from './composables/useSignalR'
    import { generateKeyPair, exportPublicKey, importPublicKey, deriveSharedKey, encryptMessage, decryptMessage } from './crypto'

    const myUserId = ref('')
    const hasJoined = ref(false)
    const messages = ref([])
    // messages carries every message across every peer you've ever chatted with in
    // this session; visibleMessages filters that down to only the conversation
    // currently open, so a message from someone you haven't selected (or don't even
    // have typed into "Chat with...") never appears on screen out of context.
    const draft = ref('')
    const otherUserId = ref('')
    const secureSessions = ref({})   // peerId -> true, one entry per peer we've completed a handshake with
    const secureSession = computed(() => !!secureSessions.value[otherUserId.value])
    const sentHandshakeTo = ref({})  // peerId -> true, once we've clicked Start Chat for them
    const awaitingPeer = computed(() => !secureSession.value && !!sentHandshakeTo.value[otherUserId.value])
    const visibleMessages = computed(() => messages.value.filter(m => m.peer === otherUserId.value))
    // Every currently-online user (excluding yourself), sourced from presenceMap —
    // which already receives everyone's presence via PresenceSnapshot/Presence
    // regardless of whether you've ever chatted with them. Clicking one just
    // fills in "Chat with..." and starts a chat exactly like typing it would.
    const onlineUsers = computed(() => {
        if (!signalRReady.value || !signalRState) return []
        const map = signalRState.presenceMap.value
        return Object.keys(map).filter(u => u !== myUserId.value && map[u] !== 'offline')
    })
    const showEmojiPicker = ref(false)

    let signalRState = null
    const signalRReady = ref(false)   // flips true right after signalRState is assigned — see join()
    let myKeyPair = null
    const aesKeys = {}   // peerId -> derived AES-GCM CryptoKey (not reactive, never rendered directly)
    const isConnected = computed(() => (signalRReady.value && signalRState) ? signalRState.connected.value : true)

    let typingStopTimeout = null
    let hasSentTypingTrue = false

    let idleTimer = null
    let myActivityState = 'online'
    const IDLE_MS = 15000

    const EMOJIS = ['😀', '😂', '😍', '👍', '🙏', '🎉', '😢', '😮', '🔥', '❤️', '🤔', '👋']

    async function join() {
        const id = myUserId.value.trim().toLowerCase()
        if (!id) return
        myUserId.value = id
        hasJoined.value = true

        myKeyPair = await generateKeyPair()
        signalRState = useSignalR(id)
        signalRReady.value = true
        await signalRState.connect()

        // Presence & typing spec asks for online / offline / inactive —
        // "offline" comes from the hub (disconnects), "inactive" is decided
        // client-side from real user activity and broadcast to the current peer
        window.addEventListener('mousemove', resetIdleTimer)
        window.addEventListener('keydown', resetIdleTimer)
        window.addEventListener('click', resetIdleTimer)
        document.addEventListener('visibilitychange', () => {
            if (document.hidden) {
                sendPresenceUpdate('inactive')
            } else {
                resetIdleTimer()
            }
        })
        resetIdleTimer()

        watch(signalRState.incoming, async (envelope) => {
            if (!envelope) return

            if (envelope.type === 'handshake') {
                const theirPublicKey = await importPublicKey(envelope.data)
                aesKeys[envelope.senderId] = await deriveSharedKey(myKeyPair.privateKey, theirPublicKey)
                secureSessions.value = { ...secureSessions.value, [envelope.senderId]: true }

                // Auto-reciprocate: always send our key back in response to an incoming
                // handshake, regardless of whether we'd already sent one before.
                //
                // This matters for reconnects: if Snehal's tab closes and reopens, she
                // gets a brand-new keypair and sends a fresh handshake. If Nagmani had
                // already marked "sent to snehal = true" from their earlier session (before
                // the guard-based sendHandshakeTo would refuse to send again), Nagmani's UI
                // would look connected (stale key from before) while Snehal never receives
                // a reply and stays stuck on "Waiting for nagmani...". Reciprocation must
                // bypass that guard — replying to a handshake is always safe and cheap.
                await sendHandshakeNow(envelope.senderId)
                return
            }

            if (envelope.type === 'chat') {
                const key = aesKeys[envelope.senderId]
                if (!key) return
                try {
                    const parsed = JSON.parse(envelope.data)
                    const plaintext = await decryptMessage(key, parsed)
                    messages.value.push({ from: envelope.senderId, peer: envelope.senderId, text: plaintext, time: Date.now() })
                } catch (err) {
                    messages.value.push({ from: envelope.senderId, peer: envelope.senderId, text: '⚠️ Unreadable — decryption failed', time: Date.now(), failed: true })
                }
            }
        })
    }

    async function sendHandshakeNow(receiver) {
        sentHandshakeTo.value = { ...sentHandshakeTo.value, [receiver]: true }

        const myPublicKey = await exportPublicKey(myKeyPair)
        const ok = await signalRState.send({
            type: 'handshake',
            senderId: myUserId.value,
            receiverId: receiver,
            data: myPublicKey,
            timestamp: Date.now()
        })

        if (!ok) {
            // send failed (e.g. offline) — don't leave it marked as sent, so it can be retried
            const updated = { ...sentHandshakeTo.value }
            delete updated[receiver]
            sentHandshakeTo.value = updated
        }
    }

    async function sendHandshakeTo(receiver) {
        // guarded version — used for the user-initiated Start Chat click, to avoid
        // re-sending needlessly if a session with this peer is already established
        if (!receiver || sentHandshakeTo.value[receiver]) return
        await sendHandshakeNow(receiver)
    }

    async function startSecureSession() {
        const receiver = otherUserId.value.trim().toLowerCase()
        if (!receiver) return
        otherUserId.value = receiver
        await sendHandshakeTo(receiver)
    }

    async function sendMessage() {
        const receiver = otherUserId.value.trim().toLowerCase()
        const key = aesKeys[receiver]
        if (!draft.value.trim() || !receiver || !key) return

        const encrypted = await encryptMessage(key, draft.value)
        const text = draft.value
        draft.value = ''
        showEmojiPicker.value = false
        stopTyping()

        const delivered = await signalRState.send({
            type: 'chat',
            senderId: myUserId.value,
            receiverId: receiver,
            data: JSON.stringify(encrypted),
            timestamp: Date.now()
        })

        // Show whether it actually reached the server instead of always showing
        // it as sent — previously this pushed a normal bubble unconditionally,
        // so a message typed while disconnected looked delivered when it wasn't.
        messages.value.push({
            from: myUserId.value,
            peer: receiver,
            text: delivered ? text : `${text} (not delivered — you're offline)`,
            time: Date.now(),
            failed: !delivered
        })
    }

    function onKeystroke() {
        const receiver = otherUserId.value.trim().toLowerCase()
        if (!receiver || !signalRState) return

        if (!hasSentTypingTrue) {
            signalRState.send({ type: 'typing', senderId: myUserId.value, receiverId: receiver, data: 'true', timestamp: Date.now() })
            hasSentTypingTrue = true
        }

        clearTimeout(typingStopTimeout)
        typingStopTimeout = setTimeout(stopTyping, 2000)
    }

    function stopTyping() {
        clearTimeout(typingStopTimeout)
        const receiver = otherUserId.value.trim().toLowerCase()
        if (hasSentTypingTrue && receiver && signalRState) {
            signalRState.send({ type: 'typing', senderId: myUserId.value, receiverId: receiver, data: 'false', timestamp: Date.now() })
        }
        hasSentTypingTrue = false
    }

    function resetIdleTimer() {
        sendPresenceUpdate('online')
        clearTimeout(idleTimer)
        idleTimer = setTimeout(() => sendPresenceUpdate('inactive'), IDLE_MS)
    }

    function sendPresenceUpdate(state) {
        if (myActivityState === state) return
        if (!signalRState || !signalRState.connected.value) return  // don't spam failed sends while offline; will retry via the next activity event once reconnected
        myActivityState = state
        // send to every peer we've ever completed a handshake with, not just
        // whichever one happens to be selected right now — otherwise switching
        // away from a chat silences inactivity updates to that peer forever
        Object.keys(secureSessions.value).forEach(peer => {
            signalRState.send({ type: 'presence', senderId: myUserId.value, receiverId: peer, data: state, timestamp: Date.now() })
        })
    }

    function switchPeer() {
        otherUserId.value = ''
    }

    async function selectUser(name) {
        otherUserId.value = name
        await startSecureSession()
    }

    function insertEmoji(emoji) {
        draft.value += emoji
    }

    function otherUserStatus() {
        if (!signalRState || !otherUserId.value) return 'offline'
        return signalRState.presenceMap.value[otherUserId.value] || 'offline'
    }

    function otherUserTyping() {
        if (!signalRState || !otherUserId.value) return false
        return signalRState.typingMap.value[otherUserId.value.trim().toLowerCase()] || false
    }

    function initials(name) {
        return name ? name.slice(0, 2).toUpperCase() : '?'
    }

    function formatTime(ts) {
        return new Date(ts).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    }
</script>

<template>
    <div v-if="!hasJoined" class="join-screen">
        <div class="join-card">
            <div class="lock-badge">🔒</div>
            <h1>SecureChat</h1>
            <p class="tagline">End-to-end encrypted. The server never sees your messages.</p>
            <input v-model="myUserId" placeholder="Enter your name" @keyup.enter="join" autofocus />
            <button class="primary-btn" @click="join">Join</button>
        </div>
    </div>

    <div v-else class="app-shell">
        <div class="chat-frame">
            <aside class="sidebar">
                <div class="sidebar-header">
                    <div class="avatar me">{{ initials(myUserId) }}</div>
                    <span class="me-label">You are <strong>{{ myUserId }}</strong></span>
                </div>
                <div class="sidebar-label">Online</div>
                <div class="sidebar-list">
                    <button v-for="u in onlineUsers"
                            :key="u"
                            class="sidebar-row"
                            :class="{ active: u === otherUserId }"
                            @click="selectUser(u)">
                        <span class="chip-avatar">{{ initials(u) }}</span>
                        <span class="sidebar-row-name">{{ u }}</span>
                        <span class="sidebar-row-dot"></span>
                    </button>
                    <p v-if="!onlineUsers.length" class="sidebar-empty">No one else online yet</p>
                </div>
            </aside>

            <div class="chat-window">

                <header class="chat-header">
                    <div class="avatar me">{{ initials(myUserId) }}</div>
                    <div class="header-info">
                        <span class="me-label">You are <strong>{{ myUserId }}</strong></span>

                        <div v-if="!secureSession" class="peer-row">
                            <input v-model="otherUserId"
                                   class="peer-input"
                                   placeholder="Chat with..."
                                   @keyup.enter="startSecureSession" />
                        </div>

                        <div v-else class="peer-active-row">
                            <span class="chatting-with">Chatting with <strong>{{ otherUserId }}</strong></span>
                            <span class="status-dot" :class="otherUserStatus()"></span>
                            <span class="status-text">
                                {{ otherUserTyping() ? 'typing…' : otherUserStatus() }}
                            </span>
                        </div>
                    </div>

                    <button v-if="!secureSession"
                            class="start-btn"
                            @click="startSecureSession"
                            title="Click to start chatting">
                        Start Chat
                    </button>
                    <button v-else
                            class="switch-btn"
                            @click="switchPeer"
                            title="Chat with someone else">
                        Switch
                    </button>
                </header>
                <p v-if="awaitingPeer" class="pending-note">
                    Waiting for {{ otherUserId }} to also start a chat with you…
                </p>
                <p v-if="hasJoined && !isConnected" class="reconnect-banner">
                    ⚠️ Connection lost — trying to reconnect…
                </p>

                <main class="messages">
                    <div v-if="!secureSession" class="system-note">
                        Enter a username above and click Start Chat (or press Enter) to begin an encrypted session.
                    </div>

                    <div v-for="(msg, i) in visibleMessages"
                         :key="i"
                         class="bubble-row"
                         :class="msg.from === myUserId ? 'mine' : 'theirs'">
                        <div class="avatar small" v-if="msg.from !== myUserId">{{ initials(msg.from) }}</div>
                        <div class="bubble" :class="{ failed: msg.failed }">
                            <span class="bubble-text">{{ msg.text }}</span>
                            <span class="bubble-time">{{ formatTime(msg.time) }}</span>
                        </div>
                    </div>

                    <div v-if="otherUserTyping()" class="bubble-row theirs">
                        <div class="avatar small">{{ initials(otherUserId) }}</div>
                        <div class="bubble typing-bubble">
                            <span class="dot"></span><span class="dot"></span><span class="dot"></span>
                        </div>
                    </div>
                </main>

                <div v-if="showEmojiPicker" class="emoji-picker">
                    <button v-for="e in EMOJIS" :key="e" class="emoji-btn" @click="insertEmoji(e)">{{ e }}</button>
                </div>

                <footer class="composer">
                    <button class="emoji-toggle" @click="showEmojiPicker = !showEmojiPicker" title="Emoji">🙂</button>
                    <input v-model="draft"
                           class="composer-input"
                           :placeholder="secureSession ? 'Type a message…' : 'Start a secure session first'"
                           :disabled="!secureSession"
                           @input="onKeystroke"
                           @keyup.enter="sendMessage" />
                    <button class="send-btn" :disabled="!secureSession || !draft.trim()" @click="sendMessage">
                        ➤
                    </button>
                </footer>

            </div>
        </div>
    </div>
</template>

<style scoped>
    * {
        box-sizing: border-box;
    }

    .join-screen, .app-shell {
        --bg: #0B1120;
        --panel: #131B2E;
        --panel-2: #17233A;
        --border: #223049;
        --text: #E2E8F0;
        --text-muted: #64748B;
        --accent-secure: #2DD4BF;
        --accent-secure-dim: #134E4A;
        --accent-warn: #FBBF24;
        --accent-danger: #F87171;
        --bubble-mine: #14746B;
        --bubble-theirs: #1E293B;
        --font-display: 'Space Grotesk', 'Segoe UI', sans-serif;
        --font-body: 'Inter', 'Segoe UI', sans-serif;
    }

    .join-screen {
        min-height: 100vh;
        display: flex;
        align-items: center;
        justify-content: center;
        background: radial-gradient(circle at 30% 20%, #10192E 0%, var(--bg) 60%);
        font-family: var(--font-body);
    }

    .join-card {
        background: var(--panel);
        border: 1px solid var(--border);
        border-radius: 20px;
        padding: 48px 40px;
        width: 340px;
        text-align: center;
        box-shadow: 0 20px 60px rgba(0,0,0,0.5);
    }

    .lock-badge {
        font-size: 40px;
        width: 72px;
        height: 72px;
        margin: 0 auto 20px;
        display: flex;
        align-items: center;
        justify-content: center;
        border-radius: 50%;
        background: var(--accent-secure-dim);
        border: 1px solid var(--accent-secure);
    }

    .join-card h1 {
        font-family: var(--font-display);
        color: var(--text);
        font-size: 26px;
        margin: 0 0 8px;
        letter-spacing: -0.02em;
    }

    .tagline {
        color: var(--text-muted);
        font-size: 13px;
        margin: 0 0 28px;
        line-height: 1.5;
    }

    .join-card input {
        width: 100%;
        padding: 12px 14px;
        border-radius: 10px;
        border: 1px solid var(--border);
        background: var(--panel-2);
        color: var(--text);
        font-family: var(--font-body);
        font-size: 14px;
        margin-bottom: 14px;
        outline: none;
        transition: border-color 0.15s;
    }

        .join-card input:focus {
            border-color: var(--accent-secure);
        }

    .primary-btn {
        width: 100%;
        padding: 12px;
        border: none;
        border-radius: 10px;
        background: var(--accent-secure);
        color: #05201C;
        font-weight: 600;
        font-family: var(--font-body);
        font-size: 14px;
        cursor: pointer;
        transition: filter 0.15s;
    }

        .primary-btn:hover {
            filter: brightness(1.1);
        }

    .app-shell {
        min-height: 100vh;
        display: flex;
        align-items: center;
        justify-content: center;
        background: var(--bg);
        font-family: var(--font-body);
        padding: 24px;
    }

    .chat-frame {
        width: 100%;
        max-width: 760px;
        height: 640px;
        display: flex;
        background: var(--panel);
        border: 1px solid var(--border);
        border-radius: 18px;
        overflow: hidden;
        box-shadow: 0 24px 70px rgba(0,0,0,0.55);
    }

    .sidebar {
        width: 240px;
        flex-shrink: 0;
        display: flex;
        flex-direction: column;
        background: var(--panel-2);
        border-right: 1px solid var(--border);
    }

    .sidebar-header {
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 16px;
        border-bottom: 1px solid var(--border);
    }

    .sidebar-label {
        padding: 12px 16px 4px;
        font-size: 11px;
        font-weight: 600;
        letter-spacing: 0.04em;
        text-transform: uppercase;
        color: var(--text-muted);
    }

    .sidebar-list {
        flex: 1;
        overflow-y: auto;
        padding: 4px 8px;
    }

    .sidebar-row {
        width: 100%;
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 8px;
        border-radius: 10px;
        border: none;
        background: transparent;
        color: var(--text);
        font-family: var(--font-body);
        font-size: 13px;
        cursor: pointer;
        transition: background 0.15s;
    }

        .sidebar-row:hover {
            background: var(--panel);
        }

        .sidebar-row.active {
            background: var(--accent-secure-dim);
            color: var(--accent-secure);
        }

    .sidebar-row-name {
        flex: 1;
        text-align: left;
        text-transform: capitalize;
    }

    .sidebar-row-dot {
        width: 8px;
        height: 8px;
        border-radius: 50%;
        background: var(--accent-secure);
        flex-shrink: 0;
    }

    .sidebar-empty {
        padding: 8px;
        font-size: 12px;
        color: var(--text-muted);
    }

    .chat-window {
        flex: 1;
        min-width: 0;
        background: var(--panel);
        display: flex;
        flex-direction: column;
        overflow: hidden;
    }

    .chat-header {
        display: flex;
        align-items: center;
        gap: 12px;
        padding: 14px 16px;
        background: var(--panel-2);
        border-bottom: 1px solid var(--border);
    }

    .avatar {
        flex-shrink: 0;
        width: 40px;
        height: 40px;
        border-radius: 50%;
        display: flex;
        align-items: center;
        justify-content: center;
        font-family: var(--font-display);
        font-weight: 600;
        font-size: 14px;
        color: #05201C;
        background: var(--accent-secure);
    }

        .avatar.small {
            width: 28px;
            height: 28px;
            font-size: 11px;
            margin-right: 6px;
        }

    .header-info {
        flex: 1;
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 4px;
    }

    .me-label {
        font-size: 11px;
        color: var(--text-muted);
    }

        .me-label strong {
            color: var(--text);
        }

    .peer-row {
        display: flex;
        align-items: center;
        gap: 6px;
    }

    .peer-input {
        flex: 1;
        min-width: 0;
        background: transparent;
        border: none;
        border-bottom: 1px solid var(--border);
        color: var(--text);
        font-family: var(--font-display);
        font-size: 15px;
        font-weight: 600;
        padding: 2px 0;
        outline: none;
    }

        .peer-input::placeholder {
            color: var(--text-muted);
            font-weight: 400;
        }

        .peer-input:focus {
            border-color: var(--accent-secure);
        }

    .peer-active-row {
        display: flex;
        align-items: center;
        gap: 6px;
    }

    .chatting-with {
        font-size: 13px;
        color: var(--text-muted);
        flex-shrink: 0;
    }

        .chatting-with strong {
            color: var(--text);
        }

    .status-dot {
        width: 8px;
        height: 8px;
        border-radius: 50%;
        background: var(--text-muted);
        flex-shrink: 0;
    }

        .status-dot.online {
            background: var(--accent-secure);
            box-shadow: 0 0 6px var(--accent-secure);
        }

        .status-dot.inactive {
            background: var(--accent-warn);
        }

        .status-dot.offline {
            background: var(--text-muted);
        }

    .status-text {
        font-size: 11px;
        color: var(--text-muted);
        text-transform: capitalize;
        flex-shrink: 0;
    }

    .start-btn, .switch-btn {
        flex-shrink: 0;
        padding: 0 16px;
        height: 40px;
        border-radius: 20px;
        border: 1px solid var(--border);
        background: var(--panel);
        color: var(--text);
        font-family: var(--font-body);
        font-size: 13px;
        font-weight: 600;
        cursor: pointer;
        transition: all 0.2s;
    }

    .chip-avatar {
        width: 20px;
        height: 20px;
        border-radius: 50%;
        background: var(--accent-secure);
        color: #06251f;
        font-size: 10px;
        font-weight: 700;
        display: flex;
        align-items: center;
        justify-content: center;
        text-transform: uppercase;
    }

    .pending-note {
        margin: 0;
        padding: 6px 16px 0;
        font-size: 11px;
        color: var(--accent-warn);
    }

    .reconnect-banner {
        margin: 0;
        padding: 8px 16px;
        font-size: 12px;
        font-weight: 600;
        color: var(--accent-warn);
        background: rgba(245, 158, 11, 0.12);
        text-align: center;
    }

    .messages {
        flex: 1;
        overflow-y: auto;
        padding: 16px;
        display: flex;
        flex-direction: column;
        gap: 10px;
        background: radial-gradient(circle at 20% 10%, rgba(45,212,191,0.04), transparent 40%), var(--bg);
    }

    .system-note {
        align-self: center;
        color: var(--text-muted);
        font-size: 12px;
        text-align: center;
        max-width: 260px;
        padding: 10px 14px;
        background: var(--panel-2);
        border-radius: 10px;
        border: 1px dashed var(--border);
    }

    .bubble-row {
        display: flex;
        align-items: flex-end;
        max-width: 78%;
    }

        .bubble-row.mine {
            align-self: flex-end;
            justify-content: flex-end;
        }

        .bubble-row.theirs {
            align-self: flex-start;
        }

    .bubble {
        padding: 9px 12px;
        border-radius: 14px;
        font-size: 14px;
        line-height: 1.4;
        color: var(--text);
        display: flex;
        flex-direction: column;
        gap: 2px;
    }

    .bubble-row.mine .bubble {
        background: var(--bubble-mine);
        border-bottom-right-radius: 4px;
    }

    .bubble-row.theirs .bubble {
        background: var(--bubble-theirs);
        border-bottom-left-radius: 4px;
    }

    .bubble.failed {
        border: 1px solid var(--accent-danger);
    }

    .bubble-text {
        word-break: break-word;
        white-space: pre-wrap;
    }

    .bubble-time {
        font-size: 10px;
        color: rgba(226,232,240,0.5);
        align-self: flex-end;
    }

    .typing-bubble {
        display: flex;
        gap: 4px;
        padding: 12px 14px;
    }

        .typing-bubble .dot {
            width: 6px;
            height: 6px;
            border-radius: 50%;
            background: var(--text-muted);
            animation: bounce 1.2s infinite;
        }

            .typing-bubble .dot:nth-child(2) {
                animation-delay: 0.2s;
            }

            .typing-bubble .dot:nth-child(3) {
                animation-delay: 0.4s;
            }

    @keyframes bounce {
        0%, 60%, 100% {
            transform: translateY(0);
            opacity: 0.4;
        }

        30% {
            transform: translateY(-4px);
            opacity: 1;
        }
    }

    .emoji-picker {
        display: flex;
        flex-wrap: wrap;
        gap: 4px;
        padding: 10px 14px;
        background: var(--panel-2);
        border-top: 1px solid var(--border);
    }

    .emoji-btn {
        background: none;
        border: none;
        font-size: 20px;
        cursor: pointer;
        padding: 4px;
        border-radius: 6px;
        transition: background 0.15s;
    }

        .emoji-btn:hover {
            background: var(--panel);
        }

    .composer {
        display: flex;
        gap: 8px;
        padding: 12px 14px;
        background: var(--panel-2);
        border-top: 1px solid var(--border);
    }

    .emoji-toggle {
        flex-shrink: 0;
        width: 42px;
        height: 42px;
        border-radius: 50%;
        border: 1px solid var(--border);
        background: var(--panel);
        font-size: 18px;
        cursor: pointer;
    }

        .emoji-toggle:hover {
            background: var(--panel-2);
        }

    .composer-input {
        flex: 1;
        padding: 11px 14px;
        border-radius: 20px;
        border: 1px solid var(--border);
        background: var(--panel);
        color: var(--text);
        font-family: var(--font-body);
        font-size: 14px;
        outline: none;
    }

        .composer-input:focus {
            border-color: var(--accent-secure);
        }

        .composer-input:disabled {
            opacity: 0.5;
            cursor: not-allowed;
        }

    .send-btn {
        width: 42px;
        height: 42px;
        border-radius: 50%;
        border: none;
        background: var(--accent-secure);
        color: #05201C;
        font-size: 16px;
        cursor: pointer;
        flex-shrink: 0;
        transition: filter 0.15s, opacity 0.15s;
    }

        .send-btn:disabled {
            opacity: 0.35;
            cursor: not-allowed;
        }

        .send-btn:not(:disabled):hover {
            filter: brightness(1.1);
        }

    @media (max-width: 640px) {
        .app-shell {
            padding: 0;
        }

        .chat-frame {
            height: 100vh;
            border-radius: 0;
            max-width: 100%;
        }

        .sidebar {
            display: none;
        }
    }
</style>