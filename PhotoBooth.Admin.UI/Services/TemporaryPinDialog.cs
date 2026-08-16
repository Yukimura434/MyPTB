using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PhotoBooth.Admin.UI.Services
{
    internal sealed class TemporaryPinDialog : Window
    {
        readonly PasswordBox pin = new PasswordBox();
        readonly PasswordBox confirmation = new PasswordBox();
        readonly TextBlock error = new TextBlock();
        readonly bool creating;
        readonly string expectedPin;

        TemporaryPinDialog(Window owner, bool create, string expected)
        {
            creating = create;
            expectedPin = expected;
            Owner = owner;
            Title = create ? "Tạo mã PIN tạm" : "Nhập mã PIN quản trị";
            Width = 430;
            Height = create ? 430 : 350;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(39, 39, 42));
            Foreground = Brushes.White;
            ShowInTaskbar = false;

            var content = new StackPanel { Margin = new Thickness(28) };
            content.Children.Add(new TextBlock
            {
                Text = create
                    ? "Tạo mã PIN gồm đúng 6 chữ số cho phiên làm việc này. PIN sẽ mất khi đóng ứng dụng."
                    : "Nhập mã PIN 6 số đã tạo để quay về Admin.",
                FontSize = 17,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 18)
            });

            AddPinField(content, "Mã PIN", pin);
            if (create) AddPinField(content, "Nhập lại mã PIN", confirmation);

            error.Foreground = new SolidColorBrush(Color.FromRgb(255, 138, 128));
            error.Margin = new Thickness(0, 8, 0, 0);
            error.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(error);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var cancel = CreateButton("Hủy", Color.FromRgb(90, 82, 88));
            cancel.IsCancel = true;
            cancel.Click += (s, e) => { DialogResult = false; };
            var accept = CreateButton(create ? "Tạo PIN" : "Mở Admin", Color.FromRgb(79, 140, 255));
            accept.IsDefault = true;
            accept.Click += Accept;
            buttons.Children.Add(cancel);
            buttons.Children.Add(accept);
            content.Children.Add(buttons);

            Content = content;
            Loaded += (s, e) => pin.Focus();
        }

        public string CreatedPin { get; private set; }

        public static string Create(Window owner)
        {
            var dialog = new TemporaryPinDialog(owner, true, null);
            return dialog.ShowDialog() == true ? dialog.CreatedPin : null;
        }

        public static bool Verify(Window owner, string expectedPin)
        {
            var dialog = new TemporaryPinDialog(owner, false, expectedPin);
            return dialog.ShowDialog() == true;
        }

        void Accept(object sender, RoutedEventArgs e)
        {
            var value = pin.Password ?? string.Empty;
            if (value.Length != 6 || !value.All(char.IsDigit))
            {
                error.Text = "Mã PIN phải gồm đúng 6 chữ số.";
                pin.SelectAll();
                pin.Focus();
                return;
            }

            if (creating)
            {
                if (!string.Equals(value, confirmation.Password, StringComparison.Ordinal))
                {
                    error.Text = "Hai mã PIN không trùng nhau.";
                    confirmation.SelectAll();
                    confirmation.Focus();
                    return;
                }
                CreatedPin = value;
            }
            else if (!string.Equals(value, expectedPin, StringComparison.Ordinal))
            {
                error.Text = "Mã PIN không đúng.";
                pin.SelectAll();
                pin.Focus();
                return;
            }

            DialogResult = true;
        }

        static void AddPinField(Panel parent, string label, PasswordBox field)
        {
            parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 4) });
            field.MaxLength = 6;
            field.FontSize = 22;
            field.Height = 42;
            field.Padding = new Thickness(8, 4, 8, 4);
            field.Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            field.Foreground = Brushes.White;
            field.BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 84));
            parent.Children.Add(field);
        }

        static Button CreateButton(string text, Color background)
        {
            return new Button
            {
                Content = text,
                MinWidth = 105,
                Height = 40,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(14, 6, 14, 6),
                Background = new SolidColorBrush(background),
                Foreground = Brushes.White
            };
        }
    }
}
