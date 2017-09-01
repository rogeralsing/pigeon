﻿//-----------------------------------------------------------------------
// <copyright file="Props.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Akka.Dispatch;
using Akka.Util.Internal;
using Akka.Util.Reflection;
using Akka.Routing;
using Akka.Util;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace Akka.Actor
{
    /// <summary>
    /// While <see cref="Props"/> are descriptors used for actor construction,
    /// <see cref="IScope"/> is used to actually create actors in scope of a particualr <see cref="IActorContext"/>.
    /// </summary>
    internal interface IScope : IDisposable
    {
        ActorBase Create();
    }

    /// <summary>
    /// This class represents a configuration object used in creating an <see cref="Akka.Actor.ActorBase">actor</see>.
    /// It is immutable and thus thread-safe.
    /// <example>
    /// <code>
    ///   private Props props = Props.Empty();
    ///   private Props props = Props.Create(() => new MyActor(arg1, arg2));
    /// 
    ///   private Props otherProps = props.WithDispatcher("dispatcher-id");
    ///   private Props otherProps = props.WithDeploy(deployment info);
    /// </code>
    /// </example>
    /// </summary>
    public abstract class Props : IEquatable<Props>, ISurrogated
    {
        #region internal classes

        private sealed class EmptyActor : UntypedActor
        {
            protected override void OnReceive(object message)
            {
            }
        }

        #endregion

        private static readonly object[] noArgs = { };

        /// <summary>
        /// A pre-configured <see cref="Akka.Actor.Props"/> that creates an actor that doesn't respond to messages.
        /// </summary>
        public static Props Empty { get; } = Props.Create(() => new EmptyActor());

        /// <summary>
        /// A pre-configured <see cref="Akka.Actor.Props"/> that doesn't create actors.
        /// 
        /// <note>
        /// The value of this field is null.
        /// </note>
        /// </summary>
        public static Props None { get; } = null;

        protected Props(Type type, object[] args, Deploy deploy, SupervisorStrategy supervisorStrategy)
        {
            Type = type ?? throw new ArgumentException("Props must be instantiated with an actor type.", nameof(type));
            Deploy = deploy ?? Deploy.None;
            Arguments = args;
            SupervisorStrategy = supervisorStrategy;
        }

        /// <summary>
        /// The type of the actor that is created.
        /// </summary>
        [JsonIgnore]
        public Type Type { get; }

        /// <summary>
        /// Arguments supplied for type construction.
        /// </summary>
        public object[] Arguments { get; }

        /// <summary>
        /// The configuration used to deploy the actor.
        /// </summary>
        public Deploy Deploy { get; }

        /// <summary>
        /// The supervisor strategy used to manage the actor.
        /// </summary>
        public SupervisorStrategy SupervisorStrategy { get; }

        /// <summary>
        /// Creates an actor using a specified lambda expression.
        /// </summary>
        /// <typeparam name="TActor">The type of the actor to create.</typeparam>
        /// <param name="factory">The lambda expression used to create the actor.</param>
        /// <param name="supervisorStrategy">Optional: The supervisor strategy used to manage the actor.</param>
        /// <returns>The newly created <see cref="Akka.Actor.Props" />.</returns>
        /// <exception cref="ArgumentException">The create function must be a 'new T (args)' expression</exception>
        public static Props Create<TActor>(Expression<Func<TActor>> factory, SupervisorStrategy supervisorStrategy = null) where TActor : ActorBase
        {
            var newExpression = factory.Body.AsInstanceOf<NewExpression>();
            if (newExpression == null)
                throw new ArgumentException("The create function must be a 'new T (args)' expression");

            var args = newExpression.GetArguments().ToArray();

            return new StaticProps(typeof(TActor), args, Deploy.None, supervisorStrategy);
        }

        public RouterConfig RouterConfig => Deploy.RouterConfig;
        public string Dispatcher => Deploy.Dispatcher;
        public string Mailbox => Deploy.Mailbox;

        /// <summary>
        /// Creates an actor using the given arguments.
        /// </summary>
        /// <typeparam name="TActor">The type of the actor to create.</typeparam>
        /// <param name="args">The arguments needed to create the actor.</param>
        /// <returns>The newly created <see cref="Akka.Actor.Props" />.</returns>
        public static Props Create<TActor>(object[] args) where TActor : ActorBase => new StaticProps(typeof(TActor), args, Deploy.None, null);

        /// <summary>
        /// Creates an actor using a specified supervisor strategy.
        /// </summary>
        /// <typeparam name="TActor">The type of the actor to create.</typeparam>
        /// <param name="supervisorStrategy">The supervisor strategy used to manage the actor.</param>
        /// <returns>The newly created <see cref="Akka.Actor.Props" />.</returns>
        public static Props Create<TActor>(SupervisorStrategy supervisorStrategy = null) where TActor : ActorBase, new() => new DynamicProps(typeof(TActor), Deploy.None, supervisorStrategy);

        /// <summary>
        /// Creates an actor of a specified type.
        /// </summary>
        /// <param name="type">The type of the actor to create.</param>
        /// <param name="args">The arguments needed to create the actor.</param>
        /// <returns>The newly created <see cref="Akka.Actor.Props" />.</returns>
        /// <exception cref="ArgumentNullException">Props must be instantiated with an actor type.</exception>
        public static Props Create(Type type, params object[] args) => new StaticProps(type, args, Deploy.None, null);

        /// <summary>
        /// Creates a new <see cref="Akka.Actor.Props" /> with a given <paramref name="mailbox" />.
        /// 
        /// <note>
        /// This method is immutable and returns a new instance of <see cref="Akka.Actor.Props" />.
        /// </note>
        /// </summary>
        /// <param name="mailbox">The mailbox used when deploying the actor.</param>
        /// <returns>A new <see cref="Akka.Actor.Props" /> with the provided <paramref name="mailbox" />.</returns>
        public Props WithMailbox(string mailbox) => Copy(deploy: Deploy.WithMailbox(mailbox));

        /// <summary>
        /// Creates a new <see cref="Akka.Actor.Props" /> with a given <paramref name="dispatcher" />.
        /// 
        /// <note>
        /// This method is immutable and returns a new instance of <see cref="Akka.Actor.Props" />.
        /// </note>
        /// </summary>
        /// <param name="dispatcher">The dispatcher used when deploying the actor.</param>
        /// <returns>A new <see cref="Akka.Actor.Props" /> with the provided <paramref name="dispatcher" />.</returns>
        public Props WithDispatcher(string dispatcher) => Copy(deploy: Deploy.WithDispatcher(dispatcher));

        /// <summary>
        /// Creates a new <see cref="Akka.Actor.Props" /> with a given router.
        /// 
        /// <note>
        /// This method is immutable and returns a new instance of <see cref="Akka.Actor.Props" />.
        /// </note>
        /// </summary>
        /// <param name="routerConfig">The router used when deploying the actor.</param>
        /// <returns>A new <see cref="Akka.Actor.Props" /> with the provided <paramref name="routerConfig" />.</returns>
        public Props WithRouter(RouterConfig routerConfig) => Copy(deploy: Deploy.WithRouterConfig(routerConfig));

        /// <summary>
        /// Creates a new <see cref="Akka.Actor.Props" /> with a given deployment configuration.
        ///
        /// <note>
        /// This method is immutable and returns a new instance of <see cref="Akka.Actor.Props" />.
        /// </note>
        /// </summary>
        /// <param name="deploy">The configuration used to deploy the actor.</param>
        /// <returns>A new <see cref="Akka.Actor.Props" /> with the provided <paramref name="deploy" />.</returns>
        public Props WithDeploy(Deploy deploy) =>
            // TODO: this is a hack designed to preserve explicit router deployments https://github.com/akkadotnet/akka.net/issues/546
            // in reality, we should be able to do copy.Deploy = deploy.WithFallback(copy.Deploy); but that blows up at the moment
            // - Aaron Stannard
            Copy(deploy: deploy.WithFallback(Deploy));

        ///  <summary>
        ///  Creates a new <see cref="Akka.Actor.Props" /> with a given supervisor strategy.
        /// 
        ///  <note>
        ///  This method is immutable and returns a new instance of <see cref="Akka.Actor.Props" />.
        ///  </note>
        ///  </summary>
        ///  <param name="supervisorStrategy">The supervisor strategy used to manage the actor.</param>
        /// <returns>A new <see cref="Akka.Actor.Props" /> with the provided <paramref name="supervisorStrategy" />.</returns>
        public Props WithSupervisorStrategy(SupervisorStrategy supervisorStrategy) => Copy(supervisorStrategy: supervisorStrategy);

        internal abstract IScope CreateScope(ExtendedActorSystem system);

        internal abstract Props Copy(Type type = null, object[] args = null, Deploy deploy = null, SupervisorStrategy supervisorStrategy = null);

        #region surrogates

        /// <summary>
        /// Creates a surrogate representation of the current <see cref="Props"/>.
        /// </summary>
        /// <param name="system">The actor system that owns this router.</param>
        /// <returns>The surrogate representation of the current <see cref="Props"/>.</returns>
        public abstract ISurrogate ToSurrogate(ActorSystem system);

        #endregion

        #region equality operators

        /// <summary>
        /// Indicates whether the current object is equal to another object of the same type.
        /// </summary>
        /// <param name="other">An object to compare with this object.</param>
        /// <returns>
        /// <c>true</c> if the current object is equal to the <paramref name="other" /> parameter; otherwise, <c>false</c>.
        /// </returns>
        public bool Equals(Props other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            if (GetType() != other.GetType()) return false;

            return Deploy.Equals(other.Deploy)
                   && CompareArguments(other)
                   && Type == other.Type;
        }

        private bool CompareArguments(Props other)
        {
            if (ReferenceEquals(Arguments, other.Arguments)) return true;
            if (ReferenceEquals(Arguments, null)) return false;
            if (ReferenceEquals(other.Arguments, null)) return false;

            //TODO: since arguments can be serialized, we can not compare by ref
            //arguments may also not implement equality operators, so we can not structurally compare either
            //we can not just call a serializer and compare outputs either, since different args may require diff serializer mechanics

            return Arguments.Length == other.Arguments.Length;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is Props props && Equals(props);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = (Deploy != null ? Deploy.GetHashCode() : 0);
                //  hashCode = (hashCode*397) ^ (SupervisorStrategy != null ? SupervisorStrategy.GetHashCode() : 0);
                //  hashCode = (hashCode*397) ^ (Arguments != null ? Arguments.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (Type?.GetHashCode() ?? 0);
                return hashCode;
            }
        }

        #endregion
    }

    internal sealed class StaticProps : Props
    {
        #region internal classes

        /// <summary>
        /// This class represents a surrogate of a <see cref="Props"/> configuration object.
        /// Its main use is to help during the serialization process.
        /// </summary>
        public sealed class StaticPropsSurrogate : ISurrogate
        {
            /// <summary>
            /// The type of actor to create
            /// </summary>
            public Type Type { get; set; }
            /// <summary>
            /// The configuration used to deploy the actor.
            /// </summary>
            public Deploy Deploy { get; set; }
            /// <summary>
            /// The arguments used to create the actor.
            /// </summary>
            public object[] Arguments { get; set; }

            /// <summary>
            /// Creates a <see cref="Props"/> encapsulated by this surrogate.
            /// </summary>
            /// <param name="system">The actor system that owns this router.</param>
            /// <returns>The <see cref="Props"/> encapsulated by this surrogate.</returns>
            public ISurrogated FromSurrogate(ActorSystem system) => new StaticProps(Type, Arguments, Deploy, null);
        }

        public sealed class StaticScope : IScope
        {
            private readonly StaticProps _props;

            public StaticScope(StaticProps props)
            {
                _props = props;
            }

            public ActorBase Create() => (ActorBase)Activator.CreateInstance(_props.Type, _props.Arguments);

            public void Dispose() { }
        }

        #endregion

        private readonly IScope _scope;

        public StaticProps(Type type, object[] args, Deploy deploy, SupervisorStrategy supervisorStrategy) : base(type, args, deploy, supervisorStrategy)
        {
            _scope = new StaticScope(this);
        }

        internal override IScope CreateScope(ExtendedActorSystem system) => _scope;

        internal override Props Copy(Type type = null, object[] args = null, Deploy deploy = null, SupervisorStrategy supervisorStrategy = null) => new StaticProps(
            type: type ?? Type,
            args: args ?? Arguments,
            deploy: deploy ?? Deploy,
            supervisorStrategy: supervisorStrategy ?? SupervisorStrategy);

        public override ISurrogate ToSurrogate(ActorSystem system) => new StaticPropsSurrogate
        {
            Type = Type,
            Arguments = Arguments,
            Deploy = Deploy
        };
    }

    internal sealed class DynamicProps : Props
    {
        #region internal classes

        public sealed class DynamicPropsSurrogate : ISurrogate
        {
            /// <summary>
            /// The type of actor to create
            /// </summary>
            public Type Type { get; set; }

            /// <summary>
            /// The configuration used to deploy the actor.
            /// </summary>
            public Deploy Deploy { get; set; }

            /// <summary>
            /// Creates a <see cref="Props"/> encapsulated by this surrogate.
            /// </summary>
            /// <param name="system">The actor system that owns this router.</param>
            /// <returns>The <see cref="Props"/> encapsulated by this surrogate.</returns>
            public ISurrogated FromSurrogate(ActorSystem system) => new DynamicProps(Type, Deploy, null);
        }

        public sealed class DynamicScope : IScope
        {
            private readonly DynamicProps _props;
            private readonly IServiceScope _inner;

            public DynamicScope(DynamicProps props, IServiceScope inner)
            {
                _props = props;
                _inner = inner;
            }

            public void Dispose() => _inner.Dispose();

            public ActorBase Create() => (ActorBase)_inner.ServiceProvider.GetService(_props.Type);
        }

        #endregion

        public DynamicProps(Type type, Deploy deploy, SupervisorStrategy supervisorStrategy) : base(type, null, deploy, supervisorStrategy)
        {
        }

        internal override IScope CreateScope(ExtendedActorSystem system) => new DynamicScope(this, system.ServiceProvider.CreateScope());

        internal override Props Copy(Type type = null, object[] args = null, Deploy deploy = null, SupervisorStrategy supervisorStrategy = null) =>
            new DynamicProps(type: type ?? Type, deploy: deploy ?? Deploy, supervisorStrategy: supervisorStrategy ?? SupervisorStrategy);

        public override ISurrogate ToSurrogate(ActorSystem system) => new DynamicPropsSurrogate
        {
            Type = Type,
            Deploy = Deploy
        };
    }
}

