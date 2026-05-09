using System;
using Avalonia.Threading;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

public class AvaloniaThreading : IThreading
{
    public void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
