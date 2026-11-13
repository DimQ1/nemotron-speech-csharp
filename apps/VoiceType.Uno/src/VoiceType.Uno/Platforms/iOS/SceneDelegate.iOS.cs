using Foundation;
using Uno.UI.Runtime.AppleUIKit;

namespace VoiceType.Uno.iOS;

/// <summary>
/// UIKit scene lifecycle (required when building with the iOS 27 SDK, error MT7199): Info.plist names this class in
/// <c>UIApplicationSceneManifest</c>; Uno 7 drives the window through it.
/// </summary>
[Register("SceneDelegate")]
public class SceneDelegate : UnoUISceneDelegate
{
}
