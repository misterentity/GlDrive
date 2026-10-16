using System.Runtime.CompilerServices;

// Existing public names remain resolvable through GlDrive after moving their implementation.
[assembly: TypeForwardedTo(typeof(GlDrive.Player.SourceAvailabilityPolicy))]
[assembly: TypeForwardedTo(typeof(GlDrive.Downloads.ExecutableExtensions))]
[assembly: TypeForwardedTo(typeof(GlDrive.Downloads.SceneNameParser))]
[assembly: TypeForwardedTo(typeof(GlDrive.Downloads.QualityProfile))]
[assembly: TypeForwardedTo(typeof(GlDrive.Downloads.ParsedRelease))]
