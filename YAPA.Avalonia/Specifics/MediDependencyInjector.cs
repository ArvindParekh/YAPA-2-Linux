using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

/// <summary>
/// IDependencyInjector backed by Microsoft.Extensions.DependencyInjection.
///
/// Supports post-build registration (needed by ThemeManager and PluginManager) by
/// materialising all currently-resolved singleton instances before rebuilding the
/// provider, so they are not re-created.
/// </summary>
public class MediDependencyInjector : IDependencyInjector, IDisposable
{
    private IServiceCollection _services;
    private IServiceProvider? _provider;
    private readonly object _lock = new();

    public MediDependencyInjector(IServiceCollection services)
    {
        _services = services;
    }

    public void Build()
    {
        lock (_lock)
        {
            _provider = _services.BuildServiceProvider();
        }
    }

    public object Resolve(Type type)
    {
        lock (_lock)
        {
            _provider ??= _services.BuildServiceProvider();
            return _provider.GetRequiredService(type);
        }
    }

    public void Register(Type type, bool singleInstance = false)
    {
        lock (_lock)
        {
            SnapshotAndRebuild(c =>
            {
                if (singleInstance) c.AddSingleton(type);
                else c.AddTransient(type);
            });
        }
    }

    public void RegisterInstance(object instance, Type? asType = null)
    {
        lock (_lock)
        {
            var t = asType ?? instance.GetType();
            SnapshotAndRebuild(c => c.AddSingleton(t, instance));
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            (_provider as IDisposable)?.Dispose();
            _provider = null;
        }
    }

    // Replaces previously-resolved singleton descriptors with their concrete instances so
    // they survive the provider rebuild, then adds the new registration and rebuilds.
    private void SnapshotAndRebuild(Action<IServiceCollection> addNew)
    {
        IServiceCollection newServices = new ServiceCollection();

        if (_provider != null)
        {
            foreach (var descriptor in _services)
            {
                if (descriptor.Lifetime == ServiceLifetime.Singleton)
                {
                    try
                    {
                        var instance = _provider.GetService(descriptor.ServiceType);
                        if (instance != null)
                        {
                            newServices.AddSingleton(descriptor.ServiceType, instance);
                            continue;
                        }
                    }
                    catch { /* unresolvable type – preserve original descriptor */ }
                }
                newServices.Add(descriptor);
            }
        }
        else
        {
            foreach (var d in _services)
                newServices.Add(d);
        }

        addNew(newServices);
        _services = newServices;
        _provider = newServices.BuildServiceProvider();
    }
}
