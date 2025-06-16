using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
//[assembly: AssemblyTitle("Three Point Polar Alignment")]
//[assembly: AssemblyDescription("Three Point Polar Alignment almost anywhere in the sky")]
//[assembly: AssemblyConfiguration("")]
//[assembly: AssemblyCompany("Stefan Berg")]
//[assembly: AssemblyProduct("NINA.Plugins")]
[assembly: AssemblyCopyright("Copyright ©  2021-2025")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("1de8d7d3-f11e-494c-a371-95cb48dffa18")]

//The assembly versioning
//Should be incremented for each new release build of a plugin
//[assembly: AssemblyVersion("2.2.3.4")]
//[assembly: AssemblyFileVersion("2.2.3.4")]

//The minimum Version of N.I.N.A. that this plugin is compatible with
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.1.2.9001")]

//Your plugin homepage - omit if not applicaple
[assembly: AssemblyMetadata("Homepage", "https://www.patreon.com/stefanberg/")]
//The license your plugin code is using
[assembly: AssemblyMetadata("License", "MPL-2.0")]
//The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
//The repository where your pluggin is hosted
[assembly: AssemblyMetadata("Repository", "https://github.com/isbeorn/nina.plugin.polaralignment")]

[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/isbeorn/nina.plugin.polaralignment/blob/master/PolarAlignment/Changelog.md")]

//Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "Polar alignment,Sequencer")]

//The featured logo that will be displayed in the plugin list next to the name
[assembly: AssemblyMetadata("FeaturedImageURL", "https://github.com/isbeorn/nina.plugin.polaralignment/blob/master/PolarAlignment/logo.png?raw=true")]
//An example screenshot of your plugin in action
[assembly: AssemblyMetadata("ScreenshotURL", "https://github.com/isbeorn/nina.plugin.polaralignment/blob/master/PolarAlignment/Starlock2.png?raw=true")]
//An additional example screenshot of your plugin in action
[assembly: AssemblyMetadata("AltScreenshotURL", "https://github.com/isbeorn/nina.plugin.polaralignment/blob/master/PolarAlignment/Imaging.png?raw=true")]
[assembly: AssemblyMetadata("LongDescription", @"Three Point Polar Alignment almost anywhere in the sky  

A new instruction will be available for the advanced sequencer as well as a new tool pane inside the imaging tab that will assist in polar alignment.  

When the instruction is called from within the sequencer, a new window will be visible, that will guide you through the process.   
Inside the imaging tab there will be a button inside the tool pane to show the polar alignment assistant with parameters and a button to start the process.  

[*Frequently Asked Questions*](https://github.com/isbeorn/nina.plugin.polaralignment/blob/master/PolarAlignment/FAQ.md)

*Prerequisites*  
* Latitude and Longitude has to be set in options. You can use [www.latlong.net](https://www.latlong.net/) to easily determine your location.
* Camera has to be connected and ready
* A goto mount that can move along the right ascension axis using one of three methods:
    + Fully Automated - Requires the mount to be connected via its ASCOM driver and the tool will move the mount
    + Manual mode with mount connected via its ASCOM driver - You need to move the mount via the hand controls or via the mount software, but leave the clutches engaged
    + Manual mode without the mount being connected - You need to manually move the mount either by hand controller or by loosening the clutches
    
* Platesolving must be setup (Astrometry.NET is not supported as primary solver for this, as it is too slow)
    + When using manual mode and there is no mount connected, the blind solver will be used

This method will use platesolving in combination with mount and camera control to automatically determine the polar alignment error.")]
