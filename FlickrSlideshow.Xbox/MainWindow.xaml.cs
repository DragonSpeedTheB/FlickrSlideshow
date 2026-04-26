using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.System;
using FlickrSlideshow.Core;

namespace FlickrSlideshow.Xbox
{
    public sealed partial class MainWindow : Window
    {
        private readonly SlideshowViewModel _vm;

        public MainWindow()
        {
            this.InitializeComponent();

            // Go full-screen on Xbox (presenter fills the display)
            this.AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);

            _vm = new SlideshowViewModel();
            _vm.ImageReady += OnImageReady;

            // Handle gamepad / keyboard navigation
            RootGrid.KeyDown += OnKeyDown;
            RootGrid.Focus(FocusState.Programmatic);

            // Load last user and start slideshow
            _ = _vm.InitializeAsync();
        }

        private void OnImageReady(BitmapImage bitmap, string title)
        {
            SlideImage.Source = bitmap;
            CaptionText.Text = title;

            var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromSeconds(2) };
            var storyboard = new Storyboard();
            Storyboard.SetTarget(fade, SlideImage);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
            storyboard.Begin();
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case VirtualKey.GamepadDPadRight:
                case VirtualKey.Right:
                    _vm.Next();
                    e.Handled = true;
                    break;

                case VirtualKey.GamepadDPadLeft:
                case VirtualKey.Left:
                    _vm.Previous();
                    e.Handled = true;
                    break;

                case VirtualKey.GamepadA:
                case VirtualKey.Space:
                    _vm.TogglePause();
                    PauseOverlay.Visibility = _vm.IsPaused
                        ? Visibility.Visible : Visibility.Collapsed;
                    e.Handled = true;
                    break;

                case VirtualKey.GamepadB:
                case VirtualKey.Escape:
                    // On Xbox B / Escape resume if paused, otherwise quit
                    if (_vm.IsPaused)
                    {
                        _vm.TogglePause();
                        PauseOverlay.Visibility = Visibility.Collapsed;
                    }
                    e.Handled = true;
                    break;
            }
        }
    }
}
