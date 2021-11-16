using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("Three Point Polar Alignment")]
[assembly: AssemblyDescription("Three Point Polar Alignment almost anywhere in the sky")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Stefan Berg")]
[assembly: AssemblyProduct("NINA.Plugins")]
[assembly: AssemblyCopyright("Copyright ©  2021")]
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
[assembly: AssemblyVersion("1.5.2.1")]
[assembly: AssemblyFileVersion("1.5.2.1")]

//The minimum Version of N.I.N.A. that this plugin is compatible with
[assembly: AssemblyMetadata("MinimumApplicationVersion", "2.0.0.2001")]

//Your plugin homepage - omit if not applicaple
[assembly: AssemblyMetadata("Homepage", "https://www.patreon.com/stefanberg/")]
//The license your plugin code is using
[assembly: AssemblyMetadata("License", "MPL-2.0")]
//The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
//The repository where your pluggin is hosted
[assembly: AssemblyMetadata("Repository", "https://bitbucket.org/Isbeorn/nina.plugin.polaralignment/")]

[assembly: AssemblyMetadata("ChangelogURL", "https://bitbucket.org/Isbeorn/nina.plugin.polaralignment/src/master/PolarAlignment/Changelog.md")]

//Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "Polar alignment,Sequencer")]

//The featured logo that will be displayed in the plugin list next to the name
[assembly: AssemblyMetadata("FeaturedImageURL", "https://bitbucket.org/Isbeorn/nina.plugin.polaralignment/downloads/logo.png")]
//An example screenshot of your plugin in action
[assembly: AssemblyMetadata("ScreenshotURL", "https://bitbucket.org/Isbeorn/nina.plugin.polaralignment/downloads/Starlock2.png")]
//An additional example screenshot of your plugin in action
[assembly: AssemblyMetadata("AltScreenshotURL", "https://bitbucket.org/Isbeorn/nina.plugin.polaralignment/downloads/Imaging.png")]
[assembly: AssemblyMetadata("LongDescription", @"Three Point Auto Polar Alignment almost anywhere in the sky  

A new instruction will be available for the advanced sequencer as well as a new tool pane inside the imaging tab that will assist in polar alignment.  

When the instruction is called, a new window will be visible, that will guide you through the process.   
Inside the imaging tab there will be a button inside the tool pane to show the polar alignment assistant with parameters and a button to start the process.  

[*Frequently Asked Questions*](https://bitbucket.org/Isbeorn/nina.plugin.polaralignment/src/master/PolarAlignment/FAQ.md)

*Prerequisites*  
* Camera has to be connected and ready
* A goto mount that can move along the right ascension axis via its ASCOM driver has to be connected
    + Alternatively a manual mode can be enabled to manually move the mount along the right ascension axis
    + Manual mode works with either a connected telescope or without any telescope connection at all
* Platesolving must be setup (Astrometry.NET is not supported as primary solver for this, as it is too slow)

This method will use platesolving in combination with mount and camera control to automatically determine the polar alignment error.")]
