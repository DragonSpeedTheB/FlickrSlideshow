using System;
using System.IO;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace FlickrSlideshow.Xbox
{
    sealed partial class App : Application
    {
        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
            this.UnhandledException += OnUnhandledException;
        }

        private void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Log("UNHANDLED: " + e.Exception?.ToString());
            e.Handled = true;
            ShowErrorOnScreen("Unhandled: " + e.Exception?.GetType().Name + "\n" + e.Exception?.Message);
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Log("OnLaunched");
            try
            {
                if (!(Window.Current.Content is Frame rootFrame))
                {
                    rootFrame = new Frame();
                    rootFrame.NavigationFailed += OnNavigationFailed;
                    Window.Current.Content = rootFrame;
                }

                if (e.PrelaunchActivated == false)
                {
                    if (rootFrame.Content == null)
                    {
                        Log("Navigating to MainPage");
                        bool ok = rootFrame.Navigate(typeof(MainPage), e.Arguments);
                        Log("Navigate returned: " + ok);
                        if (!ok)
                            ShowErrorOnScreen("Navigate(MainPage) returned false.\nCheck MainPage.xaml for errors.");
                    }
                    Window.Current.Activate();
                    Log("Activated");
                }
            }
            catch (Exception ex)
            {
                Log("OnLaunched THREW: " + ex);
                ShowErrorOnScreen("OnLaunched threw:\n" + ex.GetType().Name + "\n" + ex.Message);
            }
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            Log("NavigationFailed: " + e.SourcePageType?.FullName + " ex=" + e.Exception);
            ShowErrorOnScreen("NavigationFailed: " + e.SourcePageType?.Name
                + "\n" + e.Exception?.GetType().Name
                + "\n" + e.Exception?.Message);
            e.Handled = true;
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            e.SuspendingOperation.GetDeferral().Complete();
        }

        // ── Helpers ──────────────────────────────────────────────────────

        internal static void ShowErrorOnScreen(string message)
        {
            try
            {
                var tb = new TextBlock
                {
                    Text = message,
                    Foreground = new SolidColorBrush(Colors.Red),
                    FontSize = 26,
                    TextWrapping = Windows.UI.Xaml.TextWrapping.Wrap,
                    Margin = new Windows.UI.Xaml.Thickness(80),
                    VerticalAlignment = Windows.UI.Xaml.VerticalAlignment.Center
                };
                var grid = new Grid { Background = new SolidColorBrush(Colors.Black) };
                grid.Children.Add(tb);
                Window.Current.Content = grid;
                Window.Current.Activate();
            }
            catch { }
        }

        internal static void Log(string msg)
        {
            try
            {
                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "startup.log");
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}