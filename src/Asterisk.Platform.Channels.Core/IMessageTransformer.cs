using Asterisk.Platform.Core;
using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Channels.Core;

public interface IMessageTransformer
{
    ChannelType TargetChannel { get; }
    MessageEnvelope Transform(MessageEnvelope source);
}
