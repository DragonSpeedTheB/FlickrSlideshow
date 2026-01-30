using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FlickrSlideshow
{
    // ViewModel for each album/collection in the checklist
    public class AlbumItem : INotifyPropertyChanged
    {
        public string Title { get; set; }
        public string Id { get; set; }

        // mark whether this entry represents a collection (not a single photoset)
        public bool IsCollection { get; set; }

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

        // store collections passed in so we can expand them into albums on OK
        private List<FlickrCollection> _collections = new();

        public List<FlickrAlbum> SelectedAlbums { get; private set; } = new();

        // accept both albums and optional collections
        public AlbumPicker(List<FlickrAlbum> albums, List<FlickrCollection>? collections = null)
        {
            InitializeComponent();

            _collections = collections ?? new List<FlickrCollection>();

            // Convert to AlbumItem for binding to CheckBoxes
            // include collections first (so they appear visually distinct and near the top)
            var items = new List<AlbumItem>();

            // add collections as items (IsCollection = true)
            foreach (var c in _collections.OrderBy(c => c.Title))
            {
                items.Add(new AlbumItem { Title = c.Title, Id = c.Id, IsSelected = false, IsCollection = true });
            }

            // add albums (IsCollection = false)
            items.AddRange(albums
                .Select(a => new AlbumItem { Title = a.Title, Id = a.Id, IsSelected = false, IsCollection = false })
                .OrderBy(a => a.Title));

            _allAlbums = items.ToList();

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
            var selectedItems = _allAlbums.Where(a => a.IsSelected).ToList();

            var result = new List<FlickrAlbum>();

            // Add explicit album selections
            foreach (var it in selectedItems.Where(i => !i.IsCollection))
            {
                result.Add(new FlickrAlbum(it.Id, it.Title));
            }

            // Expand any selected collections into their albums
            foreach (var collItem in selectedItems.Where(i => i.IsCollection))
            {
                var coll = _collections.FirstOrDefault(c => c.Id == collItem.Id);
                if (coll != null)
                {
                    foreach (var album in coll.Albums)
                    {
                        // avoid duplicates
                        if (!result.Any(r => r.Id == album.Id))
                            result.Add(album);
                    }
                }
            }

            SelectedAlbums = result;

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
