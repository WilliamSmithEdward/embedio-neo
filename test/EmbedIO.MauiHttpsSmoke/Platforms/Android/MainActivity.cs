using Android.App;
using Android.Content.PM;

namespace EmbedIO.MauiHttpsSmoke;

[Activity(Name = "io.embedioneo.https.MainActivity", Theme = "@style/Maui.MainTheme",
    MainLauncher = true, Exported = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
        ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity { }
