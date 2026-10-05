namespace EmbedIO.AndroidSmoke;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp.CreateBuilder().UseMauiApp<SmokeApp>().Build();
}

public sealed class SmokeApp : Application
{
    public SmokeApp() => SmokeHost.Instance.Start();
    protected override Window CreateWindow(IActivationState? state) =>
        new(new ContentPage { Content = new Label { Text = "EmbedIO Android lifecycle smoke" } });
}
