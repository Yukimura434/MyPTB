using System;
using System.Windows;
namespace PhotoBooth.Admin.UI.Views
{
    public partial class CollectionManagerWindow : Window
    {
        public CollectionManagerWindow()
        {
            InitializeComponent();
            MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 80);
            Loaded += (_, __) => NewNameBox.Focus();
        }
    }
}
