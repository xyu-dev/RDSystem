/// <summary>
/// A source whose tracking has to be fitted to the person currently wearing the headset.
///
/// Eye tracking is not like a mouse: it reports nothing at all until it has been calibrated for
/// this particular pair of eyes, and the calibration does not survive the next visitor. In a
/// gallery that happens every few minutes, all day, so the operator needs to start it from
/// inside the piece - not by quitting to Varjo Base and coming back.
/// </summary>
public interface IInteractionCalibrationSource
{
    /// <summary>False when this source has nothing to calibrate, e.g. a mouse.</summary>
    bool CanCalibrate { get; }

    /// <summary>
    /// Starts calibration for whoever is wearing the headset now. Returns false if the request
    /// could not be made at all; a true return only means it started, not that it succeeded.
    /// </summary>
    bool RequestCalibration();
}
