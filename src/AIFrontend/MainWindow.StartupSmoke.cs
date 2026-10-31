using AIFrontend.Motion;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend;

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
