using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Plugins.PolarAlignment.External;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Instructions {
    /// <summary>
    /// External correction counterpart of <see cref="PolarAlignment"/>. It opens the broker session,
    /// publishes every measurement to the external alignment controller and waits for that controller's
    /// requests. The controller owns the alignment hardware, so nothing in this file commands an axis:
    /// these members are only used by the sequence item, never by the plugin options or the dockable.
    /// </summary>
    public partial class PolarAlignment {
        private ExternalCorrectionSession externalSession;
        private string externalSessionEndReason = ExternalCorrectionReason.UserStop;

        /// <summary>True when the controller vanished during a session and the normal loop has to take over.</summary>
        private bool externalControllerLost;

        /// <summary>
        /// Who is on the other end of the broker, e.g. "MLAstroRPA 2.2.0.0". Every status text and
        /// notification that talks about the controller uses this so the operator sees which plugin it is.
        /// </summary>
        private static string ControllerDisplay => ExternalCorrectionHub.Instance?.ControllerDisplay ?? "the external alignment controller";

        /// <summary><see cref="ControllerDisplay"/> written so it can start a sentence.</summary>
        private static string ControllerDisplayCapitalized =>
            ExternalCorrectionHub.Instance?.ControllerDisplayCapitalized ?? "The external alignment controller";

        /// <summary>
        /// Creates and opens the external correction session when a controller is connected. The mode is
        /// detected, not configured: a controller announces itself every few seconds while it is
        /// enabled, so the presence of those announcements is the handshake. Returns null whenever TPPA
        /// has to run its normal correction loop.
        /// </summary>
        private async Task<ExternalCorrectionSession> StartExternalCorrectionSessionAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var hub = ExternalCorrectionHub.Instance;
            if (hub == null) { return null; }

            if (!hub.IsControllerPresent) {
                Logger.Info("[ExternalCorrection] No external alignment controller is connected. Using the internal correction loop.");
                return null;
            }

            if (AlignmentTolerance <= 0) {
                Logger.Warning("[ExternalCorrection] A controller is connected but the alignment tolerance is zero, so no external session is started.");
                Notification.ShowWarning($"{hub.ControllerDisplayCapitalized} is connected, but the alignment tolerance is zero. The external correction mode needs a tolerance above zero, so the normal correction loop runs instead.");
                return null;
            }

            externalControllerLost = false;
            var session = new ExternalCorrectionSession(messageBroker);
            hub.AttachSession(session);
            try {
                progress?.Report(GetStatus($"Waiting for {ControllerDisplay}"));
                await session.OpenAsync(AlignmentTolerance, TPAPAVM.UseContinuousErrorEstimator, token);

                var ready = await session.WaitForControllerReadyAsync(token);
                if (!ready) {
                    await session.EndAsync(ExternalCorrectionReason.NoControllerReady,
                                           false,
                                           new ExternalSessionEndedPayload { Detail = "No external alignment controller became ready in time." },
                                           token);
                    hub.DetachSession(session);
                    session.Dispose();
                    Logger.Warning("[ExternalCorrection] No controller became ready in time. Falling back to the internal correction loop.");
                    Notification.ShowWarning($"{hub.ControllerDisplayCapitalized} assigned but not ready. Three point polar alignment continues with its normal correction loop.");
                    return null;
                }

                // Tell the controller that the reference sweep starts, so its UI does not look stuck.
                await session.PublishSessionStateAsync(ExternalCorrectionState.Measuring, ExternalCorrectionReason.ReferenceSweep, token);

                progress?.Report(GetStatus(string.Empty));
                return session;
            } catch (OperationCanceledException) {
                hub.DetachSession(session);
                session.Dispose();
                throw;
            }
        }
        /// <summary>
        /// External correction loop. Every capture waits for the external controller and TPPA never
        /// finishes because of the measured error: it only publishes the tolerance flags and ends the
        /// session when the controller asks for completion, cancels or stops answering.
        /// </summary>
        private async Task RunExternalCorrectionAsync(ExternalCorrectionSession session,
                                                      IProgress<ApplicationStatus> progress,
                                                      CancellationToken token) {
            // The operator reads the first polar error before the controller does: the run holds here
            // until Resume, so the reference sweep result is never published to the controller on its own.
            await WaitForHandoverResumeAsync(session, progress, token);

            var autoFinishGate = new AutoFinishGate(2);
            var firstMeasurementBelowTolerance = Math.Abs(TPAPAVM.PolarErrorDetermination.CurrentMountAxisTotalError.ArcMinutes) <= AlignmentTolerance;
            var autoFinishConditionMet = autoFinishGate.Register(firstMeasurementBelowTolerance);

            await PublishExternalMeasurementAsync(session,
                                                  isFirstMeasurement: true,
                                                  windowId: null,
                                                  status: ExternalMeasurementStatus.Valid,
                                                  autoFinishConditionMet: autoFinishConditionMet,
                                                  consecutiveBelowTolerance: autoFinishGate.Consecutive,
                                                  token: token);

            while (!token.IsCancellationRequested) {
                // While the operator holds the run, TPPA neither captures nor answers requests, but it keeps
                // watching: a cancel or a controller that disappears still ends the session.
                await WaitWhilePausedAsync(session, progress, token);

                var request = await session.WaitForControllerRequestAsync(token);
                switch (request.Kind) {
                    case ExternalControllerRequestKind.ControllerReady:
                    case ExternalControllerRequestKind.StopAcknowledged:
                        continue;

                    case ExternalControllerRequestKind.AdjustWindow: {
                        var grantedWindowId = await session.GrantWindowAsync(request, token);
                        Logger.Info($"External controller holds capture window {grantedWindowId} for measurement {request.MeasurementId}.");
                        progress?.Report(GetStatus($"{ControllerDisplayCapitalized} is adjusting"));
                        continue;
                    }

                    case ExternalControllerRequestKind.RequestMeasurement: {
                        if (request.Duplicate) {
                            Logger.Info("[ExternalCorrection] Ignored duplicated measurement request.");
                            continue;
                        }
                        var windowId = request.WindowId ?? session.CurrentWindowId;
                        session.CloseWindow(request.MeasurementRequest?.Reason ?? ExternalCorrectionReason.StepFinished);
                        var measurement = await CaptureExternalMeasurementAsync(session, windowId, autoFinishGate, progress, token);
                        await session.PublishMeasurementAsync(measurement, token);
                        continue;
                    }

                    case ExternalControllerRequestKind.RequestCompletion: {
                        session.EnterVerifying();
                        session.CloseWindow(ExternalCorrectionReason.CompletionRequested);
                        progress?.Report(GetStatus("Verifying final polar alignment"));
                        var measurement = await CaptureExternalMeasurementAsync(session, request.WindowId, autoFinishGate, progress, token);
                        if (measurement.Status == ExternalMeasurementStatus.Valid
                            && autoFinishGate.Consecutive >= autoFinishGate.RequiredConsecutive) {
                            await session.EndAsync(ExternalCorrectionReason.Completed, true, BuildSessionEndedDetail(measurement), token);
                            Logger.Info($"[ExternalCorrection] Alignment confirmed within tolerance {AlignmentTolerance}'. Session completed.");
                            // Same order as the hand-over prompt: clear the older toasts, then report the result.
                            Notification.CloseAll();
                            Notification.ShowInformation(
                                "SUCCESSFUL!" + Environment.NewLine +
                                "Total Error is below alignment tolerance." + Environment.NewLine +
                                BuildErrorSummary() + Environment.NewLine +
                                $"{ControllerDisplayCapitalized} completed the session.",
                                TimeSpan.FromMinutes(1));
                            return;
                        }
                        Logger.Info($"[ExternalCorrection] Completion request not confirmed ({Math.Round(measurement.TotalErrorArcMin, 2)}' vs tolerance {AlignmentTolerance}'). Continuing the session.");
                        await session.PublishMeasurementAsync(measurement, token);
                        continue;
                    }

                    case ExternalControllerRequestKind.Cancel: {
                        var reason = request.Cancel?.Reason ?? ExternalCorrectionReason.ControllerCancel;
                        var note = request.Cancel?.Note;
                        var faulted = request.Fault != null;

                        Logger.Warning($"[ExternalCorrection] {ControllerDisplayCapitalized} {(faulted ? "stopped" : "cancelled")} the session: {reason}" +
                                       (string.IsNullOrWhiteSpace(note) ? "." : $" ({note})."));

                        await session.EndAsync(reason, false, new ExternalSessionEndedPayload { Detail = note }, token);

                        // The reason is what the operator needs to act on, so both the sentence and the raw
                        // reason go into one toast instead of leaving it in the log only.
                        Notification.CloseAll();
                        Notification.ShowWarning(
                            (faulted ? $"{ControllerDisplayCapitalized} stopped the session on a fault." : $"{ControllerDisplayCapitalized} cancelled the session.") + Environment.NewLine +
                            DescribeControllerCancel(reason) + Environment.NewLine +
                            (string.IsNullOrWhiteSpace(note) ? string.Empty : note + Environment.NewLine) +
                            $"Reason: {reason}",
                            TimeSpan.FromMinutes(1));
                        return;
                    }

                    case ExternalControllerRequestKind.ExternalLost:
                        Logger.Warning("[ExternalCorrection] External controller stopped answering. Handing the run back to the normal correction loop.");
                        externalControllerLost = true;
                        return;

                    case ExternalControllerRequestKind.SessionTimeout:
                        Logger.Warning("[ExternalCorrection] Session safety time limit reached. Asking the controller to stop.");
                        await StopExternalSessionAsync(session, ExternalCorrectionReason.SessionTimeout, token);
                        return;

                    case ExternalControllerRequestKind.WindowExpired:
                        progress?.Report(GetStatus($"Capture window expired - waiting for {ControllerDisplay}"));
                        continue;

                    default:
                        continue;
                }
            }

            token.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Pauses the run right after the reference sweep so the operator can read the initial polar
        /// error before the external controller takes over. The session is already open, but the first
        /// measurement is only published to the controller once Resume is pressed.
        /// </summary>
        private async Task WaitForHandoverResumeAsync(ExternalCorrectionSession session, IProgress<ApplicationStatus> progress, CancellationToken token) {
            // Older toasts are closed first so this prompt, with the measured error in it, is the one the
            // operator actually reads.
            Notification.CloseAll();
            Notification.ShowInformation(
                $"{ControllerDisplayCapitalized} is connected." + Environment.NewLine +
                "Routine PAUSING" + Environment.NewLine +
                BuildErrorSummary() + Environment.NewLine +
                "Check the initial polar error, then press RESUME to hand the correction over to the controller.",
                TimeSpan.FromMinutes(1));
            Pause();
            await WaitWhilePausedAsync(session, progress, token);
        }

        /// <summary>
        /// Holds the loop while the operator paused the run - during the hand-over prompt and between two
        /// captures - but keeps watching the controller: a cancel, a fault or a controller that disappears
        /// still ends the session instead of waiting for a resume that may never come. Ordinary requests
        /// stay in the session queue for the resumed loop.
        /// </summary>
        private async Task WaitWhilePausedAsync(ExternalCorrectionSession session, IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (!IsPausing) { return; }

            IsPaused = true;
            progress?.Report(GetStatus("Paused"));
            try {
                while (!token.IsCancellationRequested && IsPausing) {
                    if (session.HasPendingTerminalRequest) {
                        Logger.Info("[ExternalCorrection] The controller ended the session while the run was paused.");
                        return;
                    }

                    if (ExternalCorrectionHub.Instance?.IsControllerPresent != true) {
                        Logger.Warning("[ExternalCorrection] The controller is gone while the run was paused.");
                        externalControllerLost = true;
                        return;
                    }

                    await Task.Delay(150, token);
                }
            } finally {
                IsPaused = false;
                progress?.Report(GetStatus(string.Empty));
            }
        }

        /// <summary>
        /// The tolerance in use plus the current azimuth, altitude and total error, formatted like TPPA's
        /// own error display so the notifications and the user interface never disagree.
        /// </summary>
        private string BuildErrorSummary() {
            var determination = TPAPAVM.PolarErrorDetermination;
            return $"Tolerance: {AlignmentTolerance}'{Environment.NewLine}" +
                   $"Azimuth Error: {Math.Round(determination.CurrentMountAxisAzimuthError.ArcMinutes, 2)}'{Environment.NewLine}" +
                   $"Altitude Error: {Math.Round(determination.CurrentMountAxisAltitudeError.ArcMinutes, 2)}'{Environment.NewLine}" +
                   $"Total Error: {Math.Round(determination.CurrentMountAxisTotalError.ArcMinutes, 2)}'";
        }

        /// <summary>
        /// Turns a controller cancel reason into a sentence the operator can act on. The raw reason is
        /// always shown next to it, so the toast never invents behaviour the controller did not report.
        /// </summary>
        private string DescribeControllerCancel(string reason) {
            switch (reason) {
                case ExternalCorrectionReason.UserStop:
                    return$"";
                case ExternalCorrectionReason.BrokerDisabled:
                    return$"";
                case ExternalCorrectionReason.FirmwareDisconnected:
                    return$"";
                case ExternalCorrectionReason.SessionTimeout:
                case ExternalCorrectionReason.SilenceTimeout:
                    return $"The {ControllerDisplayCapitalized} controller reached its session time limit.";
                case ExternalCorrectionReason.ControllerFault:
                    return $"The {ControllerDisplayCapitalized} controller reported a fault.";
                case ExternalCorrectionReason.CaptureFailed:
                    return "The polar alignment measurement stayed unusable.";
                case ExternalCorrectionReason.ControllerCancel:
                    return $"The {ControllerDisplayCapitalized} controller cancelled the session.";
                default:
                    return $"The {ControllerDisplayCapitalized} controller ended the session.";
            }
        }

        private async Task<ExternalMeasurementPayload> CaptureExternalMeasurementAsync(ExternalCorrectionSession session,
                                                                                       string windowId,
                                                                                       AutoFinishGate autoFinishGate,
                                                                                       IProgress<ApplicationStatus> progress,
                                                                                       CancellationToken token) {
            var solve = await Solve(TPAPAVM, 0, progress, token);
            if (!solve.Success) {
                return BuildExternalMeasurement(windowId, ExternalMeasurementStatus.CaptureFailed, false, autoFinishGate.Consecutive);
            }

            var estimateStable = await TPAPAVM.UpdateDetails(solve, progress, token);

            var azimuthError = TPAPAVM.PolarErrorDetermination.CurrentMountAxisAzimuthError;
            var altitudeError = TPAPAVM.PolarErrorDetermination.CurrentMountAxisAltitudeError;
            var totalError = TPAPAVM.PolarErrorDetermination.CurrentMountAxisTotalError;
            Logger.Info($"Calculated Error: Az: {azimuthError}, Alt: {altitudeError}, Tot: {totalError}");

            // The legacy error message stays published so other subscribers keep working.
            await messageBroker.Publish(new PolarAlignmentErrorMessage(Guid.NewGuid(),
                                                                      altitudeError: altitudeError.Degree,
                                                                      azimuthError: azimuthError.Degree,
                                                                      totalError: totalError.Degree));

            if (!estimateStable) {
                Logger.Warning("[ExternalCorrection] Publishing an unstable measurement so the controller is not left waiting.");
                return BuildExternalMeasurement(windowId, ExternalMeasurementStatus.Unstable, false, autoFinishGate.Consecutive);
            }

            var belowTolerance = Math.Abs(totalError.ArcMinutes) <= AlignmentTolerance;
            var autoFinishConditionMet = autoFinishGate.Register(belowTolerance);
            if (autoFinishConditionMet) {
                Logger.Info($"Total Error is below alignment tolerance ({AlignmentTolerance}') for {autoFinishGate.Consecutive} consecutive solves. The external controller decides when to finish.");
            }

            return BuildExternalMeasurement(windowId, ExternalMeasurementStatus.Valid, autoFinishConditionMet, autoFinishGate.Consecutive);
        }

        private async Task PublishExternalMeasurementAsync(ExternalCorrectionSession session,
                                                          bool isFirstMeasurement,
                                                          string windowId,
                                                          string status,
                                                          bool autoFinishConditionMet,
                                                          int consecutiveBelowTolerance,
                                                          CancellationToken token) {
            var measurement = BuildExternalMeasurement(windowId, status, autoFinishConditionMet, consecutiveBelowTolerance);
            measurement.IsFirstMeasurement = isFirstMeasurement;
            await session.PublishMeasurementAsync(measurement, token);
        }

        /// <summary>
        /// Builds the measurement published to the external controller. Only the signed arcminutes are
        /// sent: the controller derives the correction direction from the sign and, for altitude, the
        /// hemisphere flag, exactly like TPPA's own display does.
        /// </summary>
        private ExternalMeasurementPayload BuildExternalMeasurement(string windowId,
                                                                   string status,
                                                                   bool autoFinishConditionMet,
                                                                   int consecutiveBelowTolerance) {
            var determination = TPAPAVM.PolarErrorDetermination;
            var azimuthError = determination.CurrentMountAxisAzimuthError;
            var altitudeError = determination.CurrentMountAxisAltitudeError;
            var totalError = determination.CurrentMountAxisTotalError;

            return new ExternalMeasurementPayload {
                MeasurementId = Guid.NewGuid().ToString("N"),
                WindowId = windowId,
                Status = status,
                ToleranceReached = Math.Abs(totalError.ArcMinutes) <= AlignmentTolerance,
                AutoFinishConditionMet = autoFinishConditionMet,
                ConsecutiveBelowTolerance = consecutiveBelowTolerance,
                AzimuthErrorArcMin = azimuthError.ArcMinutes,
                AltitudeErrorArcMin = altitudeError.ArcMinutes,
                TotalErrorArcMin = totalError.ArcMinutes,
                ToleranceArcMin = AlignmentTolerance,
                Northern = Northern,
                ContinuousEstimation = TPAPAVM.UseContinuousErrorEstimator,
                TimestampUtc = DateTimeOffset.UtcNow
            };
        }

        private static ExternalSessionEndedPayload BuildSessionEndedDetail(ExternalMeasurementPayload measurement) {
            return new ExternalSessionEndedPayload {
                AzimuthErrorArcMin = measurement?.AzimuthErrorArcMin ?? 0,
                AltitudeErrorArcMin = measurement?.AltitudeErrorArcMin ?? 0,
                TotalErrorArcMin = measurement?.TotalErrorArcMin ?? 0
            };
        }

        private async Task StopExternalSessionAsync(ExternalCorrectionSession session, string reason, CancellationToken token) {
            var hardwareStopStatus = await session.RequestStopAsync(reason, token);
            await session.EndAsync(reason, false, new ExternalSessionEndedPayload { HardwareStopStatus = hardwareStopStatus }, token);
        }

        /// <summary>
        /// Ends the session if the loop did not end it already. The reason distinguishes a user cancel
        /// from a failure of the run itself.
        /// </summary>
        private async Task CloseExternalCorrectionSessionAsync(string reason = null, bool requestStop = true) {
            var hub = ExternalCorrectionHub.Instance;
            // Fall back to the session the hub holds: without it a session that was never stored in the
            // field would be closed on the hub side while the controller is never asked to stop.
            var session = externalSession ?? hub?.Session;
            externalSession = null;
            if (session == null) {
                Logger.Info("[ExternalCorrection] No external session to close: no stop request was sent to the controller.");
                return;
            }

            try {
                if (session.IsActive) {
                    var endReason = reason ?? externalSessionEndReason;
                    // A controller that just went silent cannot acknowledge a stop request, so the
                    // handover path closes the session without waiting for one.
                    var hardwareStopStatus = requestStop
                        ? await session.RequestStopAsync(endReason, CancellationToken.None)
                        : ExternalHardwareStopStatus.Unknown;
                    await session.EndAsync(endReason, false, new ExternalSessionEndedPayload { HardwareStopStatus = hardwareStopStatus }, CancellationToken.None);
                }
            } catch (Exception ex) {
                Logger.Error($"[ExternalCorrection] Failed to close the external session cleanly: {ex.Message}");
            } finally {
                hub?.DetachSession(session);
                session.Dispose();
                externalSessionEndReason = ExternalCorrectionReason.UserStop;
            }
        }
    }
}
