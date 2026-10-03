namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// An OAPA controller that runs the alignment itself (firmware 1.3.0+): TPPA measures the
    /// error, the plugin forwards it, and the controller decides every move - which axis, how
    /// far, the backlash, when to wait and when it is done.
    /// </summary>
    public interface IOapaAlignmentController {

        /// <summary>True when the connected firmware can run the alignment.</summary>
        bool RunsAlignment { get; }

        /// <summary>The version the connected firmware reports, or null.</summary>
        string FirmwareVersion { get; }

        /// <summary>The largest move cap the connected controller accepts, in arcminutes.</summary>
        float MoveCapLimit { get; }

        /// <summary>Forwards one TPPA error reading, in arcminutes; returns the controller's reply.</summary>
        string ForwardError(double azimuthArcmin, double altitudeArcmin);

        /// <summary>
        /// Gives the controller the tolerance the running alignment finishes at, in arcminutes,
        /// when it differs from the one last given.
        /// </summary>
        void SyncTolerance(double arcmin);

        /// <summary>Stops the controller's alignment run, and a calibration the plugin feeds.</summary>
        string StopAlignment();

        /// <summary>The controller's one-line loop status ("&lt;L|phase:...|&gt;").</summary>
        string AlignmentStatus();

        /// <summary>Asks the controller to calibrate before its next alignment (or now, while readings arrive).</summary>
        string RequestCalibration();

        /// <summary>The controller's calibration state and result ("&lt;K|state:...|&gt;").</summary>
        string ControllerCalibrationStatus();

        /// <summary>Calibrate on the readings that follow and stop there: they are field displacements, not a polar error.</summary>
        string RequestCalibrationOnly();

        /// <summary>Stops a calibration in progress.</summary>
        string StopCalibration();

        /// <summary>
        /// The controller's event log, one event per call ("&lt;G|seq:5|last:7|...|&gt;"): the oldest
        /// event after <paramref name="afterSequence"/>. Firmware before 1.3.1 does not know it.
        /// </summary>
        string BoardEvent(uint afterSequence);
    }
}
