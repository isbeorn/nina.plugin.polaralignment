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
        /// Result of the hand-over point: the controller took the correction over, or there was nobody to hand
        /// it over to, in which case the run keeps TPPA's own correction loop. This single outcome replaces the
        /// flags that used to describe a vanished controller and a sweep that needed no correction.
        /// </summary>
        private enum HandOverOutcome {
            HandedOver,
            NoController
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
        /// <summary>
        /// External correction step. Every capture waits for the external controller and TPPA never
        /// finishes because of the measured error: it only publishes the tolerance flags and ends the
        /// session when the controller asks for completion, cancels or stops answering.
        /// </summary>
        /// <returns>
        /// True when the external step never took over - the reference sweep was already inside the tolerance,
        /// or the controller left before the hand-over - so the caller continues with TPPA's own correction
        /// loop. False when the step ran, which means the run is over.
        /// </returns>
        private async Task<bool> RunBridgeAsync(BridgeControllerRun session,
                                                      IProgress<ApplicationStatus> progress,
                                                      CancellationToken token) {
            // From here on the controller's answers are awaited one by one, so a cancel, a fault or a controller
            // that goes quiet ends the run through the run token instead of aborting a phase of its own.

            // The three reference points are already inside the tolerance: this run finishes under TPPA's own
            // auto-finish, exactly like a run without a controller. Nothing is handed over and the controller
            // is never asked to bring its hardware up.
            var sweepTotalError = Math.Abs(TPAPAVM.PolarErrorDetermination.CurrentMountAxisTotalError.ArcMinutes);
            if (sweepTotalError <= AlignmentTolerance) {
                Logger.Info($"[Bridge] The reference sweep is already within tolerance ({Math.Round(sweepTotalError, 2)}' <= {AlignmentTolerance}'). Finishing without handing the correction over.");

                await session.EndAsync(BridgeReason.Completed,
                                       true,
                                       new BridgeSessionEndedPayload {
                                           AzimuthErrorArcMin = TPAPAVM.PolarErrorDetermination.CurrentMountAxisAzimuthError.ArcMinutes,
                                           AltitudeErrorArcMin = TPAPAVM.PolarErrorDetermination.CurrentMountAxisAltitudeError.ArcMinutes,
                                           TotalErrorArcMin = TPAPAVM.PolarErrorDetermination.CurrentMountAxisTotalError.ArcMinutes
                                       },
                                       token);
                return true;
            }

            // The reference sweep is finished: the controller is told, so it can bring its hardware up and
            // report readiness before the hand-over prompt decides whether to warn about it.
            await session.PublishSessionStateAsync(BridgeState.WaitingForRequest, BridgeReason.MeasurementsFinished, token);

            // The operator reads the first polar error before the controller does: the run holds here
            // until Resume, so the reference sweep result is never published to the controller on its own.
            var handOver = await WaitForHandoverResumeAsync(session, progress, token);
            if (handOver == HandOverOutcome.NoController) {
                // Nobody took the correction over, so the awaited external step never started: the run
                // continues exactly like a run without a controller.
                await CloseBridgeSessionAsync(BridgeReason.ExternalLost, requestStop: false);
                return true;
            }

            var autoFinishGate = new AutoFinishGate(2);
            var firstMeasurementBelowTolerance = Math.Abs(TPAPAVM.PolarErrorDetermination.CurrentMountAxisTotalError.ArcMinutes) <= AlignmentTolerance;
            var autoFinishConditionMet = autoFinishGate.Register(firstMeasurementBelowTolerance);
            var measurement = BuildBridgeMeasurement(windowId: null,
                                                     status: BridgeMeasurementStatus.Valid,
                                                     autoFinishConditionMet: autoFinishConditionMet,
                                                     consecutiveBelowTolerance: autoFinishGate.Consecutive);
            measurement.IsFirstMeasurement = true;

            while (!token.IsCancellationRequested) {
                // While the operator holds the run, TPPA neither captures nor answers the controller, but it keeps
                // watching the broker: a controller that disappears while the run is held ends the run through the
                // failure path of the run helper, which cancels the token this loop waits on.
                if (await WaitWhilePausedAsync(progress, token)) {
                    session.CancelForLostController();
                    token.ThrowIfCancellationRequested();
                }

                // The measurement is handed over and the controller answers it exactly once: it either finished the
                // move it planned or it wants the result verified. A cancel, a fault or a controller that goes quiet
                // ends the run inside that wait, through the single failure path of the run helper.
                var ask = await session.PublishMeasurementAndWaitAsync(measurement, token);

                if (ask.Kind == BridgeControllerAskKind.VerificationRequested) {
                    progress?.Report(GetStatus("Verifying final polar alignment"));
                    var verification = await CaptureBridgeMeasurementAsync(session, ask.WindowId, autoFinishGate, progress, token);
                    if (verification.Status == BridgeMeasurementStatus.Valid
                        && autoFinishGate.Consecutive >= autoFinishGate.RequiredConsecutive) {
                        await session.EndAsync(BridgeReason.Completed, true, BuildSessionEndedDetail(verification), token);
                        Logger.Info($"[Bridge] Alignment confirmed within tolerance {AlignmentTolerance}'. Session completed.");
                        // Same order as the hand-over prompt: clear the older toasts, then report the result.
                        Notification.CloseAll();
                        Notification.ShowInformation(
                            "AUTO PA COMPLETED!" + Environment.NewLine +
                            "Total Error is below alignment tolerance." + Environment.NewLine +
                            BuildErrorSummary() + Environment.NewLine +
                            $"{ControllerDisplayCapitalized} completed the session.",
                            TimeSpan.FromMinutes(1));
                        return false;
                    }

                    Logger.Info($"[Bridge] Completion request not confirmed ({Math.Round(verification.TotalErrorArcMin, 2)}' vs tolerance {AlignmentTolerance}'). Continuing the session.");
                    measurement = verification;
                    continue;
                }

                // The controller finished a move: let its mechanics settle for as long as it asked (backlash
                // release, vibration) and measure again. This is the same wait our own adjustment systems do, so
                // the operator reads "Settling" in the status bar instead of an unexplained pause.
                if (ask.SettleSeconds > 0) {
                    await CoreUtil.Wait(TimeSpan.FromSeconds(ask.SettleSeconds), token, progress, "Settling");
                }

                measurement = await CaptureBridgeMeasurementAsync(session, ask.WindowId, autoFinishGate, progress, token);
            }

            token.ThrowIfCancellationRequested();
            return false;
        }

        /// <summary>
        /// Hand-over point after the reference sweep. The session is already open, but the first measurement
        /// is only published to the controller once the run leaves this method: when TPPA is asked to pause
        /// after each update the operator reads the initial polar error and presses Resume, otherwise the
        /// correction is handed over as soon as the controller reports its hardware as ready.
        /// </summary>
        /// <returns>
        /// <see cref="HandOverOutcome.NoController"/> when the controller left before or during the hand-over,
        /// so the awaited external step never started.
        /// </returns>
        private async Task<HandOverOutcome> WaitForHandoverResumeAsync(BridgeControllerRun session, IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (BridgeHub.Instance?.IsControllerPresent != true) {
                // The controller was switched off (or went away) during the reference sweep: there is nobody to
                // hand the correction over to, so the run continues with its normal correction loop - no pause
                // and no hand-over prompt.
                Logger.Info("[Bridge] The controller is gone before the hand-over. Continuing with the normal correction loop.");
                return HandOverOutcome.NoController;
            }

            // The hold is the operator's choice: "Auto pause" is TPPA's own "pause after each
            // continuous-correction update", so a run that asks for pauses keeps the hand-over prompt and an
            // unattended run hands the correction over by itself.
            var pauseForHandover = Properties.Settings.Default.AutoPause;
            if (pauseForHandover) {
                // Older toasts are closed first so the hand-over prompts are the ones the operator really reads.
                Notification.CloseAll();

                // 1) The run holds here. No toast is raised for the measured error: the values are already on
                //    screen in TPPA, and a run without a controller reports nothing while the error is still
                //    above the tolerance. The RESUME prompt is carried by the toast of step 3.
                Logger.Info("[Bridge] Reference sweep finished. " + BuildErrorSummary().Replace(Environment.NewLine, " | "));
                Pause();
            }

            // 2) The controller was told a moment ago that the sweep is finished. Bringing the link up takes
            //    a while (a wireless attempt, then a scan of every COM port), so it gets a generous grace
            //    period: while it is still trying, the run waits instead of cancelling the alignment. A run
            //    without the operator hold also waits here for the axes to be idle, because it captures as
            //    soon as this method returns.
            var readinessDeadline = DateTime.UtcNow.AddSeconds(30);
            while (!session.HasControllerFault
                   && (!session.ControllerHardwareConnected || (!pauseForHandover && !session.ControllerHardwareReady))
                   && BridgeHub.Instance?.IsControllerPresent == true && DateTime.UtcNow < readinessDeadline) {
                await Task.Delay(250, token);
            }

            if (BridgeHub.Instance?.IsControllerPresent != true) {
                // The controller was switched off (or went away) after the hand-over point was reached: the
                // normal correction loop continues, and the operator is told why the prompt is gone.
                Logger.Info("[Bridge] The controller is gone. Handing the run back to the normal correction loop.");
                progress?.Report(GetStatus($"{ControllerDisplayCapitalized} is gone - continuing with the normal correction loop"));
                Notification.ShowWarning($"{ControllerDisplayCapitalized} stopped answering. Three point polar alignment continues with its normal correction loop.");
                return HandOverOutcome.NoController;
            }

            // 3) A controller fault is final: it tried and reported that it cannot drive the run. Being slow
            //    to connect is not a fault - that case is handled by the warning below and the RESUME re-check.
            if (session.HasControllerFault) {
                var failureDetail = session.ControllerFaultDetail;
                Logger.Warning($"[Bridge] {ControllerDisplayCapitalized} could not connect to the alignment hardware: {session.ControllerFaultReason}.");

                await session.EndAsync(BridgeReason.ControllerFault,
                                       false,
                                       new BridgeSessionEndedPayload { Detail = failureDetail },
                                       CancellationToken.None);

                // The hand-over prompt already closed the older toasts: this error appears next to the
                // success/warning of the prompt instead of clearing the screen a second time.
                Notification.ShowError(
                    $"{ControllerDisplayCapitalized} system is assigned through the broker but could not connect to the alignment hardware." + Environment.NewLine +
                    (string.IsNullOrWhiteSpace(failureDetail) ? string.Empty : failureDetail + Environment.NewLine) +
                    "Polar alignment is cancelled.");
                throw new OperationCanceledException("The controller could not connect to the alignment hardware.");
            }

            if (session.ControllerHardwareConnected) {
                // The firmware link is up: that is the success the operator has to see. Readiness is a
                // second, separate step - a connected but busy controller gets a warning, not an error.
                Notification.ShowSuccess(
                    $"{ControllerDisplayCapitalized} connected to the alignment hardware." + Environment.NewLine +
                    (pauseForHandover
                        ? "PAUSING..." + Environment.NewLine +
                          "Press RESUME [ ▶︎ ] to hand the correction over to the controller."
                        : "Handing the correction over to the controller."));
            }

            if (!session.ControllerHardwareConnected || !session.ControllerHardwareReady) {
                var readinessNote = session.ControllerReadyNote;
                Logger.Warning($"[Bridge] {ControllerDisplayCapitalized} is not ready for the hand-over" +
                               (string.IsNullOrWhiteSpace(readinessNote) ? "." : $": {readinessNote}."));
                Notification.ShowWarning(
                    $"{ControllerDisplayCapitalized} is not ready for the correction" +
                    (string.IsNullOrWhiteSpace(readinessNote) ? "." : $": {readinessNote}.") + Environment.NewLine +
                    (pauseForHandover
                        ? "Check the controller before pressing RESUME."
                        : "Check the controller."));
            }

            if (await WaitWhilePausedAsync(progress, token)) {
                // The controller disappeared while the run was paused, still before the hand-over: the normal
                // correction loop takes over instead of failing a run that was never really handed over.
                Logger.Warning("[Bridge] The controller is gone. Handing the run back to the normal correction loop.");
                progress?.Report(GetStatus($"{ControllerDisplayCapitalized} is gone - continuing with the normal correction loop"));
                Notification.ShowWarning($"{ControllerDisplayCapitalized} stopped answering. Three point polar alignment continues with its normal correction loop.");
                return HandOverOutcome.NoController;
            }

            // The operator may press RESUME while the controller is still busy. The correction needs an idle
            // controller, so a busy one ends the run instead of letting TPPA capture while the axes move.
            if (!session.ControllerHardwareReady) {
                var busyNote = session.ControllerReadyNote;
                Logger.Warning($"[Bridge] RESUME pressed while {ControllerDisplayCapitalized} was not ready" +
                               (string.IsNullOrWhiteSpace(busyNote) ? "." : $": {busyNote}."));

                await session.EndAsync(BridgeReason.NoControllerReady,
                                       false,
                                       new BridgeSessionEndedPayload { Detail = busyNote },
                                       CancellationToken.None);

                Notification.ShowError(
                    $"{ControllerDisplayCapitalized} is not ready. Session cancelled." + Environment.NewLine +
                    (string.IsNullOrWhiteSpace(busyNote) ? string.Empty : busyNote + Environment.NewLine) + Environment.NewLine +
                    "Check the controller, then start the polar alignment again.");
                throw new OperationCanceledException("The controller was not ready for the correction hand-over.");
            }

            return HandOverOutcome.HandedOver;
        }

        /// <summary>
        /// Holds the loop while the operator paused the run - during the hand-over prompt and between two captures -
        /// and keeps watching the broker: it returns true when the controller that owns the run disappeared, which
        /// the hand-over answers by continuing with TPPA's own correction loop. A cancel or a fault from the
        /// controller is handled by the run helper, which cancels the run token this wait holds on.
        /// </summary>
        private async Task<bool> WaitWhilePausedAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (!IsPausing) { return false; }

            IsPaused = true;
            progress?.Report(GetStatus("Paused"));
            try {
                while (!token.IsCancellationRequested && IsPausing) {
                    if (BridgeHub.Instance?.IsControllerPresent != true) {
                        Logger.Warning("[Bridge] The controller is gone while the run was paused.");
                        return true;
                    }

                    await Task.Delay(150, token);
                }
            } finally {
                IsPaused = false;
                progress?.Report(GetStatus(string.Empty));
            }

            return false;
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

        private async Task<BridgeMeasurementPayload> CaptureBridgeMeasurementAsync(BridgeControllerRun session,
                                                                                       string windowId,
                                                                                       AutoFinishGate autoFinishGate,
                                                                                       IProgress<ApplicationStatus> progress,
                                                                                       CancellationToken token) {
            var solve = await Solve(TPAPAVM, 0, progress, token);
            if (!solve.Success) {
                return BuildBridgeMeasurement(windowId, BridgeMeasurementStatus.CaptureFailed, false, autoFinishGate.Consecutive);
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
                                                                      totalError: totalError.Degree,
                                                                      alignmentTolerance: AlignmentTolerance));

            if (!estimateStable) {
                Logger.Warning("[Bridge] Publishing an unstable measurement so the controller is not left waiting.");
                return BuildBridgeMeasurement(windowId, BridgeMeasurementStatus.Unstable, false, autoFinishGate.Consecutive);
            }

            var belowTolerance = Math.Abs(totalError.ArcMinutes) <= AlignmentTolerance;
            var autoFinishConditionMet = autoFinishGate.Register(belowTolerance);
            if (autoFinishConditionMet) {
                Logger.Info($"Total Error is below alignment tolerance ({AlignmentTolerance}') for {autoFinishGate.Consecutive} consecutive solves. The external controller decides when to finish.");
            }

            return BuildBridgeMeasurement(windowId, BridgeMeasurementStatus.Valid, autoFinishConditionMet, autoFinishGate.Consecutive);
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
