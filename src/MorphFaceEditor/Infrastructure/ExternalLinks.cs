using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using MorphFaceEditor.Views;

namespace MorphFaceEditor.Infrastructure;

internal static class ExternalLinks
{
    internal const string NexusMods = "https://www.nexusmods.com/masseffectlegendaryedition/mods/3477";
    internal const string GitHub = "https://github.com/AudemusN7/LEBioMorphFaceEditor";
    internal const string Donate = "https://www.paypal.com/donate?token=NLX-3_M0wwiSrq92BIZGd-4mn9O9QLf5lbD5M_2wMAIXr5ZZTmunRNU3J9_4IyIPrOaN_-FOI_btIkzo&locale.x=US";

    internal static void Open(Window owner, string url) => Open(owner, new Uri(url, UriKind.Absolute));

    internal static void Open(Window owner, Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("External links must use an absolute HTTPS URL.", nameof(url));

        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            AppLog.Error("BROWSER:OPEN-FAILED", exception);
            _ = new EditorMessageWindow(Loc.Text("loc_ExternalLinks_UnavailableTitle"),
                Loc.Format("loc_ExternalLinks_CouldNotOpen", url.AbsoluteUri)) { Owner = owner }.ShowDialog();
        }
    }
}
