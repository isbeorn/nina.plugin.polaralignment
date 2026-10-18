using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment {
    public enum PolarAlignmentSystemType {
        None,

        /// <summary>UPAS (Avalon) hardware, driven by TPPA over a serial port.</summary>
        UPAS,

        /// <summary>OAPA hardware, driven by TPPA over a serial port or WiFi.</summary>
        OAPA,

        /// <summary>
        /// An external controller on the NINA message broker (MLAstroRPA). TPPA commands no axis itself: the
        /// selected controller drives the alignment hardware, and the run only happens while it is registered.
        /// </summary>
        External
    }

    public enum Axis {
        XAxis,
        YAxis,
        ZAxis
    }

    public enum LastDirection {
        Negative,
        Positive
    }

    public interface IPolarAlignmentSystem : IDisposable {
        bool Connected { get; }
        string Status { get; }

        float XPosition1 { get; }
        float YPosition1 { get; }
        float ZPosition1 { get; }

        float XGearRatio { get; set; }
        float YGearRatio { get; set; }
        float ZGearRatio { get; set; }

        LastDirection XLastDirection { get; }
        LastDirection YLastDirection { get; }
        LastDirection ZLastDirection { get; }

        Task MoveRelative(Axis axis, int speed, float position, CancellationToken token);
        Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token);
        Task RefreshStatus(CancellationToken token);
    }
}
