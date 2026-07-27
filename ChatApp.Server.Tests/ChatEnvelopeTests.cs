using System.Text.Json;
using ChatApp.Server.Models;
using Xunit;

namespace ChatApp.Server.Tests
{
    // These mirror the JSON options SignalR's built-in JsonHubProtocol uses on the
    // wire (camelCase, case-insensitive) so the test reflects what actually arrives
    // at the hub from the Vue client, not just System.Text.Json's PascalCase default.
    public class ChatEnvelopeTests
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        [Fact]
        public void SerializesAndDeserializes_RoundTrip_PreservesAllFields()
        {
            var original = new ChatEnvelope
            {
                Type = "chat",
                SenderId = "nagmani",
                ReceiverId = "snehal",
                Data = "{\"iv\":\"abc\",\"ciphertext\":\"xyz\"}",
                Timestamp = 1731000000000
            };

            var json = JsonSerializer.Serialize(original, Options);
            var roundTripped = JsonSerializer.Deserialize<ChatEnvelope>(json, Options);

            Assert.NotNull(roundTripped);
            Assert.Equal(original.Type, roundTripped!.Type);
            Assert.Equal(original.SenderId, roundTripped.SenderId);
            Assert.Equal(original.ReceiverId, roundTripped.ReceiverId);
            Assert.Equal(original.Data, roundTripped.Data);
            Assert.Equal(original.Timestamp, roundTripped.Timestamp);
        }

        [Fact]
        public void Deserializes_CamelCaseJsonFromTheBrowser_Correctly()
        {
            // this is what actually comes over the wire from useSignalR.js's send()
            var json = "{\"type\":\"typing\",\"senderId\":\"nagmani\",\"receiverId\":\"snehal\",\"data\":\"true\",\"timestamp\":1731000000000}";

            var envelope = JsonSerializer.Deserialize<ChatEnvelope>(json, Options);

            Assert.NotNull(envelope);
            Assert.Equal("typing", envelope!.Type);
            Assert.Equal("nagmani", envelope.SenderId);
            Assert.Equal("snehal", envelope.ReceiverId);
            Assert.Equal("true", envelope.Data);
        }

        [Fact]
        public void Deserializes_MissingOptionalFields_WithoutThrowing()
        {
            // ReceiverId and Data are nullable — a bare envelope (e.g. a "connect" ping)
            // should still deserialize without error
            var json = "{\"type\":\"connect\",\"senderId\":\"nagmani\"}";

            var envelope = JsonSerializer.Deserialize<ChatEnvelope>(json, Options);

            Assert.NotNull(envelope);
            Assert.Equal("connect", envelope!.Type);
            Assert.Equal("nagmani", envelope.SenderId);
            Assert.Null(envelope.ReceiverId);
            Assert.Null(envelope.Data);
        }

        [Fact]
        public void Deserializes_InvalidJson_ThrowsRatherThanSilentlyCorrupting()
        {
            // a genuinely malformed payload (mirrors the "invalid messages are
            // rejected safely" protocol-test requirement)
            var brokenJson = "{ this is not valid json ";

            Assert.Throws<JsonException>(() =>
                JsonSerializer.Deserialize<ChatEnvelope>(brokenJson, Options));
        }
    }
}
