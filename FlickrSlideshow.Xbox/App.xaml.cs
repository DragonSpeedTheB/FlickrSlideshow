using Microsoft.UI.Xaml;

namespace FlickrSlideshow.Xbox
{
    public partial class App : Application
    {
        private MainWindow? _window;

        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();
        }
    }
}
