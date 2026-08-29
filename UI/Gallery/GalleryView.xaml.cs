using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Backtrack.UI.Gallery;

public partial class GalleryView : UserControl
{
    public GalleryView()
    {
        InitializeComponent();
    }

    private MainWindow? Main => MainWindow.Instance ?? Window.GetWindow(this) as MainWindow;

    private void BackToIdle_Click(object sender, RoutedEventArgs e) => Main?.BackToIdle_Click(sender, e);
    private void GallerySortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => Main?.GallerySortComboBox_SelectionChanged(sender, e);
    private void GalleryFilterBox_TextChanged(object sender, TextChangedEventArgs e) => Main?.GalleryFilterBox_TextChanged(sender, e);
    private void GalleryLocalTab_Click(object sender, RoutedEventArgs e) => Main?.GalleryLocalTab_Click(sender, e);
    private void GalleryRemoteTab_Click(object sender, RoutedEventArgs e) => Main?.GalleryRemoteTab_Click(sender, e);
    private void GalleryUp_Click(object sender, RoutedEventArgs e) => Main?.GalleryUp_Click(sender, e);
    private void GalleryBackButton_DragEnter(object sender, DragEventArgs e) => Main?.GalleryBackButton_DragEnter(sender, e);
    private void GalleryBackButton_DragOver(object sender, DragEventArgs e) => Main?.GalleryBackButton_DragOver(sender, e);
    private void GalleryBackButton_DragLeave(object sender, DragEventArgs e) => Main?.GalleryBackButton_DragLeave(sender, e);
    private void GalleryBackButton_Drop(object sender, DragEventArgs e) => Main?.GalleryBackButton_Drop(sender, e);
    private void MoveSelected_Click(object sender, RoutedEventArgs e) => Main?.MoveSelected_Click(sender, e);
    private void DeleteSelected_Click(object sender, RoutedEventArgs e) => Main?.DeleteSelected_Click(sender, e);
    private void CancelSelection_Click(object sender, RoutedEventArgs e) => Main?.CancelSelection_Click(sender, e);
    private void GalleryScrollHost_DragEnter(object sender, DragEventArgs e) => Main?.GalleryScrollHost_DragEnter(sender, e);
    private void GalleryScrollHost_DragOver(object sender, DragEventArgs e) => Main?.GalleryScrollHost_DragOver(sender, e);
    private void GalleryScrollHost_Drop(object sender, DragEventArgs e) => Main?.GalleryScrollHost_Drop(sender, e);
}
