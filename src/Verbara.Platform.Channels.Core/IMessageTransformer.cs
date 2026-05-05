using Verbara.Platform.Core;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Channels.Core;

public interface IMessageTransformer
{
    ChannelType TargetChannel { get; }
    MessageEnvelope Transform(MessageEnvelope source);
}
