namespace ChatApp.Server.Models
{
    
    public class ChatEnvelope
    {
        public string Type { get; set; } = string.Empty;      // "handshake" | "chat" | "typing" | "presence" | "error"
        public string SenderId { get; set; } = string.Empty;
        public string? ReceiverId { get; set; }
        public string? Data { get; set; }
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
