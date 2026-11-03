using FluentCode.Motion;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode;

public sealed partial class MainWindow
{
    internal void VerifyStartupMotion(System.Action completed, System.Action<System.Exception> failed)
    {
        ContentFrame.Navigate(typeof(Pages.OpenCodePage), null, FluentMotion.PageEntrance());
        // A chip can receive reposition motion before its Loaded entrance completes.
        // Native WinUI rejects XAML Scale if any helper has handed out its Visual.
        var chip = new Grid { Width = 32, Height = 32 };
        FluentMotion.PressScale(chip);
        chip.Loaded += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                FluentMotion.Settle(chip);
                FluentMotion.ChipEntrance(chip);
                FluentMotion.EnableImplicitOffset(chip);
                // Exercise XAML's implicit Scale animation even if OS animations are disabled
                // on the test machine (only this disposable chip is affected).
                chip.ScaleTransition = new Microsoft.UI.Xaml.Vector3Transition { Duration = System.TimeSpan.FromMilliseconds(83) };
                chip.Scale = new System.Numerics.Vector3(0.8f, 0.8f, 1f);
                _ = chip.Scale;
                var compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
                var scale = compositor.CreateVector3KeyFrameAnimation();
                scale.Target = "Scale";
                scale.InsertKeyFrame(1f, System.Numerics.Vector3.One);
                scale.Duration = System.TimeSpan.FromMilliseconds(83);
                chip.StartAnimation(scale);
                chip.StopAnimation(scale);
                FluentMotion.Settle(chip);
                _ = chip.Scale;
                chip.StartAnimation(scale);
                chip.StopAnimation(scale);
                chip.Scale = new System.Numerics.Vector3(0.9f, 0.9f, 1f);
                FluentMotion.ScaleFadeIn(chip, 0.8f);
                chip.Scale = System.Numerics.Vector3.One;
                completed();
            }
            catch (System.Exception ex) { failed(ex); }
        });
        RootElement.Children.Add(chip);
    }
}

public sealed partial class MainWindow
{
    // Opt-in (FC_TRANSCRIPT_INPUT_TEST=1): real OpenCodePage transcript hit-test / selection / menu surface checks.
    internal async System.Threading.Tasks.Task<int> VerifyTranscriptInputAsync(string evidence)
    {
        // Fresh isolated profiles start on Welcome with the Frame collapsed; realize the actual page first.
        _welcomeExitTimer?.Stop();
        HideWelcome();
        ContentFrame.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        ContentFrame.Navigate(typeof(Pages.OpenCodePage));
        var until = System.DateTime.UtcNow.AddSeconds(15);
        while (ContentFrame.Content is not Pages.OpenCodePage && System.DateTime.UtcNow < until) await System.Threading.Tasks.Task.Delay(100);
        if (ContentFrame.Content is not Pages.OpenCodePage page)
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(evidence, "transcript-input.txt"), "FAIL OpenCodePage not shown");
            return 1;
        }
        return await page.VerifyTranscriptInputAsync(evidence);
    }
}
