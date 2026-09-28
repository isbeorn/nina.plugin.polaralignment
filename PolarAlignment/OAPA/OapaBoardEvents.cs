using System;
using System.Collections.Generic;
using System.Globalization;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// The controller's own event log, copied into N.I.N.A.'s one event per "$G=": what the
    /// board did between two readings - each leg of a move, backlash included - and why a run
    /// or a calibration ended. A tester's N.I.N.A. log then holds the moves as they were made,
    /// not only as they were planned. Firmware before 1.3.1 does not know the command and
    /// acknowledges it with "ok": the copy stops for that controller.
    /// </summary>
    public sealed class OapaBoardEvents {

        /// <summary>As many as the board keeps: one drain never asks for more.</summary>
        public const int MaxPerDrain = 100;

        private uint copiedUpTo;
        private bool unsupported;

        /// <summary>The events logged since the last drain, oldest first, one line each.</summary>
        public IReadOnlyList<string> Drain(Func<uint, string> query) {
            var lines = new List<string>();
            for (var i = 0; i < MaxPerDrain && !unsupported; i++) {
                var fields = OapaControllerStatus.Parse(query(copiedUpTo), "G");
                if (!TryNumber(fields, "last", out var last)) {
                    unsupported = true;
                    break;
                }
                if (last < copiedUpTo) {
                    // The board restarted and numbers its events from 1 again.
                    copiedUpTo = 0;
                    continue;
                }
                if (!TryNumber(fields, "seq", out var seq)) {
                    break;  // nothing newer
                }
                copiedUpTo = seq;
                lines.Add(Describe(fields));
            }
            return lines;
        }

        private static bool TryNumber(IReadOnlyDictionary<string, string> fields, string key, out uint value) {
            value = 0;
            return fields.TryGetValue(key, out var text) && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        // "12.3 s leg: ALT (Y) +2.21' (+298 steps)": seconds since the board started, as it logged them.
        private static string Describe(IReadOnlyDictionary<string, string> fields) {
            var seconds = TryNumber(fields, "ms", out var ms) ? (ms / 1000.0).ToString("F1", CultureInfo.InvariantCulture) : "?";
            fields.TryGetValue("code", out var code);
            fields.TryGetValue("text", out var text);
            return $"{seconds} s {code}: {text}";
        }
    }
}
