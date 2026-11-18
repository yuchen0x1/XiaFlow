using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace XiaFlow.App.Views
{
    public partial class PreviewDialog : Window
    {
        public PreviewDialog(string fileName, string content, bool isText, string? imagePath = null)
        {
            InitializeComponent();
            FileTitle.Text = fileName + "  （只读预览 · 不执行压缩包内文件）";
            if (isText)
            {
                ContentText.Text = content;
            }
            else if (imagePath != null)
            {
                ContentText.Visibility = Visibility.Collapsed;
                PreviewImage.Source = new BitmapImage(new Uri(imagePath));
                PreviewImage.Visibility = Visibility.Visible;
            }
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
