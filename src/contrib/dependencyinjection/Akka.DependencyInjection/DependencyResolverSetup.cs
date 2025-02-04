﻿//-----------------------------------------------------------------------
// <copyright file="DependencyResolverSetup.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using Akka.Actor;
using Akka.Actor.Setup;

namespace Akka.DependencyInjection
{
    /// <summary>
    /// Used to help bootstrap an <see cref="ActorSystem"/> with dependency injection (DI)
    /// support via a <see cref="IServiceProvider"/> reference.
    ///
    /// The <see cref="IServiceProvider"/> will be used to access previously registered services
    /// in the creation of actors and other pieces of infrastructure inside Akka.NET.
    ///
    /// The constructor is internal. Please use <see cref="Create"/> to create a new instance.
    /// </summary>
    [Obsolete("Used DependencyResolverSetup instead.")]
    public class ServiceProviderSetup : Setup
    {
        internal ServiceProviderSetup(IServiceProvider serviceProvider)
        {
            ServiceProvider = serviceProvider;
        }

        public IServiceProvider ServiceProvider { get; }

        public static ServiceProviderSetup Create(IServiceProvider provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));

            return new ServiceProviderSetup(provider);
        }
    }

    /// <summary>
    /// Used to help bootstrap an <see cref="ActorSystem"/> with dependency injection (DI)
    /// support via a <see cref="IDependencyResolver"/> reference.
    ///
    /// The <see cref="IDependencyResolver"/> will be used to access previously registered services
    /// in the creation of actors and other pieces of infrastructure inside Akka.NET.
    ///
    /// The constructor is internal. Please use <see cref="Create(IServiceProvider)"/> to create a new instance.
    /// </summary>
    public class DependencyResolverSetup : Setup
    {
        public IDependencyResolver DependencyResolver { get; }

        internal DependencyResolverSetup(IDependencyResolver dependencyResolver)
        {
            DependencyResolver = dependencyResolver;
        }

        /// <summary>
        /// Creates a new instance of DependencyResolverSetup, passing in <see cref="IServiceProvider"/>
        /// here creates an <see cref="IDependencyResolver"/> that resolves dependencies from the specified <see cref="IServiceProvider"/>
        /// </summary>
        public static DependencyResolverSetup Create(IServiceProvider provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));

            return new DependencyResolverSetup(new ServiceProviderDependencyResolver(provider));
        }
        
        /// <summary>
        /// Creates a new instance of DependencyResolverSetup, an implementation of  <see cref="IDependencyResolver"/>
        /// can be passed in here to resolve services from test or alternative DI frameworks.
        /// </summary>
        public static DependencyResolverSetup Create(IDependencyResolver provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));

            return new DependencyResolverSetup(provider);
        }
    }
}
