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
        private BridgeSession bridgeSession;
        private string bridgeSessionEndReason = BridgeReason.UserStop;

        /// <summary>True when the controller vanished during a session and the normal loop has to take over.</summary>
        private bool controllerLost;

        /// <summary>
        /// True once <see cref="RunBridgeAsync"/> serves the controller request queue. While it is false the
        /// run is still measuring the three reference points, so a controller stop ends it immediately
        /// instead of waiting for the queue to be drained.
        /// </summary>
        private bool bridgeLoopStarted;

        /// <summary>
        /// True when the three reference points already met the tolerance, so nothing is handed over and the
        /// run finishes under TPPA's own auto-finish - the same result the operator gets without a controller.
        /// The caller reads it to continue with the normal loop instead of ending the run.
        /// </summary>
        private bool bridgeNotNeeded;

        /// <summary>
        /// Who is on the other end of the broker, e.g. "MLAstroRPA 2.2.0.0". Every status text and
        /// notification that talks about the controller uses this so the operator sees which plugin it is.
        /// </summary>
        private static string ControllerDisplay => BridgeHub.Instance?.ControllerDisplay ?? "the external alignment controller";

        /// <summary><see cref="ControllerDisplay"/> written so it can start a sentence.</summary>
        private static string ControllerDisplayCapitalized =>
            BridgeHub.Instance?.ControllerDisplayCapitalized ?? "The external alignment controller";

        /// <summary>
        /// Creates and opens the external correction session when a controller is connected. The mode is
        /// detected, not configured: a controller announces itself every few seconds while it is
        /// enabled, so the presence of those announcements is the handshake. Returns null whenever TPPA
        /// has to run its normal correction loop.
        /// </summary>
        private async Task<BridgeSession> StartBridgeSessionAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var hub = BridgeHub.Instance;
            if (hub == null) { return null; }

            if (!hub.IsControllerPresent) {
                Logger.Info("[Bridge] No external alignment controller is connected. Using the internal correction loop.");
                return null;
            }

            if (AlignmentTolerance <= 0) {
                Logger.Warning("[Bridge] A controller is connected but the alignment tolerance is zero, so no external session is started.");
                Notification.ShowWarning($"{hub.ControllerDisplayCapitalized} is connected, but the alignment tolerance is zero. The external correction mode needs a tolerance above zero, so the normal correction loop runs instead.");
                return null;
            }

            controllerLost = false;
            bridgeNotNeeded = false;
            var session = new BridgeSession(messageBroker);
            hub.AttachSession(session);
            try {
                progress?.Report(GetStatus($"Waiting for {ControllerDisplay}"));
                await session.OpenAsync(AlignmentTolerance, TPAPAVM.UseContinuousErrorEstimator, token);

                var ready = await session.WaitForControllerReadyAsync(token);
                if (!ready) {
                    if (session.HasControllerFault) {
                        // A fault means the controller cannot drive the run at all (no hardware link on either
                        // transport, or it gave up): the run is stopped here with the reason it reported.
                        Logger.Warning($"[Bridge] {ControllerDisplayCapitalized} cannot take the run over: {session.ControllerFaultReason}");
                        Notification.ShowError(
                            $"{ControllerDisplayCapitalized} cannot drive the polar alignment." + Environment.NewLine +
                            (string.IsNullOrWhiteSpace(session.ControllerFaultDetail) ? string.Empty : session.ControllerFaultDetail + Environment.NewLine) +
                            "Polar alignment is cancelled.");
                        throw new OperationCanceledException("The controller cannot drive the polar alignment run.");
                    }

                    // The controller is present but has not confirmed readiness yet. No toast and no fall
                    // back: the run continues silently, and readiness is checked again at the hand-over
                    // prompt, where a controller that is still not ready pauses the run and warns.
                    Logger.Warning("[Bridge] The controller has not confirmed readiness yet. The run continues; readiness is checked again at the hand-over.");
                }

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
        /// External correction loop. Every capture waits for the external controller and TPPA never
        /// finishes because of the measured error: it only publishes the tolerance flags and ends the
        /// session when the controller asks for completion, cancels or stops answering.
        /// </summary>
        private async Task RunBridgeAsync(BridgeSession session,
                                                      IProgress<ApplicationStatus> progress,
                                                      CancellationToken token) {
            // From here on the queue is served, so a cancel or fault from the controller is handled by the
            // loop below (session end plus the reason in a toast) instead of aborting the measurement phase.
            bridgeLoopStarted = true;

            // The three reference points are already inside the tolerance: this run finishes under TPPA's own
            // auto-finish, exactly like a run without a controller. Nothing is handed over and the controller
            // is never asked to bring its hardware up.
            var sweepTotalError = Math.Abs(TPAPAVM.PolarErrorDetermination.CurrentMountAxisTotalError.ArcMinutes);
            if (sweepTotalError <= AlignmentTolerance) {
                Logger.Info($"[Bridge] The reference sweep is already within tolerance ({Math.Round(sweepTotalError, 2)}' <= {AlignmentTolerance}'). Finishing without handing the correction over.");
                bridgeNotNeeded = true;

                await session.EndAsync(BridgeReason.Completed,
                                       true,
                                       new BridgeSessionEndedPayload {
                                           AzimuthErrorArcMin = TPAPAVM.PolarErrorDetermination.CurrentMountAxisAzimuthError.ArcMinutes,
                                           AltitudeErrorArcMin = TPAPAVM.PolarErrorDetermination.CurrentMountAxisAltitudeError.ArcMinutes,
                                           TotalErrorArcMin = TPAPAVM.PolarErrorDetermination.CurrentMountAxisTotalError.ArcMinutes
                                       },
                                       token);
                return;
            }

            // The reference sweep is finished: the controller is told, so it can bring its hardware up and
            // report readiness before the hand-over prompt decides whether to warn about it.
            await session.PublishSessionStateAsync(BridgeState.WaitingForRequest, BridgeReason.MeasurementsFinished, token);

            // The operator reads the first polar error before the controller does: the run holds here
            // until Resume, so the reference sweep result is never published to the controller on its own.
            await WaitForHandoverResumeAsync(session, progress, token);

            var autoFinishGate = new AutoFinishGate(2);
            var firstMeasurementBelowTolerance = Math.Abs(TPAPAVM.PolarErrorDetermination.CurrentMountAxisTotalError.ArcMinutes) <= AlignmentTolerance;
            var autoFinishConditionMet = autoFinishGate.Register(firstMeasurementBelowTolerance);

            await PublishBridgeMeasurementAsync(session,
                                                  isFirstMeasurement: true,
                                                  windowId: null,
                                                  status: BridgeMeasurementStatus.Valid,
                                                  autoFinishConditionMet: autoFinishConditionMet,
                                                  consecutiveBelowTolerance: autoFinishGate.Consecutive,
                                                  token: token);

            while (!token.IsCancellationRequested) {
                // While the operator holds the run, TPPA neither captures nor answers requests, but it keeps
                // watching: a cancel or a controller that disappears still ends the session.
                await WaitWhilePausedAsync(session, progress, token);

                var request = await session.WaitForControllerRequestAsync(token);
                switch (request.Kind) {
                    case BridgeRequestKind.ControllerReady:
                    case BridgeRequestKind.StopAcknowledged:
                        continue;

                    case BridgeRequestKind.AdjustWindow: {
                        var grantedWindowId = await session.GrantWindowAsync(request, token);
                        Logger.Info($"External controller holds capture window {grantedWindowId} for measurement {request.MeasurementId}.");
                        progress?.Report(GetStatus($"{ControllerDisplayCapitalized} is adjusting"));
                        continue;
                    }

                    case BridgeRequestKind.RequestMeasurement: {
                        if (request.Duplicate) {
                            Logger.Info("[Bridge] Ignored duplicated measurement request.");
                            continue;
                        }
                        var windowId = request.WindowId ?? session.CurrentWindowId;
                        session.CloseWindow(request.MeasurementRequest?.Reason ?? BridgeReason.StepFinished);
                        var measurement = await CaptureBridgeMeasurementAsync(session, windowId, autoFinishGate, progress, token);
                        await session.PublishMeasurementAsync(measurement, token);
                        continue;
                    }

                    case BridgeRequestKind.RequestCompletion: {
                        session.EnterVerifying();
                        session.CloseWindow(BridgeReason.CompletionRequested);
                        progress?.Report(GetStatus("Verifying final polar alignment"));
                        var measurement = await CaptureBridgeMeasurementAsync(session, request.WindowId, autoFinishGate, progress, token);
                        if (measurement.Status == BridgeMeasurementStatus.Valid
                            && autoFinishGate.Consecutive >= autoFinishGate.RequiredConsecutive) {
                            await session.EndAsync(BridgeReason.Completed, true, BuildSessionEndedDetail(measurement), token);
                            Logger.Info($"[Bridge] Alignment confirmed within tolerance {AlignmentTolerance}'. Session completed.");
                            // Same order as the hand-over prompt: clear the older toasts, then report the result.
                            Notification.CloseAll();
                            Notification.ShowInformation(
                                "PA COMPLETED!" + Environment.NewLine +
                                "Total Error is below alignment tolerance." + Environment.NewLine +
                                BuildErrorSummary() + Environment.NewLine +
                                $"{ControllerDisplayCapitalized} completed the session.",
                                TimeSpan.FromMinutes(1));
                            return;
                        }
                        Logger.Info($"[Bridge] Completion request not confirmed ({Math.Round(measurement.TotalErrorArcMin, 2)}' vs tolerance {AlignmentTolerance}'). Continuing the session.");
                        await session.PublishMeasurementAsync(measurement, token);
                        continue;
                    }

                    case BridgeRequestKind.Cancel: {
                        var reason = request.Cancel?.Reason ?? BridgeReason.ControllerCancel;
                        var note = request.Cancel?.Note;
                        var faulted = request.Fault != null;

                        Logger.Warning($"[Bridge] {ControllerDisplayCapitalized} {(faulted ? "stopped" : "cancelled")} the session: {reason}" +
                                       (string.IsNullOrWhiteSpace(note) ? "." : $" ({note})."));

                        await session.EndAsync(reason, false, new BridgeSessionEndedPayload { Detail = note }, token);

                        // The reason is what the operator needs to act on, so both the sentence and the raw
                        // reason go into one toast instead of leaving it in the log only. A reason that means
                        // the hardware could not be driven is an error, not a plain warning.
                        var hardwareFailure = IsHardwareFailureReason(reason);
                        var endMessage = (faulted ? $"{ControllerDisplayCapitalized} stopped the session on a fault." : $"{ControllerDisplayCapitalized} cancelled the session.") + Environment.NewLine +
                                         DescribeControllerCancel(reason) + Environment.NewLine +
                                         (string.IsNullOrWhiteSpace(note) ? string.Empty : note + Environment.NewLine) +
                                         $"Reason: {reason}";

                        Notification.CloseAll();
                        if (faulted || hardwareFailure) {
                            Notification.ShowError(endMessage);
                        } else {
                            Notification.ShowWarning(endMessage, TimeSpan.FromMinutes(1));
                        }
                        return;
                    }

                    case BridgeRequestKind.ExternalLost:
                        Logger.Warning("[Bridge] External controller stopped answering. Handing the run back to the normal correction loop.");
                        controllerLost = true;
                        return;

                    case BridgeRequestKind.SessionTimeout:
                        Logger.Warning("[Bridge] Session safety time limit reached. Asking the controller to stop.");
                        await StopBridgeSessionAsync(session, BridgeReason.SessionTimeout, token);
                        return;

                    case BridgeRequestKind.WindowExpired:
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
        private async Task WaitForHandoverResumeAsync(BridgeSession session, IProgress<ApplicationStatus> progress, CancellationToken token) {
            // Older toasts are closed first so the hand-over prompts are the ones the operator really reads.
            Notification.CloseAll();

            // 1) The measured error, and the run holds here: the correction is not handed over before the
            //    operator has seen the initial polar error.
            Notification.ShowInformation(
                "Polar alignment error" + Environment.NewLine +
                BuildErrorSummary() + Environment.NewLine);
            Pause();

            // 2) The controller was told a moment ago that the sweep is finished. Bringing the link up takes
            //    a while (a wireless attempt, then a scan of every COM port), so it gets a generous grace
            //    period: while it is still trying, the run waits instead of cancelling the alignment.
            var readinessDeadline = DateTime.UtcNow.AddSeconds(30);
            while (!session.ControllerHardwareConnected && !session.HasControllerFault && DateTime.UtcNow < readinessDeadline) {
                await Task.Delay(250, token);
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

                Notification.CloseAll();
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
                    "Press RESUME [ ▶︎ ] to hand the correction over to the controller.");
            }

            if (!session.ControllerHardwareConnected || !session.ControllerHardwareReady) {
                var readinessNote = session.ControllerReadyNote;
                Logger.Warning($"[Bridge] {ControllerDisplayCapitalized} is not ready for the hand-over" +
                               (string.IsNullOrWhiteSpace(readinessNote) ? "." : $": {readinessNote}."));
                Notification.ShowWarning(
                    $"{ControllerDisplayCapitalized} is not ready for the correction" +
                    (string.IsNullOrWhiteSpace(readinessNote) ? "." : $": {readinessNote}.") + Environment.NewLine +
                    "Check the controller before pressing RESUME.");
            }

            await WaitWhilePausedAsync(session, progress, token);

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

                Notification.CloseAll();
                Notification.ShowError(
                    $"{ControllerDisplayCapitalized} is not ready. Session cancelled." + Environment.NewLine +
                    (string.IsNullOrWhiteSpace(busyNote) ? string.Empty : busyNote + Environment.NewLine) + Environment.NewLine +
                    "Check the controller, then start the polar alignment again.");
                throw new OperationCanceledException("The controller was not ready for the correction hand-over.");
            }
        }

        /// <summary>
        /// Holds the loop while the operator paused the run - during the hand-over prompt and between two
        /// captures - but keeps watching the controller: a cancel, a fault or a controller that disappears
        /// still ends the session instead of waiting for a resume that may never come. Ordinary requests
        /// stay in the session queue for the resumed loop.
        /// </summary>
        private async Task WaitWhilePausedAsync(BridgeSession session, IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (!IsPausing) { return; }

            IsPaused = true;
            progress?.Report(GetStatus("Paused"));
            try {
                while (!token.IsCancellationRequested && IsPausing) {
                    if (session.HasPendingTerminalRequest) {
                        Logger.Info("[Bridge] The controller ended the session while the run was paused.");
                        return;
                    }

                    if (BridgeHub.Instance?.IsControllerPresent != true) {
                        Logger.Warning("[Bridge] The controller is gone while the run was paused.");
                        controllerLost = true;
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
        /// True when a controller end reason means the correction could not run on the hardware, so the
        /// operator gets an error instead of a plain warning.
        /// </summary>
        private static bool IsHardwareFailureReason(string reason) {
            return string.Equals(reason, BridgeReason.FirmwareDisconnected, StringComparison.Ordinal)
                   || string.Equals(reason, BridgeReason.ControllerFault, StringComparison.Ordinal)
                   || string.Equals(reason, BridgeReason.CaptureFailed, StringComparison.Ordinal);
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

        private async Task<BridgeMeasurementPayload> CaptureBridgeMeasurementAsync(BridgeSession session,
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
                                                                      totalError: totalError.Degree));

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

        private async Task PublishBridgeMeasurementAsync(BridgeSession session,
                                                          bool isFirstMeasurement,
                                                          string windowId,
                                                          string status,
                                                          bool autoFinishConditionMet,
                                                          int consecutiveBelowTolerance,
                                                          CancellationToken token) {
            var measurement = BuildBridgeMeasurement(windowId, status, autoFinishConditionMet, consecutiveBelowTolerance);
            measurement.IsFirstMeasurement = isFirstMeasurement;
            await session.PublishMeasurementAsync(measurement, token);
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
        /// Watches a session for a controller stop that arrives while TPPA is still measuring the three
        /// reference points. That phase does not serve the request queue, so without this the operator would
        /// wait for captures the controller has already abandoned.
        /// </summary>
        private void WatchControllerStopBeforeHandover(BridgeSession session, CancellationTokenSource runCts) {
            if (session == null) { return; }

            // The instruction can be executed again, so the flag of the previous run never survives.
            bridgeLoopStarted = false;
            session.ControllerStopRequested += (_, e) => OnControllerStopBeforeHandover(session, e, runCts);
        }

        private void OnControllerStopBeforeHandover(BridgeSession session, BridgeControllerStopEventArgs e, CancellationTokenSource runCts) {
            if (bridgeLoopStarted || session == null || !ReferenceEquals(session, bridgeSession)) { return; }

            var reason = string.IsNullOrWhiteSpace(e?.Reason) ? BridgeReason.ControllerCancel : e.Reason;
            var note = e?.Note;
            var faulted = e?.IsFault == true;
            bridgeSessionEndReason = reason;

            Logger.Warning($"[Bridge] {ControllerDisplayCapitalized} {(faulted ? "stopped the session on a fault" : "cancelled the session")} while the reference points were still being measured: {reason}" +
                           (string.IsNullOrWhiteSpace(note) ? "." : $" ({note})."));

            var hardwareFailure = IsHardwareFailureReason(reason);
            var stopMessage = (faulted ? $"{ControllerDisplayCapitalized} stopped the run on a fault." : $"{ControllerDisplayCapitalized} stopped the run.") + Environment.NewLine +
                              DescribeControllerCancel(reason) + Environment.NewLine +
                              (string.IsNullOrWhiteSpace(note) ? string.Empty : note + Environment.NewLine) +
                              $"Reason: {reason}";

            Notification.CloseAll();
            if (faulted || hardwareFailure) {
                Notification.ShowError(stopMessage);
            } else {
                Notification.ShowWarning(stopMessage, TimeSpan.FromMinutes(1));
            }

            // The bridge loop never runs, so the session is ended here: the controller is told the session is
            // over even though no measurement was published for it.
            _ = EndSessionAfterEarlyStopAsync(session, reason, note);
            try { runCts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        private async Task EndSessionAfterEarlyStopAsync(BridgeSession session, string reason, string note) {
            try {
                await session.EndAsync(reason, false, new BridgeSessionEndedPayload { Detail = note }, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Error($"[Bridge] Failed to end the session after an early controller stop: {ex.Message}");
            }
        }

        private async Task StopBridgeSessionAsync(BridgeSession session, string reason, CancellationToken token) {
            var hardwareStopStatus = await session.RequestStopAsync(reason, token);
            await session.EndAsync(reason, false, new BridgeSessionEndedPayload { HardwareStopStatus = hardwareStopStatus }, token);
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
