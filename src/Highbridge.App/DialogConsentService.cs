using Highbridge.Consent;

namespace Highbridge.App;

/// <summary>Shows <see cref="ConsentForm"/> on the UI thread; cancellation closes the dialog as a denial.</summary>
internal sealed class DialogConsentService(SynchronizationContext ui) : IConsentService
{
    public Task<bool> RequestPairingAsync(PairingRequest request, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(
            _ =>
            {
                var form = ConsentForm.ForPairing(request);
                var registration = cancellationToken.Register(() => ui.Post(_ => form.Close(), null));
                form.FormClosed += (_, _) =>
                {
                    registration.Dispose();
                    tcs.TrySetResult(form.Approved);
                    form.Dispose();
                };
                form.Show();
                form.Activate();
            },
            null);
        return tcs.Task;
    }
}
