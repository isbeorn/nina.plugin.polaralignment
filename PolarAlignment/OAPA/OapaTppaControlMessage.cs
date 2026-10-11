using NINA.Plugin.Interfaces;
using System;
using System.Collections.Generic;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// A pause or resume for the running polar alignment, on the topics the instruction
    /// already subscribes to. Only the topic matters to it; there is no content.
    /// </summary>
    public sealed class OapaTppaControlMessage(string topic) : IMessage {

        public Guid SenderId => Guid.Parse(PolarAlignmentPlugin.PluginId);

        public string Sender => nameof(PolarAlignmentPlugin);

        public DateTimeOffset SentAt { get; } = DateTimeOffset.UtcNow;

        public Guid MessageId { get; } = Guid.NewGuid();

        public DateTimeOffset? Expiration => null;

        public Guid? CorrelationId => null;

        public int Version => 1;

        public IDictionary<string, object> CustomHeaders { get; } = new Dictionary<string, object>();

        public string Topic { get; } = topic;

        public object Content => null;
    }
}
