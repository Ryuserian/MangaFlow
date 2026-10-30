using System.Reactive.Disposables;
using ReactiveUI.Primitives.Disposables;

namespace MangaTranslator.App;

public static class RxMixins
{
    public static T DisposeWith<T>(this T subscription, MultipleDisposable bag) where T : IDisposable
    {
        bag.Add(subscription);
        return subscription;
    }

    public static T DisposeWith<T>(this T subscription, CompositeDisposable bag) where T : IDisposable
    {
        bag.Add(subscription);
        return subscription;
    }

    public static T DisposeWith<T>(this T subscription, ICollection<IDisposable> bag) where T : IDisposable
    {
        bag.Add(subscription);
        return subscription;
    }
}
