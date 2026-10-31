using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Xandrite.Presentation;

namespace Xandrite.App;

/// <summary>About Xandrite: the logo, the version, and how the app is kept up to date.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow(AboutInfo about)
    {
        InitializeComponent();
        Logo.Source = LargestIcon();
        VersionText.Text = "Version " + about.Version;
        DescriptionText.Text = about.Description;
        UpdatesText.Text = about.Updates;
        LinkText.Text = about.Link;
        Link.NavigateUri = new Uri(about.Link);
    }

    /// <summary>The biggest image in Xandrite.ico (Image would otherwise take the first, 16 pixels, and blur it).</summary>
    private static BitmapFrame? LargestIcon()
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Xandrite.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.MaxBy(f => f.PixelWidth);
        }
        catch (Exception e) when (e is IOException or NotSupportedException)
        {
            return null;
        }
    }

    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // no browser: nothing to do
        }
        e.Handled = true;
    }
}
