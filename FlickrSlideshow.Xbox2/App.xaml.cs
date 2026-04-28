using System;
using System.IO;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace FlickrSlideshow.Xbox2;

sealed partial class App : Application
{
    public App()
    {
        this.InitializeComponent();
        this.Suspending += OnSuspending;
        this.UnhandledException += (s, e) =>
        {
            Log("UNHANDLED: " + e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        Log("OnLaunched");
        if (Window.Current.Content is not Frame rootFrame)
        {
            rootFrame = new Frame();
            rootFrame.NavigationFailed += (s, ex) => throw new Exception("Nav failed: " + ex.SourcePageType.FullName);
            Window.Current.Content = rootFrame;
        }

        if (e.PrelaunchActivated == false)
        {
            if (rootFrame.Content == null)
                rootFrame.Navigate(typeof(SettingsPage), e.Arguments);
            Window.Current.Activate();
        }
    }

    private void OnSuspending(object sender, SuspendingEventArgs e) =>
        e.SuspendingOperation.GetDeferral().Complete();

    internal static void Log(string msg)
    {
        try
        {
            var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "app.log");
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }
}
