using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoBooth.Customer.UI.ViewModels;
namespace PhotoBooth.Customer.UI.Views
{
    public partial class BoothLoginView : UserControl
    {
        bool keyboard;
        public BoothLoginView()
        {
            InitializeComponent(); PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { if (keyboard) { HideKeyboard_Click(s, e); e.Handled = true; } } }; Loaded += (s, e) => Layout(); SizeChanged += (s, e) => Layout(); Unloaded += (s, e) => { keyboard = false; KeyboardPanel.Visibility = Visibility.Collapsed; };
            foreach (var letters in new[] { "1234567890", "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM⌫✓" }) { var row = new System.Windows.Controls.Primitives.UniformGrid { Columns = letters.Length, Margin = new Thickness(0, 3, 0, 3) }; foreach (var letter in letters) { var key = new Button { Content = letter == '✓' ? "Xong" : letter.ToString(), Tag = letter, Focusable = false, FontSize = 16, Padding = new Thickness(6), Margin = new Thickness(3, 0, 3, 0), MinHeight = 43, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 49, 63)), BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 117, 139)) }; key.Click += Key_Click; row.Children.Add(key); } Keys.Children.Add(row); }
        }
        void Layout()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return; bool portrait = ActualHeight > ActualWidth, small = portrait && ActualWidth <= 650, shortWide = !portrait && ActualHeight <= 850;
            HeaderRow.Height = new GridLength(portrait ? (small ? 72 : 86) : 80); PortraitShade.Visibility = portrait ? Visibility.Visible : Visibility.Collapsed; PortraitBackdrop.Visibility = portrait ? Visibility.Visible : Visibility.Collapsed; Story.Visibility = portrait ? Visibility.Collapsed : Visibility.Visible; StoryColumn.Width = new GridLength(portrait ? 0 : 1, portrait ? GridUnitType.Pixel : GridUnitType.Star); StoryGap.Width = new GridLength(portrait ? 0 : ActualWidth * (shortWide ? .04 : .07)); LoginBody.Margin = new Thickness(portrait ? (small ? 24 : ActualWidth * .08) : ActualWidth * (shortWide ? .05 : .07), keyboard ? (portrait ? (small ? 14 : 22) : 12) : portrait ? (small ? 28 : 50) : (shortWide ? 26 : 32), portrait ? (small ? 24 : ActualWidth * .08) : ActualWidth * (shortWide ? .05 : .07), keyboard ? (portrait ? (small ? 14 : 22) : 12) : portrait ? (small ? 28 : 50) : (shortWide ? 26 : 32));
            LoginCard.MaxWidth = portrait ? 610 : 540; LoginCard.Padding = new Thickness(portrait ? (keyboard ? (small ? 22 : 28) : (small ? 28 : 42)) : 0); LoginCard.Background = portrait ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(245, 27, 23, 34)) : Brushes.Transparent; LoginCard.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(117, 98, 127)); LoginCard.BorderThickness = new Thickness(portrait ? 1 : 0);
            Title.FontSize = keyboard ? (portrait ? (small ? 28 : 34) : 26) : portrait ? (small ? 36 : 46) : (shortWide ? 37 : 46); Intro.FontSize = portrait ? (small ? 16 : 19) : (shortWide ? 16 : 18); Intro.LineHeight = Intro.FontSize * 1.65; SubmitButton.FontSize = small ? 15 : 17; Intro.Visibility = Tagline.Visibility = keyboard && shortWide ? Visibility.Collapsed : Visibility.Visible; CodeLabel.Margin = new Thickness(0, keyboard ? (portrait ? 20 : 10) : portrait ? (small ? 25 : 36) : 30, 0, 10); CodeBox.Height = keyboard ? (portrait ? 72 : 58) : portrait ? (small ? 78 : 92) : 84; SubmitButton.Height = keyboard ? (small || shortWide ? 50 : 62) : portrait ? 66 : 62; Helper.Margin = new Thickness(0, keyboard ? 6 : 12, 0, keyboard ? 8 : 24); Assist.Margin = new Thickness(0, portrait ? 32 : 22, 0, 0); Assist.Visibility = keyboard ? Visibility.Collapsed : Visibility.Visible;
            foreach (var row in Keys.Children) { if (row is System.Windows.Controls.Primitives.UniformGrid grid) foreach (Button key in grid.Children) key.MinHeight = portrait ? (small ? 44 : 53) : (shortWide ? 39 : 43); }
        }
        void ShowKeyboard() { if (!(DataContext is BoothLoginViewModel vm) || !vm.IsCodeMode) return; keyboard = true; KeyboardPanel.Visibility = Visibility.Visible; Layout(); }
        void Code_Focus(object s, KeyboardFocusChangedEventArgs e) => ShowKeyboard(); void Code_Touch(object s, TouchEventArgs e) => ShowKeyboard();
        void HideKeyboard_Click(object s, RoutedEventArgs e) { keyboard = false; KeyboardPanel.Visibility = Visibility.Collapsed; Keyboard.ClearFocus(); Layout(); }
        void Key_Click(object s, RoutedEventArgs e) { char key = (char)((Button)s).Tag; if (key == '✓') { HideKeyboard_Click(s, e); return; } CodeBox.Focus(); var text = CodeBox.Text ?? ""; int start = CodeBox.SelectionStart, length = CodeBox.SelectionLength; if (key == '⌫') { if (length > 0) text = text.Remove(start, length); else if (start > 0) { text = text.Remove(start - 1, 1); start--; } } else { if (length > 0) text = text.Remove(start, length); if (text.Length < 8) { text = text.Insert(start, key.ToString()); start++; } } CodeBox.Text = text; CodeBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); CodeBox.CaretIndex = Math.Min(start, text.Length); }
        void EmployeePassword_Changed(object s, RoutedEventArgs e) { if (DataContext is BoothLoginViewModel vm) vm.Password = EmployeePassword.Password; }
        void EventLogin_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is BoothLoginViewModel vm) || vm.IsBusy) return;
            HideKeyboard_Click(sender, e);
            EmployeePassword.Clear();
            vm.IsCodeMode = !vm.IsCodeMode;
            if (vm.IsEmployeeMode) AccountName.Focus();
            else SubmitButton.Focus();
        }
    }
}
