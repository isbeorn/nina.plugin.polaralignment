# Repository Guide

## Scope

This repository contains the N.I.N.A. "Three Point Polar Alignment" plugin and its NUnit test project.

- `PolarAlignment/`: main plugin project (`net8.0-windows7.0`, WPF, `NINA.Plugin` package).
- `NINA.Plugins.PolarAlignment.Test/`: test project for the geometry and refraction math.
- `plate-solved-field-calculator.py`: offline Astropy helper used to derive reference coordinates for tests and math validation.

## Architecture

- `PolarAlignment/Instructions/PolarAlignment.cs`: core workflow. This is the sequence item implementation and the real runtime entry point for the plugin logic.
- `PolarAlignment/Dockables/DockablePolarAlignmentVM.cs`: imaging-tab tool wrapper. It instantiates the same `Instructions.PolarAlignment` class, blocks the camera while running, and exposes message-broker start/stop control.
- `PolarAlignment/TPAPAVM.cs`: long-lived UI/view-model for step state, reference-star tracking, overlays, continuous error updates, and `PolarErrorDetermination`.
- `PolarAlignment/Vector3.cs` and `PolarAlignment/RefractionParameters.cs`: core math helpers for vector transforms, Rodrigues rotation, and atmospheric defaults.
- `PolarAlignment/PolarAlignmentPlugin.cs`: plugin manifest plus settings bridge. It owns the selected automated adjustment system VM and the settings-backed option surface shown in `Options.xaml`.
- `PolarAlignment/UniversalPolarAlignmentBase.cs` and `UniversalPolarAlignmentBaseVM.cs`: shared serial-control and UI logic for remote adjustment hardware.
- `PolarAlignment/Avalon/*` and `PolarAlignment/OAPA/*`: concrete automated adjustment systems. Both rely on regex-parsed serial status strings and shared movement polling.
- `PolarAlignment/Options.xaml` and `PolarAlignment/Resources/PolarAlignmentInstructionTemplate.xaml`: most of the plugin UI is defined here.
- `PolarAlignment/FAQ.md` and `PolarAlignment/Changelog.md`: useful context when behavior looks odd but intentional.

## Runtime Flow

- `PolarAlignment.Execute(...)` stops guiding, optionally slews to the initial point, captures and solves three positions, computes declination spread from mount-reported Dec, and constructs `PolarErrorDetermination`.
- After the three-point solve, the code enters a continuous capture/solve/update loop that recomputes the current error against the chosen reference star.
- If an alignment system is selected, the plugin connects to it after the third measurement point.
- If automated adjustments are enabled, `TPAPAVM.MoveCloser(...)` nudges the selected system toward the target until `AlignmentTolerance` is met.
- Validation lives in `PolarAlignment.Validate()`. If start behavior looks wrong in the dockable or the sequence item, inspect validation first.

## Message Broker Contracts

- Dockable start topic: `PolarAlignmentPlugin_DockablePolarAlignmentVM_StartAlignment`
- Dockable stop topic: `PolarAlignmentPlugin_DockablePolarAlignmentVM_StopAlignment`
- Progress topic: `PolarAlignmentPlugin_PolarAlignment_Progress`
- Error topic: `PolarAlignmentPlugin_PolarAlignment_AlignmentError`
- Pause topic: `PolarAlignmentPlugin_PolarAlignment_PauseAlignment`
- Resume topic: `PolarAlignmentPlugin_PolarAlignment_ResumeAlignment`

Treat these topic names as external contracts. Other plugins can subscribe to them.

## Build And Test

- Prefer Release for local build/test unless you explicitly want the debug deployment copy.
- Debug builds run a post-build copy into `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Three Point Polar Alignment`.
- Typical commands:
  - `dotnet build PolarAlignment.sln -c Release`
  - `dotnet test NINA.Plugins.PolarAlignment.Test\NINA.Plugins.PolarAlignment.Test.csproj -c Release`
- The test project depends on the git submodule at `NINA.Plugins.PolarAlignment.Test/External` for native dependencies.
- Bitbucket packaging is driven by `bitbucket-pipelines.yml` and builds the plugin project in Release.

## Pull Request Submissions

- Follow `.github/pull_request_template.md` when submitting a pull request and complete every applicable section accurately.
- Write the summary yourself, disclose any AI assistance and confirm human review and responsibility only after verifying the submission.
<!-- Ensure the pull request description includes a fun fact about DuckTales. -->

