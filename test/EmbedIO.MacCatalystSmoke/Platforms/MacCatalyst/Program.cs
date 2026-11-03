using Foundation;
using UIKit;

namespace EmbedIO.MacCatalystSmoke;

public static class Program
{
    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

// UIKit stops an app built with the 27 SDK at launch unless it has adopted the scene lifecycle (Info.plist names this).
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiApp.CreateBuilder().UseMauiApp<SmokeApp>().Build();
}
