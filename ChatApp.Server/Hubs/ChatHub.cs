using ChatApp.Server.Models;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

public class ChatHub : Hub
{
    // static: survives across Hub instances (Hub is transient — new instance per invocation)
    private static readonly ConcurrentDictionary<string, string> UserConnections = new();
    // userId -> connectionId

    private static readonly ConcurrentDictionary<string, DateTime> LastSeen = new();
    // userId -> last activity timestamp, used only for logging/debug here;
    // "inactive" detection itself is client-driven 

    // ---------- CONNECT ----------
    public override async Task OnConnectedAsync()
    {
        var userId = Context.GetHttpContext()?.Request.Query["userId"].ToString();

        if (string.IsNullOrWhiteSpace(userId))
        {
            // reject connections that didn't identify themselves
            Context.Abort();
            return;
        }

        // if this userId reconnects (e.g. after a network blip), overwrite the old
        // connectionId mapping rather than rejecting — last connection wins
        UserConnections[userId] = Context.ConnectionId;
        LastSeen[userId] = DateTime.UtcNow;

        // tell the newly connected client who is already online —
        // without this, a user who joins first never learns about users who
        // joined before them, since "Presence" only broadcasts to Clients.Others
        // at the moment of connecting
        var alreadyOnline = UserConnections.Keys.Where(u => u != userId).ToList();
        await Clients.Caller.SendAsync("PresenceSnapshot", alreadyOnline);

        await Clients.Others.SendAsync("Presence", userId, "online");
        await base.OnConnectedAsync();
    }

    // ---------- DISCONNECT ----------
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // find which userId owned this connectionId
        var entry = UserConnections.FirstOrDefault(kv => kv.Value == Context.ConnectionId);

        if (!string.IsNullOrEmpty(entry.Key))
        {
            UserConnections.TryRemove(entry.Key, out _);
            LastSeen.TryRemove(entry.Key, out _);

            try
            {
                await Clients.Others.SendAsync("Presence", entry.Key, "offline");
            }
            catch
            {
                // if broadcasting presence itself fails (e.g. transport already tearing down),
                // swallow it — we're already in the disconnect path, nothing more to do
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    // ---------- SEND / RELAY ----------
    public async Task SendEnvelope(ChatEnvelope envelope)
    {
        if (!IsValid(envelope))
        {
            await Clients.Caller.SendAsync("Error", "Invalid envelope: missing required fields or unknown type.");
            return;
        }

        // presence/typing envelopes don't need a specific receiver lookup failure to be fatal —
        // but chat/handshake do, since they're meaningless without a live recipient
        if (string.IsNullOrEmpty(envelope.ReceiverId))
        {
            await Clients.Caller.SendAsync("Error", "Missing receiver.");
            return;
        }

        if (!UserConnections.TryGetValue(envelope.ReceiverId, out var targetConnectionId))
        {
            // receiver isn't connected right now — tell the sender, don't crash, don't silently drop
            await Clients.Caller.SendAsync("Error", $"User '{envelope.ReceiverId}' is not currently connected.");
            return;
        }

        try
        {
            await Clients.Client(targetConnectionId).SendAsync("Receive", envelope);
        }
        catch (Exception)
        {
            // race condition: receiver disconnected between the TryGetValue check above
            // and this send actually going out. Don't let it bubble up and tear down
            // the caller's Hub method — tell them instead.
            await Clients.Caller.SendAsync("Error", $"Failed to deliver message to '{envelope.ReceiverId}'.");
        }
    }

    // ---------- VALIDATION ----------
    // kept as a static/pure method so it's unit-testable without mocking HubCallerContext
    public static bool IsValid(ChatEnvelope envelope)
    {
        if (envelope == null) return false;

        var validTypes = new[] { "handshake", "chat", "typing", "presence", "connect", "error" };
        if (!validTypes.Contains(envelope.Type)) return false;

        if (string.IsNullOrWhiteSpace(envelope.SenderId)) return false;

        // Data can legitimately be null for some types (e.g. a bare "connect" ping),
        // but chat/handshake must carry a payload
        if ((envelope.Type == "chat" || envelope.Type == "handshake") && string.IsNullOrWhiteSpace(envelope.Data))
            return false;

        // basic size guard — not a real DoS defense, but catches obviously broken/huge payloads
        if (envelope.Data?.Length > 100_000) return false;

        return true;
    }
}