using System.Windows;
using System.Windows.Controls;
using GlDrive.Config;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

[Collection("WpfRender")]
public class RecoveryPassphraseTests
{
    [Theory]
    [InlineData("fixture passphrase", "fixture passphrasE", false)]
    [InlineData("fixture passphrase", "", false)]
    [InlineData("short", "short", false)]
    [InlineData("fixture passphrase", "fixture passphrase", true)]
    public async Task ExportRequiresMatchingConfirmationAndShowsActionableError(string phrase, string confirmation, bool valid)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // Set the resource assembly before the first Window initializes WPF's
                // pack-resource cache; never instantiate the production Application.
                typeof(Application).GetField("_resourceAssembly", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .SetValue(null, typeof(App).Assembly);
                var window = new RecoveryCenterWindow(new AppConfig());
                ((PasswordBox)window.FindName("PassphraseBox")).Password = phrase;
                ((PasswordBox)window.FindName("ConfirmPassphraseBox")).Password = confirmation;
                Assert.Equal(valid, window.ValidateExportPassphrase());
                if (!valid)
                {
                    ((Button)window.FindName("ExportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Contains("confirmation", ((TextBlock)window.FindName("StatusText")).Text);
                }
                window.Close();
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task;
    }
}
