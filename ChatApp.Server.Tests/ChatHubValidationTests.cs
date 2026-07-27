using ChatApp.Server.Models;
using Xunit;

namespace ChatApp.Server.Tests
{
    // ChatHub.IsValid is a public static method specifically so it can be tested
    // without spinning up a real Hub/HubCallerContext.
    public class ChatHubValidationTests
    {
        [Fact]
        public void ValidChatEnvelope_PassesValidation()
        {
            var envelope = new ChatEnvelope
            {
                Type = "chat",
                SenderId = "nagmani",
                ReceiverId = "snehal",
                Data = "{\"iv\":\"abc\",\"ciphertext\":\"xyz\"}"
            };

            Assert.True(ChatHub.IsValid(envelope));
        }

        [Fact]
        public void NullEnvelope_FailsValidation()
        {
            Assert.False(ChatHub.IsValid(null!));
        }

        [Theory]
        [InlineData("bogus")]
        [InlineData("")]
        public void UnknownOrEmptyType_FailsValidation(string type)
        {
            var envelope = new ChatEnvelope
            {
                Type = type,
                SenderId = "nagmani",
                ReceiverId = "snehal",
                Data = "payload"
            };

            Assert.False(ChatHub.IsValid(envelope));
        }

        [Fact]
        public void MissingSenderId_FailsValidation()
        {
            var envelope = new ChatEnvelope
            {
                Type = "chat",
                SenderId = "",
                ReceiverId = "snehal",
                Data = "payload"
            };

            Assert.False(ChatHub.IsValid(envelope));
        }

        [Theory]
        [InlineData("chat")]
        [InlineData("handshake")]
        public void ChatOrHandshake_WithoutData_FailsValidation(string type)
        {
            var envelope = new ChatEnvelope
            {
                Type = type,
                SenderId = "nagmani",
                ReceiverId = "snehal",
                Data = null
            };

            Assert.False(ChatHub.IsValid(envelope));
        }

        [Fact]
        public void TypingEnvelope_WithoutData_PassesValidation()
        {
            // typing/presence pings legitimately carry no payload —
            // IsValid() only requires Data for chat/handshake
            var envelope = new ChatEnvelope
            {
                Type = "typing",
                SenderId = "nagmani",
                ReceiverId = "snehal",
                Data = null
            };

            Assert.True(ChatHub.IsValid(envelope));
        }

        [Fact]
        public void OversizedData_FailsValidation()
        {
            var envelope = new ChatEnvelope
            {
                Type = "chat",
                SenderId = "nagmani",
                ReceiverId = "snehal",
                Data = new string('x', 100_001)
            };

            Assert.False(ChatHub.IsValid(envelope));
        }

        [Fact]
        public void DataAtExactSizeLimit_PassesValidation()
        {
            var envelope = new ChatEnvelope
            {
                Type = "chat",
                SenderId = "nagmani",
                ReceiverId = "snehal",
                Data = new string('x', 100_000)
            };

            Assert.True(ChatHub.IsValid(envelope));
        }
    }
}