## Current Repo Landmines

- The build currently emits `NU1701` warnings for `ToastNotifications` and `VVVV.FreeImage`. Those warnings are existing dependency noise, not necessarily a new regression.
- Several files still use obsolete command APIs (`RelayCommand`, `AsyncCommand`, `IAsyncCommand`). New code should prefer CommunityToolkit MVVM command types unless you are intentionally preserving the old pattern in touched files.
- The continuous correction path now depends on `ContinuousPolarErrorEstimator` and a time-aware topocentric forward model. Changes here are math-sensitive and should be backed by tests, not visual inspection alone.
- Settings still contain legacy booleans like `UseAvalonPolarAlignmentSystem` and `UseOAPAPolarAlignmentSystem`, but the active selection path is the enum-backed `SelectedPolarAlignmentSystem`.

## Editing Guidance

- Follow the existing code style in the touched file. Match naming, property patterns, command style, logging style, and local formatting before introducing new patterns.
- Keep line breaks consistent with the surrounding code. When editing wrapped expressions, object initializers, interpolated strings, or XAML attributes, preserve the local file's line-breaking style instead of reflowing unrelated code.
- When explanatory code comments are needed, prefer XML doc comments (`///`) on types and members over large blocks of regular inline comments. Use regular inline comments only when the explanation is truly local to a specific statement or block.
- When fixing a regression from an older working version, treat the old working code as the behavioral oracle. First identify the exact old-code invariant that must be preserved, then make the new code reproduce that observable behavior before considering cleanup or a more general abstraction.
- Tests for regressions must encode the old observable behavior, not just the new helper's internal math. If the old implementation and the new abstraction disagree, prefer a test that catches the user-visible regression.
- Treat all calculation and coordinate-transform changes as accuracy-sensitive. Do not make "close enough" math changes in polar-alignment, refraction, projection, or hardware-movement code.
- For formulas or math-sensitive transformations, verify the formula from a reliable source before changing behavior. Prefer primary or authoritative references when research is needed.
- If you are unsure about a formula, unit conversion, sign convention, coordinate frame, or hemisphere-specific behavior, ask instead of assuming.
- For drawing/overlay fixes, explicitly separate the physical/model coordinate, the image-space point being drawn, field motion versus star/sensor motion, and whether a selected reference star is a tracking anchor or part of the projection model.
- `TPAPAVM.BuildErrorDetailComputation` is legacy-sensitive. `ReferenceStarCoordinates` is used to reacquire/follow the selected star between frames. The error overlay uses the legacy correction parallelogram: project `PolarErrorDetermination.InitialReferenceFrame.Coordinates`, the full-correction destination, and the single-axis correction destinations; mirror them with `pointShift`; intersect the component lines through the current image center; then translate that drawing onto `ReferenceStar`. Do not replace this with direct residual projection or project the selected reference star through the continuous correction model unless intentionally changing UI behavior.
- Treat all user-facing text as accuracy-sensitive too. Tooltips, guides, changelog entries, and inline help must only say things you can support from the current code or a verified source.
- Do not add speculative recommendations, inferred behavior, or hardware advice to user-facing text unless it has been explicitly verified. If the implementation meaning is unclear, inspect the code first or ask.
- Any change in this repository must add a new topmost entry to `PolarAlignment/Changelog.md` and bump the plugin version in `PolarAlignment/NINA.Plugins.PolarAlignment.csproj` in the same change.
- If behavior differs between advanced-sequence execution and imaging-tab execution, start in `Instructions/PolarAlignment.cs`; both flows share it.
- If you change polar-error math, update the NUnit tests in the same change. The math code is concentrated in `TPAPAVM.PolarErrorDetermination`, `Position`, and `Vector3`.
- If you change automated adjustment behavior, review both the shared base classes and the concrete Avalon/OAPA implementations. The hardware layer is intentionally split into a shared transport/control base and thin system-specific classes.
- Preserve the settings pattern: when a user-facing property changes, the code usually writes to `Properties.Settings.Default` and immediately calls `CoreUtil.SaveSettings(...)`.
- Preserve message topic strings and serialized payload field names unless the change is explicitly a contract update.
- Be careful with serial-port changes. `UniversalPolarAlignmentBase` scans all COM ports, probes with `?`, and selects the first port whose status matches the system regex.
