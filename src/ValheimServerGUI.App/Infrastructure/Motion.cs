using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ValheimServerGUI.App.Controls;

namespace ValheimServerGUI.App.Infrastructure;

/// <summary>
/// Code-driven entrance animations: the main window's page fades and rises in when a sidebar page is picked, and
/// its dashboard cards follow one after the other. Only opacity and a render-transform offset move, so nothing is
/// laid out again while it plays. Hover, press and state transitions live in the styles (Styles/*.axaml).
/// </summary>
public static class Motion
{
    private static readonly TimeSpan PageDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan CardDuration = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan CardStagger = TimeSpan.FromMilliseconds(40);

    // Past this many cards the rest come in with the last one, so a long page never waits on its tail.
    private const int MaxStaggeredCards = 8;

    /// <summary>
    /// Plays the page entrance on <paramref name="page"/>: a short fade and rise, then the cards inside it in turn.
    /// Returns the token source of the run; cancel it before starting the next page so a quick click does not
    /// leave two runs on the same page.
    /// </summary>
    public static CancellationTokenSource PlayPageEnter(Visual page)
    {
        var cts = new CancellationTokenSource();
        _ = FadeRise(PageDuration, 6, TimeSpan.Zero).RunAsync(page, cts.Token);

        // The new page's content is attached on the next layout pass; stagger its cards once it is there.
        Dispatcher.UIThread.Post(() =>
        {
            if (cts.IsCancellationRequested) return;
            var cards = page.GetVisualDescendants().OfType<DashboardCard>().ToList();
            for (var i = 0; i < cards.Count; i++)
            {
                var delay = CardStagger * Math.Min(i, MaxStaggeredCards);
                _ = FadeRise(CardDuration, 10, delay).RunAsync(cards[i], cts.Token);
            }
        }, DispatcherPriority.Loaded);

        return cts;
    }

    // Opacity 0 → 1 while the element slides up from `offset` px. FillMode.Backward holds the first frame during
    // the delay, so a staggered card stays hidden until its turn instead of flashing in first.
    private static Animation FadeRise(TimeSpan duration, double offset, TimeSpan delay) => new()
    {
        Duration = duration,
        Delay = delay,
        Easing = new CubicEaseOut(),
        FillMode = FillMode.Backward,
        Children =
        {
            new KeyFrame
            {
                Cue = new Cue(0),
                Setters =
                {
                    new Setter(Visual.OpacityProperty, 0d),
                    new Setter(TranslateTransform.YProperty, offset),
                },
            },
            new KeyFrame
            {
                Cue = new Cue(1),
                Setters =
                {
                    new Setter(Visual.OpacityProperty, 1d),
                    new Setter(TranslateTransform.YProperty, 0d),
                },
            },
        },
    };
}
