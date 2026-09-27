using System;
using System.Collections.Generic;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// Turns the controller's loop status line into one sentence for the panel:
    /// "&lt;L|phase:moving_x|outcome:none|source:nina|moves:3|...|reason:Continuing corrections.|&gt;"
    /// becomes "moving azimuth, 3 moves - Continuing corrections."
    /// </summary>
    public static class OapaControllerStatus {

        public static IReadOnlyDictionary<string, string> Parse(string line, string kind = "L") {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var text = line?.Trim() ?? "";
            if (!text.StartsWith($"<{kind}|", StringComparison.Ordinal) || !text.EndsWith("|>", StringComparison.Ordinal)) {
                return fields;
            }
            foreach (var part in text.Substring(3, text.Length - 5).Split('|')) {
                var colon = part.IndexOf(':');
                if (colon > 0) {
                    fields[part.Substring(0, colon)] = part.Substring(colon + 1);
                }
            }
            return fields;
        }

        /// <summary>
        /// The factors of a finished controller calibration, from
        /// "&lt;K|state:done|x:15.02,+1|y:14.98,-1|reason:...|&gt;"; null unless both axes have one.
        /// </summary>
        public static (float x, float y)? CalibratedFactors(string line) {
            var f = Parse(line, "K");
            if (!f.TryGetValue("state", out var state) || state != "done") { return null; }
            static float? Factor(IReadOnlyDictionary<string, string> fields, string axis) {
                if (!fields.TryGetValue(axis, out var value)) { return null; }
                var comma = value.IndexOf(',');
                var number = comma > 0 ? value.Substring(0, comma) : value;
                return float.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var factor) && factor > 0
                    ? factor : null;
            }
            var x = Factor(f, "x");
            var y = Factor(f, "y");
            return x.HasValue && y.HasValue ? (x.Value, y.Value) : null;
        }

        /// <summary>
        /// The play a finished controller calibration measured on one axis, from
        /// "xplay:4.20,4.35,U" (entering positive, entering negative, mode): the mean of the two
        /// directions - what the controller aligns with - and the mode. Null when not measured.
        /// </summary>
        public static (float mean, OapaBacklashMode mode)? CalibratedPlay(string line, char axis) {
            var f = Parse(line, "K");
            if (!f.TryGetValue("state", out var state) || state != "done") { return null; }
            if (!f.TryGetValue(char.ToLowerInvariant(axis) + "play", out var value)) { return null; }
            var parts = value.Split(',');
            if (parts.Length != 3) { return null; }
            var c = System.Globalization.CultureInfo.InvariantCulture;
            if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, c, out var positive)
                || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, c, out var negative)
                || positive < 0 || negative < 0) {
                return null;
            }
            OapaBacklashMode? mode = parts[2] switch {
                "O" => OapaBacklashMode.Off,
                "S" => OapaBacklashMode.Soft,
                "F" => OapaBacklashMode.Full,
                "U" => OapaBacklashMode.Unidirectional,
                _ => null
            };
            return mode.HasValue ? ((positive + negative) / 2f, mode.Value) : null;
        }

        public static string Describe(string line) {
            var f = Parse(line);
            if (!f.TryGetValue("phase", out var phase)) {
                return string.IsNullOrWhiteSpace(line) ? "" : $"unreadable status: {line.Trim()}";
            }
            f.TryGetValue("moves", out var moves);
            f.TryGetValue("reason", out var reason);
            f.TryGetValue("outcome", out var outcome);
            var what = phase switch {
                "moving_x" => "moving azimuth",
                "moving_y" => "moving altitude",
                "waiting" => "waiting for a reading",
                "calibrating" => "calibrating",
                "ended" => $"ended: {outcome?.Replace('_', ' ')}",
                _ => phase
            };
            var sentence = $"{what}, {moves ?? "0"} moves";
            return string.IsNullOrEmpty(reason) ? sentence : $"{sentence} - {reason}";
        }
    }
}
