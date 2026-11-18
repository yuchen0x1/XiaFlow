using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace XiaFlow.App.Views
{
    /// <summary>拖放接收区（文件 + 文件夹）。</summary>
    public partial class DropZone : UserControl
    {
        public event Action<string[]>? FilesDropped;

        public string Hint
        {
            get => HintText.Text;
            set => HintText.Text = value;
        }

        public string SubHint
        {
            get => SubText.Text;
            set => SubText.Text = value;
        }

        public DropZone()
        {
            InitializeComponent();
            AllowDrop = true;
            DragOver += OnDragOver;
            DragEnter += OnDragEnter;
            DragLeave += OnDragLeave;
            Drop += OnDrop;
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var anim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(120));
            bd.BeginAnimation(OpacityProperty, anim);
            bd.BorderBrush = (System.Windows.Media.Brush)FindResource("Brush.Accent");
            bd.Background = (System.Windows.Media.Brush)FindResource("Brush.AccentSoft");
        }

        void OnDragLeave(object sender, DragEventArgs e)
        {
            var anim = new DoubleAnimation(0.9, TimeSpan.FromMilliseconds(160));
            bd.BeginAnimation(OpacityProperty, anim);
            bd.BorderBrush = (System.Windows.Media.Brush)FindResource("Brush.BorderStrong");
            bd.Background = (System.Windows.Media.Brush)FindResource("Brush.Bg1");
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            OnDragLeave(sender, e);
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            if (paths.Length > 0) FilesDropped?.Invoke(paths.Where(p => FileOrDirExists(p)).ToArray());
        }

        static bool FileOrDirExists(string p) =>
            System.IO.File.Exists(p) || System.IO.Directory.Exists(p);
    }
}
