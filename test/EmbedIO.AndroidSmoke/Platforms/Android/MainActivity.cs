using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace EmbedIO.AndroidSmoke;

[Activity(Name = "io.embedioneo.smoke597.MainActivity", Theme = "@style/Maui.MainTheme",
    MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
        ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? state)
    {
        Interlocked.Increment(ref SmokeHost.Created);
        base.OnCreate(state);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Interlocked.Increment(ref SmokeHost.Resumed);
    }

    protected override void OnStop()
    {
        Interlocked.Increment(ref SmokeHost.Stopped);
        base.OnStop();
    }

    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration configuration)
    {
        base.OnConfigurationChanged(configuration);
        Interlocked.Increment(ref SmokeHost.Configured);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        switch (intent?.GetStringExtra("smoke_action"))
        {
            case "recreate": Recreate(); break;
            case "dispose-backend": _ = SmokeHost.Instance.StopBackendObservedAsync(); break;
            case "restart": _ = SmokeHost.Instance.RestartObservedAsync(); break;
        }
    }
}
