# Frequently Asked Questions

## Do I need to point at or near the pole?

No. TPPA can work almost anywhere above your horizon. Some field choices are more forgiving than others, especially during the correction phase.

## Will this work in the southern hemisphere?

Yes.

## Does it account for refraction?

Yes. There is an option on the plugin page to include refraction-aware calculations, and that option is still marked as under test.  
Even with refraction enabled, a perfectly static solution is difficult because atmospheric conditions change over time, but TPPA can still provide a good practical alignment with or without refraction correction.

## How does the procedure work?

The procedure consists of the following steps:

* Step 1
    + Slew to the specified alt/az start coordinates, or start from the current position
    + Start telescope tracking
* Step 2
    + Capture an image at the current position
    + Plate-solve the image
* Step 3
    + Move the telescope at the configured [Move Rate] in automatic mode, or move it manually east or west along the right ascension axis based on the [East Direction] setting, until it has moved by at least [Target Distance]°
    + Capture an image at the new position
    + Plate-solve the image
* Step 4
    + Repeat the same RA-axis movement again until the next point has moved by at least [Target Distance]°
    + Capture an image at the new position
    + Plate-solve the image
* Step 5
    + Reconstruct the telescope axis from the three measured points and compare it with the expected polar axis for the configured location
* Step 6
    + Continue capturing and plate-solving while the mount tracks
    + Update the reported polar error from each new solve
    + Adjust only the mount altitude and azimuth during this phase until the alignment is good enough
    + If you left-click a star, the visual indicators will follow that reference star during incremental adjustments
* Step 7
    + Close the window when you are done to finish the instruction

## What do I need for the procedure to run?

* Site latitude and longitude should be set correctly in N.I.N.A.
* A connected camera that is ready to capture
* A working plate solver configured for the current optical setup
* An equatorial mount whose right ascension axis can be moved
    + In automatic mode, the mount must be connected and must support RA-axis motion through ASCOM `MoveAxis`
    + In Manual Mode, you provide the RA-axis movement yourself and a mount connection is optional

## What do I need to use the OAPA automated alignment system?

