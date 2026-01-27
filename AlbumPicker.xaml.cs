using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FlickrSlideshow
{
    // ViewModel for each album in the checklist
    public class AlbumItem : INotifyPropertyChanged
    {
        public string Title { get; set; }
        public string Id { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class AlbumPicker : Window
    {
        private List<AlbumItem> _allAlbums;
        private ICollectionView _collectionView;

        public List<FlickrAlbum> SelectedAlbums { get; private set; } = new();

        public AlbumPicker(List<FlickrAlbum> albums)
        {
            InitializeComponent();

            // Convert to AlbumItem for binding to CheckBoxes
            _allAlbums = albums
                .Select(a => new AlbumItem { Title = a.Title, Id = a.Id, IsSelected = false }) // pre-check none
                .OrderBy(a => a.Title)
                .ToList();

            AlbumList.ItemsSource = _allAlbums;

            // Make sure selection is only controlled by CheckBoxes
            AlbumList.SelectionChanged += (s, e) => AlbumList.SelectedItems.Clear();

            _collectionView = CollectionViewSource.GetDefaultView(_allAlbums);
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = FilterBox.Text?.Trim().ToLower() ?? "";

            if (string.IsNullOrEmpty(filter))
            {
                _collectionView.Filter = null;
            }
            else
            {
                _collectionView.Filter = obj =>
                {
                    if (obj is AlbumItem album)
                        return album.Title.ToLower().Contains(filter);
                    return false;
                };
            }
            _collectionView.Refresh();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            SelectedAlbums = _allAlbums
                .Where(a => a.IsSelected)
                .Select(a => new FlickrAlbum(a.Id, a.Title))
                .ToList();

            if (!SelectedAlbums.Any())
            {
                MessageBox.Show("No albums selected! Please check at least one.");
                return;
            }
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
