using System;
using System.Windows;

namespace XiaFlow.App.Views
{
    /// <summary>简单文本输入对话框。</summary>
    public partial class InputDialog : Window
    {
        public string Value => ValueBox.Text;

        InputDialog(string title, string message, string initial)
        {
            InitializeComponent();
            TitleText.Text = title;
            MsgText.Text = message;
            ValueBox.Text = initial;
            ValueBox.SelectAll();
            ValueBox.Focus();
        }

        public static string? Ask(Window? owner, string title, string message, string initial = "")
        {
            var dlg = new InputDialog(title, message, initial);
            if (owner != null) dlg.Owner = owner;
            return dlg.ShowDialog() == true ? dlg.Value : null;
        }

        void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
