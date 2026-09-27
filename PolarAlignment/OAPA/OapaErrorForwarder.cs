using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using System;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// Listens for the error the polar-alignment instruction publishes after every stable
    /// solve and hands it on in arcminutes. It is how the OAPA controller gets its readings
    /// without any change to the alignment instruction: the message is already published.
    /// </summary>
    public sealed class OapaErrorForwarder : ISubscriber {

        /// <summary>
        /// Topic of <see cref="Instructions.PolarAlignmentErrorMessage"/>. A literal because
        /// "PolarAlignment" is both a namespace and a type here; a test pins it against the
        /// message's own Topic.
        /// </summary>
        public const string ErrorTopic = "PolarAlignmentPlugin_PolarAlignment_AlignmentError";

        private readonly Action<double, double> onError;

        /// <param name="messageBroker">May be null: tests and hosts without the plugin broker.</param>
        /// <param name="onError">Receives azimuth and altitude error in arcminutes.</param>
        public OapaErrorForwarder(IMessageBroker messageBroker, Action<double, double> onError) {
            this.onError = onError ?? throw new ArgumentNullException(nameof(onError));
            messageBroker?.Subscribe(ErrorTopic, this);
        }

        public Task OnMessageReceived(IMessage message) {
            if (message?.Topic != ErrorTopic || message.Content == null) {
                return Task.CompletedTask;
            }
            // The content is an anonymous object: AzimuthError / AltitudeError in degrees.
            var type = message.Content.GetType();
            if (type.GetProperty("AzimuthError")?.GetValue(message.Content) is not double azimuthDegrees
                || type.GetProperty("AltitudeError")?.GetValue(message.Content) is not double altitudeDegrees) {
                Logger.Warning($"OAPA: alignment error message without the expected fields ({type.Name})");
                return Task.CompletedTask;
            }
            onError(azimuthDegrees * 60.0, altitudeDegrees * 60.0);
            return Task.CompletedTask;
        }
    }
}