* An OAPA-compatible motorized platform (two stepper motors adjusting the mount's altitude and azimuth)
* A controller board running the reference OAPA firmware, available at [github.com/michelebergo/oapa-firmware](https://github.com/michelebergo/oapa-firmware)
* A USB connection between the controller and the computer running N.I.N.A.
* Select **OAPA** as the alignment system in the plugin options, connect, and run the built-in **Self-Calibration** once: it automatically measures your platform's gear response and backlash on the sky
* After calibration, TPPA drives the platform automatically during the correction phase until the configured tolerance is reached

## The mount and camera are both connected, but the automatic-mode button is greyed out. Why?

* Automatic mode requires the mount to move along the right ascension axis through the ASCOM [`MoveAxis`](https://ascom-standards.org/Help/Platform/html/M_ASCOM_DeviceInterface_ITelescopeV3_MoveAxis.htm) method, which is different from a normal slew.
* If the N/E/S/W buttons in the N.I.N.A. telescope tab are greyed out, the driver is reporting through [`CanMoveAxis`](https://ascom-standards.org/Help/Platform/html/M_ASCOM_DeviceInterface_ITelescopeV3_CanMoveAxis.htm) that it cannot use `MoveAxis`.
* If that is the case, automatic mode cannot be used.
* Ask your mount vendor whether the driver supports `MoveAxis`. For EQMOD users, disabling strict conformance mode may help.
* Until then, use **Manual Mode** instead.

## How does Manual Mode work exactly?

Manual Mode is intended for mounts whose drivers cannot use `MoveAxis`, or for cases where the mount is not connected to N.I.N.A.  
For Manual Mode to work well, follow these steps:

1. If possible, connect the mount so TPPA can use reference coordinates and the regular solver path. If you do not connect the mount, make sure the blind solver is configured.
2. Enable the `Manual Mode` toggle.
3. Slew to the field where you want to start the alignment.
4. Enable tracking.
5. Click `Start`.
6. TPPA captures the first measurement point.
7. After the first point, TPPA asks you to move the mount along the right ascension axis. The total amount of movement depends on the configured `Target Distance`.
8. While you are moving, TPPA continues solving and checking how far the mount has already moved.
9. Once the second point is far enough away, TPPA transitions to the third point automatically. This stage works the same way as steps 7 and 8.
10. After the third-point movement is large enough, TPPA waits 10 seconds for settling. Do not move the mount during that wait. After that, it determines the final point.
11. Once all points are measured, TPPA displays the polar error and the correction phase begins.

## Is there a preferred direction or sky position to start from?

TPPA can work almost anywhere above your horizon, but some starting fields are more tolerant than others. In practice:

* Prefer a field toward the pole for your hemisphere.
* If practical, choose a field around 15° altitude or higher.
* Avoid exact east or west during the correction phase, because those positions are weak for the correction math.
* Avoid exact zenith during the correction phase.
* Avoid runs that will cross the meridian during the three-point slews or the later adjustment phase.

## What do the settings mean?

**Default Move Rate**  
The default RA-axis rate TPPA requests when it moves the mount automatically between the first three measurement points.

**Default East Direction**  
Controls whether the automatic RA sweep is sent in the eastward direction.

**Default Target Distance**  
The angular separation between the measurement points during the initial RA-only sweep.

**Default Search Radius**  
The initial plate-solve search radius, in degrees. It should be large enough to cover your starting pointing and alignment error, but not so large that solves become unnecessarily slow.

**Axis move timeout factor**  
Multiplier applied to the automatic RA-move timeout. TPPA computes the timeout from move distance and move rate, then multiplies it by this factor.

**Default azimuth offset from pole**  
The default azimuth offset used when TPPA creates a start position instead of starting from the current position.

**Default altitude offset from pole**  
The default altitude offset used when TPPA creates a start position instead of starting from the current position.

**Default Alignment Tolerance**  
If this value is non-zero, TPPA can automatically finish once the reported total error falls below the configured threshold in arcminutes.

**Error Colors**  
These settings control the colors used for the altitude, azimuth, total-error, target-circle, and success overlays.

**Log polar alignment error adjustments**  
When enabled, TPPA writes the current altitude, azimuth, and total error values to a log file in `\Documents\N.I.N.A\PolarAlignment`.

**Adjust for refraction?**  
When enabled, TPPA uses refraction-aware coordinates based on location, elevation, weather data, and wavelength.  
If no weather source is connected, TPPA falls back to a standard set of atmospheric values.

**Use continuous error estimator?**
When enabled, TPPA uses the experimental time-aware estimator during the live correction loop. When disabled, TPPA keeps using the legacy image-plane calculation.

**Stop Tracking when done?**  
Disable this if you want the mount to continue tracking after TPPA finishes.

**Auto pause between continuous exposures?**  
When enabled, TPPA pauses itself after each continuous correction update.

**Polar Alignment System**  
Selects which supported adjustment system TPPA exposes during the correction phase: `None`, `UPAS`, or `OAPA`.

**Reverse Azimuth Axis?**  
Reverses azimuth movement commands sent to the selected adjustment system.

**Reverse Altitude Axis?**  
Reverses altitude movement commands sent to the selected adjustment system.

**Azimuth backlash compensation**  
If non-zero, TPPA adds backlash compensation when the azimuth movement direction changes.

**Do automated adjustments?**  
When enabled, TPPA tries to send automated correction nudges through the selected adjustment system during the correction phase. This option is still experimental.

**Automated adjustment settle time**  
The number of seconds TPPA waits after each automated adjustment before continuing.

## The solver keeps failing, even though solving works in other places. How can I fix this?

TPPA uses its own solve `Search Radius` setting so the workflow can solve quickly.  
If that radius is smaller than your combined pointing and polar-alignment error, solving can fail even if solving works elsewhere in N.I.N.A.  
Increase the TPPA search radius first, then verify exposure, focal length, pixel size, and binning.

## Do I need the guider or the main imaging camera for this to work?

You only need a camera that can be connected to N.I.N.A. and correct optical parameters for focal length and pixel size.  
You also need a working plate solver for that setup.

## Do I need a goto mount?

No. TPPA supports `Manual Mode`. In that mode, TPPA does not control the mount for the RA sweep.  
Instead, you move the mount yourself along the RA axis and TPPA tells you when the second and third points are far enough away.  
If possible, keep tracking enabled for the whole procedure.

## How do I start the polar alignment?

There are two ways to start it:

**Inside the Advanced Sequencer**  
Drag the `Three Point Polar Alignment` instruction into your sequence where you want it to run. When the instruction executes, a guided window appears.

**From the Imaging tab**  
Open the TPPA tool from the available dockables in the Imaging tab. The tool pane exposes the same workflow and start controls directly.

## My error keeps changing when I am not adjusting anything. Why?

The routine expects the mount to keep tracking, and any change in the field is reflected in the continuous correction estimate.  
If tracking is imperfect, or if the mount has just gone through periodic error, some motion in the reported error is normal.  
A few arcseconds of movement is usually not a problem. If it takes a long time to dial in the final adjustment, restarting TPPA for a fresh fine pass can help.

## What is the size of the target circle?

The circle is rendered from your image scale. TPPA draws circles at 30 arcseconds, 1 arcminute, and 5 arcminutes.

## Are there any areas in the sky I should avoid?

* Exact east (`90°`) or west (`270°`) during the correction phase
* Exact zenith during the correction phase
* Very low-altitude fields
* Runs that will cross the meridian during the three-point slews or the later adjustment phase

Lower-altitude fields are generally less forgiving, so if practical, choose a field around 15° altitude or higher.  
For the southern hemisphere, the preferred pole-side region is due south.

![TPPA_Zones](./TPPA_Zones.png)
