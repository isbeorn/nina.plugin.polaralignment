using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The speed dropdown offered 100-1000 while the firmware accepts 50-3000. The gap was
    /// invisible for as long as it was harmless: before firmware 1.2.1 the F feed value in a
    /// jog was parsed and thrown away, so the control did nothing and its range meant
    /// nothing. rc10 made the firmware honour F, and the list - inherited unchanged from the
    /// pre-OAPA panel - silently became a ceiling. A tester whose altitude axis runs at 1000
    /// steps per arcminute was capped at 0.6 arcmin/s with no way to ask for more, on the one
    /// axis where it mattered.
    ///
    /// These tests tie the offered range to the firmware's own constants, so the next time
    /// one moves the other has to follow.
    /// </summary>
    public class OapaSpeedRangeTest {

        private static DirectoryInfo RepoRoot() {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PolarAlignment", "NINA.Plugins.PolarAlignment.csproj"))) {
                dir = dir.Parent;
            }
            dir.Should().NotBeNull("the test must run inside the repository tree");
            return dir;
        }

        private static string PanelXaml =>
            File.ReadAllText(Path.Combine(RepoRoot().FullName, "PolarAlignment", "OAPA", "OAPAControlPanel.xaml"));

        /// <summary>
        /// The values offered for one axis' speed dropdown, in the order the panel lists them.
        /// </summary>
        private static List<int> OfferedSpeeds(string axisBinding) {
            var xaml = PanelXaml;
            var start = xaml.IndexOf($"SelectedValue=\"{{Binding {axisBinding}}}\"", StringComparison.Ordinal);
            start.Should().BeGreaterThan(0, $"the panel must contain a speed dropdown bound to {axisBinding}");
            var end = xaml.IndexOf("</ComboBox>", start, StringComparison.Ordinal);
            end.Should().BeGreaterThan(start, "the speed dropdown must be a well-formed ComboBox");

            return Regex.Matches(xaml.Substring(start, end - start), @"<s:Int32>(\d+)</s:Int32>")
                .Select(m => int.Parse(m.Groups[1].Value))
                .ToList();
        }

        /// <summary>
        /// The firmware is the beta line's working copy; its canonical home is the separate
        /// oapa-firmware repository, so a checkout without it is legitimate. Same guard as
        /// <see cref="OapaFirmwareSourceTest"/>.
        /// </summary>
        private static int FirmwareConstant(string name) {
            var path = Path.Combine(RepoRoot().FullName, "firmware", "oapa.ino");
            if (!File.Exists(path)) {
                Assert.Ignore("firmware source not present in this checkout");
            }
            var match = Regex.Match(File.ReadAllText(path), $@"{name}\s*=\s*(\d+)");
            match.Success.Should().BeTrue($"the firmware must declare {name}");
            return int.Parse(match.Groups[1].Value);
        }

        [Test]
        public void BothAxes_OfferTheSameSpeeds() {
            // An axis-specific range would be a trap: the axis that needs the high end is
            // whichever one has the heavier reduction, and that is a property of the rig.
            OfferedSpeeds("XSpeed").Should().Equal(OfferedSpeeds("YSpeed"),
                "the two speed dropdowns must offer the same values");
        }

        [Test]
        public void OfferedSpeeds_ReachTheFirmwareCeiling() {
            var ceiling = FirmwareConstant("JOG_SPEED_MAX");

            OfferedSpeeds("XSpeed").Max().Should().Be(ceiling,
                "a speed the firmware accepts but the panel will not offer is unreachable: the " +
                "user cannot type into the dropdown, so the list is the whole range they have");
        }

        [Test]
        public void OfferedSpeeds_StayAboveTheFirmwareFloor() {
            var floor = FirmwareConstant("JOG_SPEED_MIN");

            OfferedSpeeds("XSpeed").Min().Should().BeGreaterThanOrEqualTo(floor,
                "a value below the firmware floor is silently clamped, so the panel would be " +
                "showing a speed the controller never runs at");
        }

        [Test]
        public void OfferedSpeeds_AreAscendingAndDistinct() {
            var speeds = OfferedSpeeds("XSpeed");

            speeds.Should().BeInAscendingOrder("a dropdown out of order is a usability defect");
            speeds.Should().OnlyHaveUniqueItems();
        }
    }
}
