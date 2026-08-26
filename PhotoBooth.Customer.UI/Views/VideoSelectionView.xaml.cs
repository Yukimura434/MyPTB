using System;
using System.Windows;
using System.Windows.Controls;
namespace PhotoBooth.Customer.UI.Views { public partial class VideoSelectionView : UserControl { public VideoSelectionView() { InitializeComponent(); } private void PreviewMediaLoaded(object sender,RoutedEventArgs e){var media=sender as MediaElement;if(media?.Source!=null)media.Play();} private void PreviewMediaEnded(object sender,RoutedEventArgs e){var media=sender as MediaElement;if(media==null)return;media.Position=TimeSpan.Zero;media.Play();} } }
