using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Plugins.PolarAlignment.Bridge;
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
        private BridgeControllerRun bridgeSession;
        private string bridgeSessionEndReason = BridgeReason.UserStop;

        /// <summary>
        /// Connects the external step the way the other alignment systems are connected before the correction
        /// loop starts: the session is already open, the controller is told that the reference sweep is finished
        /// and its hardware readiness is awaited. The correction itself is then driven by the same correction
        /// loop as any other alignment system, so this replaces the hand-over prompt of the older design.
        /// </summary>
        /// <returns>
        /// False when the controller left, so the caller continues with TPPA's own correction loop. True when the
        /// controller owns the correction from here on.
        /// </returns>
        private async Task<bool> PrepareExternalStepAsync(BridgeControllerRun session, IProgress<ApplicationStatus> progress, CancellationToken token) {
            var hub = BridgeHub.Instance;
            if (hub?.IsControllerPresent != true) {
                // The controller was switched off (or went away) during the reference sweep: there is nobody to
                // hand the correction over to, so the run continues with its normal correction loop.
                Logger.Info("[Bridge] The controller is gone before the correction. Continuing with the normal correction loop.");
                return false;
            }

            // The reference sweep is finished: the controller is told, so it can bring its hardware up and report
            // readiness while the axes are still untouched.
            await session.PublishSessionStateAsync(BridgeState.WaitingForRequest, BridgeReason.MeasurementsFinished, token);

            // The operator has to read that the run is waiting for the hardware link, not still calculating the error:
            // bringing the link up takes a while (a wireless attempt, then a scan of every COM port).
            progress?.Report(GetStatus($"Waiting for {ControllerDisplay} to connect the alignment hardware"));

            // Bringing the link up takes a while (a wireless attempt, then a scan of every COM port), so it gets a
            // generous grace period: while the controller is still trying, the run waits instead of cancelling the
            // alignment, because the axes are not touched until the controller answers the first measurement.
            var readinessDeadline = DateTime.UtcNow.AddSeconds(30);
            while (!session.HasControllerFault
                   && (!session.ControllerHardwareConnected || !session.ControllerHardwareReady)
                   && hub.IsControllerPresent && DateTime.UtcNow < readinessDeadline) {
                await Task.Delay(250, token);
            }

            progress?.Report(GetStatus(string.Empty));

            if (!hub.IsControllerPresent) {
                // The controller was switched off (or went away) while it was connecting: the normal correction
                // loop continues, and the operator is told why the hand-over is gone.
                Logger.Info("[Bridge] The controller is gone. Handing the run back to the normal correction loop.");
                progress?.Report(GetStatus($"{ControllerDisplayCapitalized} is gone - continuing with the normal correction loop"));
                Notification.ShowWarning($"{ControllerDisplayCapitalized} stopped answering. Three point polar alignment continues with its normal correction loop.");
                return false;
            }

            // A controller fault is final: it tried and reported that it cannot drive the run. Being slow to
            // connect is not a fault - that case is the warning below.
            if (session.HasControllerFault) {
                var failureDetail = session.ControllerFaultDetail;
                Logger.Warning($"[Bridge] {ControllerDisplayCapitalized} could not connect to the alignment hardware: {session.ControllerFaultReason}.");

                await session.EndAsync(BridgeReason.ControllerFault,
                                       false,
                                       new BridgeSessionEndedPayload { Detail = failureDetail },
                                       CancellationToken.None);

                Notification.ShowError(
                    $"{ControllerDisplayCapitalized} system is assigned through the broker but could not connect to the alignment hardware." + Environment.NewLine +
                    (string.IsNullOrWhiteSpace(failureDetail) ? string.Empty : failureDetail + Environment.NewLine) +
                    "Polar alignment is cancelled.");
                throw new OperationCanceledException("The controller could not connect to the alignment hardware.");
            }

            if (session.ControllerHardwareConnected) {
                // The link is up: the success notification for it is shown when the run confirms the controller and its
                // hardware before the reference frames, so this only records the hand-over in the log.
                Logger.Info($"[Bridge] Handing the correction over to {ControllerDisplay}; its hardware is connected.");
            }

            if (!session.ControllerHardwareConnected || !session.ControllerHardwareReady) {
                var readinessNote = session.ControllerReadyNote;
                Logger.Warning($"[Bridge] {ControllerDisplayCapitalized} is not ready for the correction" +
                               (string.IsNullOrWhiteSpace(readinessNote) ? "." : $": {readinessNote}."));
                Notification.ShowWarning(
                    $"{ControllerDisplayCapitalized} is not ready for the correction" +
                    (string.IsNullOrWhiteSpace(readinessNote) ? "." : $": {readinessNote}.") + Environment.NewLine +
                    "Check the controller.");
            }

            return true;
        }

        /// <summary>
        /// The awaited external step of one correction loop iteration: the measurement that loop just produced is
        /// published to the controller and its single answer decides what the loop does next. A cancel, a fault or
        /// a controller that stops answering throws here, so the loop never invents an end of its own.
        /// </summary>
        private async Task<BridgeControllerAsk> RunExternalStepAsync(BridgeControllerRun session,
                                                                            BridgeMeasurementPayload measurement,
                                                                            IProgress<ApplicationStatus> progress,
                                                                            CancellationToken token) {
            // The measurement is the controller's turn now: the status says so instead of leaving the last capture
            // text on screen while the controller plans, connects its hardware or holds a move.
            progress?.Report(GetStatus($"Waiting for {ControllerDisplay}"));

            var ask = await session.PublishMeasurementAndWaitAsync(measurement, token);

            // The settle of a pause belongs to the resume, not to this step: the axes were stopped for the pause, so
            // waiting here would only add an unexplained wait inside the pause itself.
            if (ask.Kind == BridgeControllerAskKind.AdjustmentFinished
                && ask.SettleSeconds > 0
                && !string.Equals(ask.Reason, BridgeReason.Paused, StringComparison.Ordinal)) {
                // The controller finished a move: let its mechanics settle for as long as it asked (backlash
                // release, vibration). This is the same wait our own adjustment systems do, so the operator reads
                // "Settling" in the status bar instead of an unexplained pause.
                await CoreUtil.Wait(TimeSpan.FromSeconds(ask.SettleSeconds), token, progress, "Settling");
            }

            return ask;
        }

        /// <summary>
        /// Confirms that the controller and its hardware are ready before the three reference frames are captured,
        /// which is what the review asks for: those frames model the whole run, so axes that are still turning would
        /// build that model from a field that moved while it was measured. Returns null when the controller is ready,
        /// and the message for the one failure of the run when it is not - the caller throws it, so the operator reads
        /// a single error instead of one per side.
        /// </summary>
        private async Task<string> EnsureControllerIsReadyBeforeReferenceSweepAsync(BridgeControllerRun session, IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (session.HasControllerFault) {
                return await EndRunBeforeReferenceSweepAsync(session, BridgeReason.ControllerFault, "the controller reported a fault");
            }

            if (session.ControllerHardwareConnected && session.ControllerHardwareReady) {
                Logger.Info($"[Bridge] {ControllerDisplayCapitalized} reports its controller and its hardware as ready before the reference sweep.");
                return null;
            }

            progress?.Report(GetStatus($"Waiting for {ControllerDisplayCapitalized} to bring its hardware up"));
            var ready = await session.WaitForHardwareReadyAsync(TimeSpan.FromMilliseconds(session.Options.ReadyTimeoutMs), token);
            token.ThrowIfCancellationRequested();
            progress?.Report(GetStatus(string.Empty));

            if (ready && !session.HasControllerFault) {
                Logger.Info($"[Bridge] {ControllerDisplayCapitalized} reports its controller and its hardware as ready before the reference sweep.");
                return null;
            }

            if (session.HasControllerFault) {
                return await EndRunBeforeReferenceSweepAsync(session, BridgeReason.ControllerFault, "the controller reported a fault");
            }

            var detail = session.ControllerHardwareConnected
                ? "the controller is connected but reports that its axes are still busy"
                : "the controller has not connected its alignment hardware";

            Logger.Error($"[Bridge] {ControllerDisplayCapitalized} did not become ready within {session.Options.ReadyTimeoutMs} ms before the reference sweep: {detail}. The run ends instead of capturing frames that cannot be trusted.");
            return await EndRunBeforeReferenceSweepAsync(session, BridgeReason.NoControllerReady, detail);
        }

        /// <summary>
        /// Ends a run that never reached the reference frames. The run failing is the single notification for it, so this
        /// only records the reason in the log and in the session end, and returns the text the caller throws.
        /// </summary>
        private async Task<string> EndRunBeforeReferenceSweepAsync(BridgeControllerRun session, string reason, string detail) {
            await session.EndAsync(reason, false, new BridgeSessionEndedPayload {
                Achieved = false,
                Detail = detail,
                ToleranceUsedArcMin = session.Options.ToleranceArcMin
            }, CancellationToken.None);

            Logger.Error($"[Bridge] The run ended before the reference frames: {detail}.");

            return $"{ControllerDisplayCapitalized} did not become ready before the reference frames ({detail})." + Environment.NewLine +
                   "Check the controller, then run the polar alignment again.";
        }

        /// <summary>
        /// Waits for the controller to report readiness again after the run was paused, because it stopped its axes
        /// for the pause. A controller that stays busy only gets a warning: the answer deadline of the next
        /// measurement takes over from here, so a pause can never hang the run.
        /// </summary>
        private async Task RequireControllerReadyAfterPauseAsync(BridgeControllerRun session, CancellationToken token) {
            var ready = await session.WaitForHardwareReadyAsync(TimeSpan.FromMilliseconds(session.Options.ReadyTimeoutMs), token);
            if (ready) { return; }

            Logger.Warning($"[Bridge] {ControllerDisplayCapitalized} did not report readiness after the resume; the next measurement is handed over anyway and its answer deadline applies.");
        }

        /// <summary>
        /// Ends a run whose result the controller confirmed: the achieved errors go out with the session end and
        /// the operator reads the same wording TPPA uses when it finishes on its own.
        /// </summary>
        private async Task FinishBridgeSessionAsync(BridgeControllerRun session, BridgeMeasurementPayload measurement) {
            await session.EndAsync(BridgeReason.Completed, true, BuildSessionEndedDetail(measurement), CancellationToken.None);

            Logger.Info($"[Bridge] {ControllerDisplayCapitalized} confirmed the alignment within tolerance {AlignmentTolerance}'. Session completed.");
            // Older toasts are closed first, so the result is the one the operator really reads.
            Notification.CloseAll();
            Notification.ShowInformation(
                "AUTO PA COMPLETED!" + Environment.NewLine +
                "Total Error is below alignment tolerance." + Environment.NewLine +
                BuildErrorSummary() + Environment.NewLine +
                $"{ControllerDisplayCapitalized} completed the session.",
                TimeSpan.FromMinutes(1));
        }

        /// <summary>
        /// Who is on the other end of the broker, e.g. "MLAstroRPA 2.2.0.0". Every status text and
        /// notification that talks about the controller uses this so the operator sees which plugin it is.
        /// </summary>
        private static string ControllerDisplay => BridgeHub.Instance?.ControllerDisplay ?? "the external alignment controller";

        /// <summary><see cref="ControllerDisplay"/> written so it can start a sentence.</summary>
        private static string ControllerDisplayCapitalized =>
            BridgeHub.Instance?.ControllerDisplayCapitalized ?? "The external alignment controller";

        /// <summary>
        /// Creates and opens the external correction session when the operator selected an external controller as
        /// the alignment system. Nothing is detected on its own: a controller that answers on the broker only
        /// appears in the system list, and TPPA talks to it only while it is the selected system. Returns null
        /// whenever TPPA has to run its normal correction loop.
        /// </summary>
        private async Task<BridgeControllerRun> StartBridgeSessionAsync(IProgress<ApplicationStatus> progress, CancellationTokenSource runTokens) {
            var token = runTokens.Token;
            var hub = BridgeHub.Instance;
            if (hub == null) { return null; }

            if (Properties.Settings.Default.SelectedPolarAlignmentSystem != nameof(PolarAlignmentSystemType.External)) {
                Logger.Info("[Bridge] The external correction system is not the selected alignment system. Using the internal correction loop.");
                return null;
            }

            // The selection carries the broker identity of the controller, because an announced name is not unique.
            var controllerId = Guid.TryParse(Properties.Settings.Default.SelectedBridgeControllerId, out var parsedId)
                ? parsedId
                : (Guid?)null;

            if (!hub.IsRegistered(controllerId)) {
                Logger.Warning("[Bridge] The selected external controller is not registered on the broker, so no external session is started.");
                Notification.ShowWarning("The external alignment system is selected, but its controller is not connected to the message broker. The normal correction loop runs instead.");
                return null;
            }

            if (AlignmentTolerance <= 0) {
                Logger.Warning("[Bridge] The external correction system is selected but the alignment tolerance is zero, so no external session is started.");
                Notification.ShowWarning($"{hub.ControllerDisplayCapitalized} is selected as the alignment system, but the alignment tolerance is zero. The external correction mode needs a tolerance above zero, so the normal correction loop runs instead.");
                return null;
            }

            var session = new BridgeControllerRun(messageBroker, runTokens);
            session.RunFailed += (_, e) => OnBridgeRunFailed(e);
            session.AdjustmentAnnounced += (_, adjustment) => progress?.Report(GetStatus($"{ControllerDisplayCapitalized} {DescribeAdjustment(adjustment)}{BuildAdjustmentSummary(adjustment)}"));
            hub.AttachSession(session, controllerId);
            try {
                progress?.Report(GetStatus($"Waiting for {ControllerDisplay}"));
                await session.OpenAsync(AlignmentTolerance, TPAPAVM.UseContinuousErrorEstimator, token);

                // No readiness wait here: the controller is only asked to connect once the reference sweep is
                // finished (see the state published below). Waiting for it at session open would only delay
                // the run, and it would keep a session alive for a controller that was switched off meanwhile.

                // Tell the controller that the reference sweep starts, so its UI does not look stuck.
                await session.PublishSessionStateAsync(BridgeState.Measuring, BridgeReason.ReferenceSweep, token);

                progress?.Report(GetStatus(string.Empty));
                return session;
            } catch (OperationCanceledException) {
                hub.DetachSession(session);
                session.Dispose();
                throw;
            }
        }
        /// <summary>The move the controller announced, in the wording the operator reads in the status bar.</summary>
        private static string DescribeAdjustment(BridgeAdjustmentRequestPayload adjustment) =>
            adjustment?.DetectingDirection == true ? "is detecting direction" : "is adjusting";

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
        /// True when a controller end reason means the correction could not run on the hardware, so the
        /// operator gets an error instead of a plain warning.
        /// </summary>
        private static bool IsHardwareFailureReason(string reason) {
            return string.Equals(reason, BridgeReason.FirmwareDisconnected, StringComparison.Ordinal)
                   || string.Equals(reason, BridgeReason.ControllerFault, StringComparison.Ordinal)
                   || string.Equals(reason, BridgeReason.CaptureFailed, StringComparison.Ordinal);
        }

        /// <summary>
        /// The move the controller is about to make, for the status text: only the axes it really moves are
        /// listed (the controller sends no value for an axis it holds), in arcminutes like its own status line.
        /// </summary>
        private static string BuildAdjustmentSummary(BridgeAdjustmentRequestPayload adjustment) {
            if (adjustment == null) { return string.Empty; }

            var text = string.Empty;
            if (adjustment.PlannedAzimuthArcMin.HasValue) {
                text = $"NudgeAz: {Math.Abs(adjustment.PlannedAzimuthArcMin.Value):0.00}'";
            }
            if (adjustment.PlannedAltitudeArcMin.HasValue) {
                if (text.Length > 0) { text += "; "; }
                text += $"NudgeAlt: {Math.Abs(adjustment.PlannedAltitudeArcMin.Value):0.00}'";
            }

            return text.Length == 0 ? string.Empty : ": " + text;
        }

        /// <summary>
        /// Turns a controller cancel reason into a sentence the operator can act on. The raw reason is
        /// always shown next to it, so the toast never invents behaviour the controller did not report.
        /// </summary>
        private string DescribeControllerCancel(string reason) {
            switch (reason) {
                case BridgeReason.UserStop:
                    return $"STOP or FORCE STOP was pressed on {ControllerDisplay}.";
                case BridgeReason.BrokerDisabled:
                    return $"External correction was switched off on {ControllerDisplay}.";
                case BridgeReason.FirmwareDisconnected:
                    return $"{ControllerDisplayCapitalized} lost its link to the alignment hardware.";
                case BridgeReason.SessionTimeout:
                case BridgeReason.SilenceTimeout:
                    return $"The {ControllerDisplayCapitalized} controller reached its session time limit.";
                case BridgeReason.ControllerFault:
                    return $"The {ControllerDisplayCapitalized} controller reported a fault.";
                case BridgeReason.CaptureFailed:
                    return "The polar alignment measurement stayed unusable.";
                case BridgeReason.ControllerCancel:
                    return $"The {ControllerDisplayCapitalized} controller cancelled the session.";
                default:
                    return $"The {ControllerDisplayCapitalized} controller ended the session.";
            }
        }

        /// <summary>
        /// Builds the measurement published to the external controller. Only the signed arcminutes are
        /// sent: the controller derives the correction direction from the sign and, for altitude, the
        /// hemisphere flag, exactly like TPPA's own display does.
        /// </summary>
        private BridgeMeasurementPayload BuildBridgeMeasurement(string windowId,
                                                                   string status,
                                                                   bool autoFinishConditionMet,
                                                                   int consecutiveBelowTolerance) {
            var determination = TPAPAVM.PolarErrorDetermination;
            var azimuthError = determination.CurrentMountAxisAzimuthError;
            var altitudeError = determination.CurrentMountAxisAltitudeError;
            var totalError = determination.CurrentMountAxisTotalError;

            return new BridgeMeasurementPayload {
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

        private static BridgeSessionEndedPayload BuildSessionEndedDetail(BridgeMeasurementPayload measurement) {
            return new BridgeSessionEndedPayload {
                AzimuthErrorArcMin = measurement?.AzimuthErrorArcMin ?? 0,
                AltitudeErrorArcMin = measurement?.AltitudeErrorArcMin ?? 0,
                TotalErrorArcMin = measurement?.TotalErrorArcMin ?? 0
            };
        }

        /// <summary>
        /// Reports a run that the controller ended, with the name the controller announced itself: one place for
        /// the toast of every phase, because the run helper cancels the run token instead of ending a phase.
        /// </summary>
        private void OnBridgeRunFailed(BridgeRunFailedEventArgs e) {
            if (e == null) { return; }

            if (e.ControllerStoppedAnswering) {
                Notification.ShowError(
                    $"{ControllerDisplayCapitalized} stopped answering." + Environment.NewLine +
                    "Polar alignment is cancelled: the controller drives the alignment hardware and cannot confirm that it stopped.");
                return;
            }

            var message = (e.IsFault
                    ? $"{ControllerDisplayCapitalized} stopped the session on a fault."
                    : $"{ControllerDisplayCapitalized} cancelled the session.") + Environment.NewLine +
                          $"Reason: {e.Reason}" +
                          (string.IsNullOrWhiteSpace(e.Note) ? string.Empty : Environment.NewLine + e.Note);

            if (e.IsFault || IsHardwareFailureReason(e.Reason)) {
                Notification.ShowError(message);
            } else {
                Notification.ShowWarning(message, TimeSpan.FromMinutes(1));
            }
        }

        /// <summary>
        /// Ends the session if the loop did not end it already. The reason distinguishes a user cancel
        /// from a failure of the run itself.
        /// </summary>
        private async Task CloseBridgeSessionAsync(string reason = null, bool requestStop = true) {
            var hub = BridgeHub.Instance;
            // Fall back to the session the hub holds: without it a session that was never stored in the
            // field would be closed on the hub side while the controller is never asked to stop.
            var session = bridgeSession ?? hub?.Session;
            bridgeSession = null;
            if (session == null) {
                Logger.Info("[Bridge] No external session to close: no stop request was sent to the controller.");
                return;
            }

            try {
                if (session.IsActive) {
                    var endReason = reason ?? bridgeSessionEndReason;
                    // A controller that just went silent cannot acknowledge a stop request, so the
                    // handover path closes the session without waiting for one.
                    var hardwareStopStatus = requestStop
                        ? await session.RequestStopAsync(endReason, CancellationToken.None)
                        : BridgeHardwareStopStatus.Unknown;
                    await session.EndAsync(endReason, false, new BridgeSessionEndedPayload { HardwareStopStatus = hardwareStopStatus }, CancellationToken.None);
                }
            } catch (Exception ex) {
                Logger.Error($"[Bridge] Failed to close the external session cleanly: {ex.Message}");
            } finally {
                hub?.DetachSession(session);
                session.Dispose();
                bridgeSessionEndReason = BridgeReason.UserStop;
            }
        }
    }
}
