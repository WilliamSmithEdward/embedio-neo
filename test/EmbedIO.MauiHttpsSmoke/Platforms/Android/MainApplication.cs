using Android.App;
using Android.Runtime;

namespace EmbedIO.MauiHttpsSmoke;

[Application]
public sealed class MainApplication : MauiApplication
{
    public MainApplication(nint handle, JniHandleOwnership ownership) : base(handle, ownership) { }
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
