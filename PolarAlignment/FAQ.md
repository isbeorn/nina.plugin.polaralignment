# Frequently Asked Questions

## Do I need to point at or near the pole?

No. In fact the plugin should work almost anywhere above your horizon. 

## Will this work in the southern hemisphere?

Yes.

## Does it account for refraction?

There is an option to enable this in the plugin page and it is currently under test. 
Keep in mind however that a *perfect* polar alignment is inherently difficult, due to constant changing atmosphere conditions, but the procedure will give you a good alignment with and without refraction corrections.

## How does the procedure work?

The procedure consists of the following steps  

* Step 1  
    + Slew to the specified alt/az coordinates
    + Start telescope tracking  
* Step 2  
    + Take an image of current position
    + Plate solve current position  
* Step 3  
    + Move the telescope by the [Move Rate] in East or West direction along the Right Ascension axis, based on [East Direction] until at least moved by [Target Distance]°
    + Take an image of current position
    + Plate solve current position  
* Step 4  
    + Move the telescope by the [Move Rate] in East or West direction along the Right Ascension axis, based on [East Direction] until at least moved by [Target Distance]°
    + Take an image of current position
    + Plate solve current position  
* Step 5  
    + Calculate the telescope axis out of the three points and compare it with the expected axis based on the user location  
* Step 6  
    + Continuously loop exposures, while tracking and plate solve them. Adjust the polar error according to the new solved result
    + The user should now adjust the altitude and azimuth of the mount during the loop until precise enough polar alignment is reached
    + By left clicking on a star, the visual indicators will follow the star for each incremental adjustment  
* Step 7  
    + Once the window is closed the instruction will finish and is complete

## What do I need for the procedure to run?

* Camera has to be connected and ready
* A goto mount that can move along the right ascension axis via its ASCOM driver has to be connected
  + Alternatively a manual mode can be enabled to manually move the mount along the right ascension axis
* Platesolving must be setup (Astrometry.NET is not supported as primary solver for this, as it is too slow)

## The mount and camera are both connected, but the button is greyed out to run it in auto mode, why?

* The automatic mode requires the mount to move along the right ascension axis. To achieve this a special method from ASCOM is used ["MoveAxis"](https://ascom-standards.org/Help/Platform/html/M_ASCOM_DeviceInterface_ITelescopeV3_MoveAxis.htm), which is different from standard slews.
* When the N E S W buttons in the telescope tab are greyed out the driver reports via ["CanMoveAxis"](https://ascom-standards.org/Help/Platform/html/M_ASCOM_DeviceInterface_ITelescopeV3_CanMoveAxis.htm) that the mount is incapable of using the "MoveAxis" method and thus making it impossible to run the automatic mode.
* Reach out to your mount vendor to enhance the driver (or for EQMOD users - disable strict conformance mode in the driver setup)  
* Until then you can use the **manual mode** instead

## How does manual mode work exactly?

The manual mode is targeted for mount drivers that can't use the MoveAxis command or can't connect to the application in general.  
For the manual mode to work properly follow these instructions:

1. If possible connect to your mount, so solving can have reference coordinates and does not need the blind solver. If you have no telescope connection, make sure your blind solver is setup.  
2. Enable the `Manual Mode` toggle  
3. Slew to the starting position where you want to start the polar alignment procedure  
4. Enable tracking of your mount  
5. Click start   
6. The polar alignment procedure will take the first measurement point.  
7. After the first point you will be asked to `Move the mount along Right Ascension (RA) axis`. The total amount of movement required will depend on your `Measure Point Distance` set   
8. During moving the mount, the polar alignment procedure will constantly solve and check how far you have already moved  
9. When moved enough for the second point the procedure will automatically transition to getting the third point. This third step will work exactly like step 6 & 7  
10. After moving enough for the third point, there is a 10 second cooldown period, where you should not move the mount any further. After this cooldown period the final point will be determined.  
11. Once all points are determined, the error adjustment will be displayed.  

## Is there a preferred direction to start the polar alignment process?

While the polar alignment in itself will work anywhere above your horizon, the further away you are from the celestial equator (which is at declination 0°) 
the less error prone the correction calculation will be, as things like tracking errors will be less pronounced then.  
There are also two additional locations, that should be avoided - azimuth exactly at 90° and 270° for the correction adjustments, as there the correction for altitude is impossible to calculate.

## What does setting xyz do?

**Default [setting]**  
All settings starting with *default* are the initial settings where the values inside the instructions will be pre-populated from  
**Default Move Rate**  
The rate at which the telescope should be moved between points. Typically this is in degrees / seconds, but some mount vendors implement the move rate in a different way. If your mount differs from the expected rate, you might need to adjust the [*Axis move timeout factor*]  
**Default East Direction**  
Defines if the direction for the second and third point should be done by moving the mount in east or west direction along the RA axis  
*Hint: Some mount drivers need their primary axis move direction reversed inside the telescope tab*  
**Default Target Distance**  
The distance between measure points  
**Default Search Radius**  
As platesolving should be quick, this is the search radius that should be used for your plate solver. It should be larger than your initial error, but not too large to get quick solves.  
**Axis move timeout factor**  
After ([Measure Point Distance] / [Telescope Move Rate] * [Axis move timeout factor]) seconds, the mount will stop moving to the next point as a failsafe. Only increase this if you get warnings, that the [Measure Point Distance] was not reached  
**Default azimuth offset from pole**  
The azimuth offset in degrees from the pole position to start from for the first point  
**Default altitude offset from pole**  
The altitude offset in degrees from the pole position to start from for the first point  
**Various Error Colors**  
Here you can adjust the error colors for the guide numbers that will show you the error amount  


## Do I need the guider or the main imaging camera for this to work?

All you need to have is a camera that can be connected to N.I.N.A. and correct settings for your focal length and camera pixel size.
In addition to that you need a plate solver setup (ASTAP is recommended here) to work with the combination.

## Do I need a goto mount?

With the plugin version 1.3.0.0 and above there is a toggle for "manual mode". There the mount will not be controlled, but instead the users needs to move the mount by themselves.  
The polar alignment will then start on the current position and will tell you when to move the mount to the second and third point. Ideally you enable tracking for the whole procedure.

## How do I start the polar alignment?

There are two ways to start it. From within an advanced sequence and directly inside the imaging tab.  
**Inside advanced sequence**: Just drag the `Three Point Polar Alignment` instruction to the location inside your advanced sequence where you think it will fit best. Once the instruction is executed a new window will appear that will guide you through the process.  
**From imaging tab**: Open the panel from the available tools on the top right corner. A new panel will appear that will guide you through the process.