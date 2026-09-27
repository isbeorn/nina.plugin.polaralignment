namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// How an OAPA axis handles its mechanical backlash on direction reversals, from the most
    /// conservative to the most aggressive strategy. Sent to the controller ($B=), which plans
    /// the moves of its own alignment with it.
    /// </summary>
    public enum OapaBacklashMode {
        /// <summary>No compensation: plain moves (negligible measured backlash).</summary>
        Off,
        /// <summary>Single move extended by 75% of the backlash: limits the injected error when the value is overestimated.</summary>
        Soft,
        /// <summary>Single move extended by the full backlash: the engagement is part of the move, no out-and-back excursion.</summary>
        Full,
        /// <summary>Overshoot past the target and return so the final approach always comes from the engaged direction.</summary>
        Unidirectional
    }
}
