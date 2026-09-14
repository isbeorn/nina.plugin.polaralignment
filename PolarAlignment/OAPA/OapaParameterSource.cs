namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// Where a calibration parameter's current value came from. Manual values are
    /// protected: applying a calibration over them requires an explicit confirmation
    /// instead of a silent overwrite.
    /// </summary>
    public enum OapaParameterSource {
        Default,
        Manual,
        Calibrated
    }
}
